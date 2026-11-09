using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElementalLegion.Server;

internal sealed record OnlineRoomInfo(string Id, string Title, int Count, int Capacity, int MinimumPlayers, bool Running, bool Persistent,
    bool PlayerHosted = false, bool PasswordRequired = false);
internal sealed record OnlineTicket(string RoomId, AccountIdentity Account, bool Resume, int? FreshElement, DateTimeOffset Expires);

/// <summary>방마다 독립된 월드와 세션을 소유하고 단일 틱 루프에서 게임 상태를 변경한다.</summary>
internal sealed class OnlineRooms : BackgroundService
{
    internal NetworkLoadTrace? LoadTrace { get; init; }
    internal const int MaxRooms = 4;
    private const int MaxTickets = 512;
    private const string PersistentWorldId = "00000000000000000000000000000001";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportRetention = TimeSpan.FromDays(7);
    private readonly object gate = new();
    private readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OnlineTicket> tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OnlinePeer> connected = new(StringComparer.Ordinal);
    private readonly Channel<StoredReport> reports = Channel.CreateBounded<StoredReport>(256);
    private readonly Channel<byte> hostedBackups = Channel.CreateBounded<byte>(1);
    private readonly GameRules rules;
    private readonly NetworkSettings settings;
    private readonly IAccountStore? accounts;
    private readonly IWorldStore? store;
    private readonly string? reportDirectory;
    private readonly TimeProvider clock;
    private readonly ILogger<OnlineRooms> logger;
    private readonly NetDataWriter writer = new();
    private bool stopping;

    private sealed class Room(string id, string title, int minimumPlayers, int capacity, string creator, DateTimeOffset deadline, NetworkSettings settings)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public int MinimumPlayers { get; } = minimumPlayers;
        public int Capacity { get; } = capacity;
        public string Creator { get; } = creator;
        public DateTimeOffset Deadline { get; } = deadline;
        public List<OnlinePeer> Peers { get; } = [];
        public Dictionary<string, int> Players { get; set; } = new(StringComparer.Ordinal);
        public NetworkWorld? World { get; set; }
        public ChatRouter Chat { get; } = new(settings);
        public Task<bool> SaveTask { get; set; } = Task.FromResult(true);
        public long NextCheckpointTick { get; set; }
        public bool Closing { get; set; }
        public bool Finished { get; set; }
        public bool Frozen { get; set; }
        public bool RequireBackup { get; set; }
        public bool InitialBackupPending { get; set; }
        public bool InitialBackupConfirmed { get; set; }
        public ulong BackupNumber { get; set; }
        public Queue<MigrationCapture> Captures { get; } = new();
        public Dictionary<string, MigrationMember> MigratedMembers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RestoredPeers { get; } = new(StringComparer.Ordinal);
        public List<int> AiColorIds { get; } = [];
        public List<AiDifficulty> AiDifficulties { get; } = [];
        public int AiCount => AiDifficulties.Count;
        public bool CloseQueued { get; set; }
        public DateTimeOffset NextCloseAttempt { get; set; }
        public bool Persistent => Id == PersistentWorldId;
        public OnlineRoomInfo Info => new(Id, Title, Peers.Count + RestoredPeers.Count + (World?.LivingAiCount ?? AiCount),
            Capacity, MinimumPlayers, World != null, Persistent);
    }

    private sealed record StoredReport(string RoomId, DateTimeOffset Time, string Reporter, int SenderPlayerId, string? SenderAccountId,
        ulong Id, string Text, string Reason);

    private sealed record MigrationCapture(ulong Generation, bool Freeze, string? LeavingAccount,
        TaskCompletionSource<HostMigrationSnapshot> Completion);

    internal Task<HostMigrationSnapshot> CaptureHostedMigration(string roomId, ulong generation, bool freeze = false,
        string? leavingAccountId = null)
    {
        lock (gate)
        {
            if (generation == 0 || !rooms.TryGetValue(roomId, out Room? room) || room.Closing || room.Finished || room.Captures.Count >= 2)
                return Task.FromException<HostMigrationSnapshot>(new InvalidOperationException("승계할 방을 찾을 수 없습니다."));
            var completion = new TaskCompletionSource<HostMigrationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            room.Captures.Enqueue(new MigrationCapture(generation, freeze, leavingAccountId, completion));
            return completion.Task;
        }
    }

    internal void ResumeHostedMigration(string roomId)
    {
        lock (gate)
            if (rooms.TryGetValue(roomId, out Room? room)) room.Frozen = false;
    }

    internal long HostedTick(string roomId)
    {
        lock (gate) return rooms.TryGetValue(roomId, out Room? room) ? room.World?.World.Metrics.Tick ?? 0 : 0;
    }

    internal bool HostedFrozen(string roomId)
    {
        lock (gate) return rooms.TryGetValue(roomId, out Room? room) && room.Frozen;
    }

    internal void RequireHostedBackups(string roomId)
    {
        lock (gate)
        {
            rooms[roomId].RequireBackup = true;
            hostedBackups.Writer.TryWrite(0);
        }
    }

    internal ValueTask<byte> WaitForHostedBackup(CancellationToken stop) => hostedBackups.Reader.ReadAsync(stop);

    internal void RequestHostedBackup() => hostedBackups.Writer.TryWrite(0);

    internal void ConfirmHostedBackup(string roomId, bool hasWorld)
    {
        lock (gate)
            if (hasWorld && rooms.TryGetValue(roomId, out Room? room)) room.InitialBackupConfirmed = true;
    }

    /// <summary>새 호스트의 틱을 시작하기 전에 인증된 현재 참가자와 월드를 연결한다.</summary>
    internal void RestoreHostedMigration(HostMigrationSnapshot snapshot, AccountIdentity creator,
        IReadOnlyCollection<string> connectedAccounts)
    {
        MigrationSnapshotCodec.Validate(snapshot, rules);
        if (!connectedAccounts.Contains(creator.Id) || connectedAccounts.Count > snapshot.Capacity ||
            connectedAccounts.Distinct(StringComparer.Ordinal).Count() != connectedAccounts.Count ||
            snapshot.World != null && !snapshot.Members.Any(member => member.AccountId == creator.Id))
            throw new InvalidDataException("새 호스트 또는 현재 참가자 정보가 잘못됐습니다.");
        lock (gate)
        {
            if (stopping || ExecuteTask != null || rooms.Count != 0 || store != null)
                throw new InvalidOperationException("승계 런타임이 이미 실행 중입니다.");
            var room = new Room(snapshot.RoomId, snapshot.Title, snapshot.MinimumPlayers, snapshot.Capacity,
                creator.Id, clock.GetUtcNow() + TimeSpan.FromSeconds(settings.MigrationCandidateSeconds), settings)
            {
                Players = new(snapshot.Players, StringComparer.Ordinal),
                BackupNumber = snapshot.BackupNumber, RequireBackup = true
            };
            room.AiDifficulties.AddRange(snapshot.AiDifficulties ?? Enumerable.Repeat(AiDifficulty.Normal, snapshot.AiCount).ToArray());
            if (snapshot.World == null && room.AiCount + connectedAccounts.Count > room.Capacity)
                room.AiDifficulties.RemoveRange(room.Capacity - connectedAccounts.Count,
                    room.AiCount - room.Capacity + connectedAccounts.Count);
            int[] colors = MigrationSnapshotCodec.ResolveColors(snapshot);
            room.AiColorIds.AddRange(colors.Skip(snapshot.Members.Length));
            if (room.AiColorIds.Count > room.AiCount) room.AiColorIds.RemoveRange(room.AiCount, room.AiColorIds.Count - room.AiCount);
            foreach (MigrationMember member in snapshot.Members.OrderBy(member => member.AccountId != creator.Id))
                if (snapshot.World != null || connectedAccounts.Contains(member.AccountId))
                    room.MigratedMembers.Add(member.AccountId, member with { ColorId = colors[Array.IndexOf(snapshot.Members, member)] });
            room.RestoredPeers.UnionWith(connectedAccounts);
            if (snapshot.World != null)
            {
                int[] active = connectedAccounts.Where(snapshot.Players.ContainsKey).Select(account => snapshot.Players[account]).ToArray();
                room.World = new NetworkWorld(rules, settings, snapshot.World, snapshot.NextPlayerId, snapshot.Ai,
                    active, snapshot.Players.Values.ToArray(), aiMatch: snapshot.AiCount > 0);
            }
            room.Chat.RestoreMigration(snapshot.Chat);
            rooms.Add(room.Id, room);
        }
    }

    /// <summary>준비 완료 후 늦게 끊긴 참가자에게 로그아웃을 적용하고 준비한 월드를 그대로 시작한다.</summary>
    internal void ReconcileHostedMigration(string roomId, AccountIdentity creator, IReadOnlyCollection<string> connectedAccounts)
    {
        if (!connectedAccounts.Contains(creator.Id) || !PlayerNickname.IsValid(creator.Nickname) ||
            connectedAccounts.Distinct(StringComparer.Ordinal).Count() != connectedAccounts.Count ||
            connectedAccounts.Any(account => !IsRoomId(account)))
            throw new InvalidDataException("승계 확정 참가자 정보가 잘못됐습니다.");
        lock (gate)
        {
            if (stopping || ExecuteTask != null || !rooms.TryGetValue(roomId, out Room? room) || room.Peers.Count != 0 ||
                room.Creator != creator.Id || !room.RestoredPeers.IsSupersetOf(connectedAccounts))
                throw new InvalidDataException("준비하지 않은 참가자나 방장으로 승계를 확정할 수 없습니다.");
            foreach (string account in room.RestoredPeers.Where(account => !connectedAccounts.Contains(account)).ToArray())
            {
                if (room.Players.TryGetValue(account, out int owner)) room.World?.Leave(owner);
                room.RestoredPeers.Remove(account);
                if (room.World == null) room.MigratedMembers.Remove(account);
            }
        }
    }

    public OnlineRooms(GameRules rules, NetworkSettings settings)
        : this(rules, settings, null, null, null, TimeProvider.System, NullLogger<OnlineRooms>.Instance, null) { }

    public OnlineRooms(GameRules rules, NetworkSettings settings, IAccountStore? accounts, IWorldStore? store,
        string? reportDirectory, TimeProvider clock, ILogger<OnlineRooms> logger, string? persistentWorldTitle)
    {
        this.rules = rules;
        this.settings = settings;
        this.accounts = accounts;
        this.store = store;
        this.reportDirectory = reportDirectory;
        this.clock = clock;
        this.logger = logger;
        if (store == null) return;
        foreach (string id in store.ListIds())
        {
            if (id == PersistentWorldId) continue;
            try
            {
                WorldSave? saved = store.Load(id);
                if (saved?.Title == null || saved.LanPort != 0) continue;
                if (!IsRoomId(id) || rooms.Count >= MaxRooms || saved.MinimumPlayers < 2 || saved.Capacity > 15 ||
                    !M5Protocol.IsSafeText(saved.Title, M5Protocol.TitleLimit)) throw new InvalidDataException("온라인 방 저장 형식 오류입니다.");
                var room = new Room(id, saved.Title, saved.MinimumPlayers, saved.Capacity, "", default, settings)
                {
                    World = new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId,
                        aiOwners: saved.AiOwners, aiDifficulties: saved.AiDifficulties),
                    Players = new Dictionary<string, int>(saved.Players, StringComparer.Ordinal)
                };
                room.AiDifficulties.AddRange(saved.AiDifficulties ?? Enumerable.Repeat(AiDifficulty.Normal, saved.AiOwners?.Length ?? 0).ToArray());
                if (saved.AiOwners != null)
                {
                    int[] colors = LobbyColors.Restore(saved.Players.Values.Concat(saved.AiOwners).Select(owner =>
                        room.World.World.TryGetView(owner, out WorldView view)
                            ? view.OwnerColors.Single(color => color.PlayerId == owner).ColorId : -1).ToArray());
                    room.AiColorIds.AddRange(colors.Skip(saved.Players.Count));
                }
                rooms.Add(id, room);
            }
            catch (InvalidDataException) { logger.LogWarning("온라인 방 {RoomId} 저장을 열 수 없습니다.", id); }
        }
        if (persistentWorldTitle != null) AddPersistentWorld(store, persistentWorldTitle);
    }

    private void AddPersistentWorld(IWorldStore worldStore, string title)
    {
        if (!M5Protocol.IsSafeText(title, M5Protocol.TitleLimit) || title != title.Trim())
            throw new ArgumentException("온라인 상시 월드 제목을 확인하세요.", nameof(title));
        if (rooms.Count >= MaxRooms)
        {
            logger.LogWarning("복원한 방이 운영 상한에 도달하여 온라인 상시 월드를 열 수 없습니다.");
            return;
        }
        try
        {
            WorldSave? saved = worldStore.Load(PersistentWorldId);
            if (saved?.IsRoom == true) throw new InvalidDataException("상시 월드의 저장 종류가 잘못됐습니다.");
            var room = new Room(PersistentWorldId, title, 1, Math.Min(15, rules.MaxPlayers), "", default, settings)
            {
                World = saved == null ? new NetworkWorld(rules, settings) : new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId),
                Players = saved == null ? new(StringComparer.Ordinal) : new(saved.Players, StringComparer.Ordinal)
            };
            rooms.Add(room.Id, room);
        }
        catch (InvalidDataException) { logger.LogWarning("온라인 상시 월드 저장을 열 수 없습니다."); }
    }

    public OnlineRoomInfo[] List()
    {
        lock (gate) return rooms.Values.Where(room => !room.Closing).Select(room => room.Info).ToArray();
    }

    internal int ReservedCount
    {
        get { lock (gate) return rooms.Count; }
    }

    internal bool PendingCreation(string accountId)
    {
        lock (gate) return HasPendingRoom(accountId);
    }

    public OnlineRoomInfo? Status(string roomId)
    {
        lock (gate) return rooms.TryGetValue(roomId, out Room? room) && !room.Closing ? room.Info : null;
    }

    internal static string? ValidateRoom(GameRules rules, string? title, int minimumPlayers, int capacity)
    {
        if (title == null || title.Any(char.IsControl)) return null;
        title = title.Trim();
        return M5Protocol.IsSafeText(title, M5Protocol.TitleLimit) && minimumPlayers >= 2 &&
            minimumPlayers <= capacity && capacity <= Math.Min(15, rules.MaxPlayers) ? title : null;
    }

    public string? Create(AccountIdentity account, string? title, int minimumPlayers, int capacity, out OnlineRoomInfo? created,
        string? hostedId = null)
    {
        created = null;
        title = ValidateRoom(rules, title, minimumPlayers, capacity);
        if (title == null || hostedId != null && (store != null || !IsRoomId(hostedId) || hostedId == PersistentWorldId))
            return "invalidRoom";
        lock (gate)
        {
            if (stopping) return "unavailable";
            if (connected.ContainsKey(account.Id) || HasPendingRoom(account.Id))
                return "alreadyInRoom";
            if (rooms.Count >= (store == null ? 1 : MaxRooms)) return "roomLimit";
            var room = new Room(hostedId ?? Guid.NewGuid().ToString("N"), title, minimumPlayers, capacity, account.Id,
                clock.GetUtcNow() + TicketLifetime, settings);
            rooms.Add(room.Id, room);
            created = room.Info;
            return null;
        }
    }

    public string? IssueTicket(string roomId, AccountIdentity account, int version, string? dataHash, int? startElement, out string? value)
    {
        value = null;
        if (version != Protocol.Version) return "versionMismatch";
        if (!string.Equals(dataHash, rules.DataHash, StringComparison.OrdinalIgnoreCase)) return "dataMismatch";
        lock (gate)
        {
            if (stopping) return "unavailable";
            string? error = CanEnter(roomId, account.Id, out Room? room);
            if (error != null) return error;
            bool resume = CanResume(room!, account.Id);
            int? fresh = room!.Persistent && !resume ? startElement ?? rules.StartingElements[0] : null;
            if (fresh.HasValue && !rules.StartingElements.Contains(fresh.Value)) return "invalidElement";
            DateTimeOffset now = clock.GetUtcNow();
            foreach (var previous in tickets.Where(entry => entry.Value.Expires <= now || entry.Value.Account.Id == account.Id).ToArray())
                tickets.Remove(previous.Key);
            if (tickets.Count >= MaxTickets) return "rateLimited";
            value = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(FileAccountStore.TokenBytes));
            tickets.Add(value, new OnlineTicket(roomId, account, resume, fresh, now + TicketLifetime));
            return null;
        }
    }

    public OnlineTicket? Redeem(string roomId, ReadOnlySpan<byte> value)
    {
        if (value.Length != FileAccountStore.TokenBytes) return null;
        lock (gate)
        {
            string key = Convert.ToHexStringLower(value);
            if (!tickets.TryGetValue(key, out OnlineTicket? ticket) || ticket.RoomId != roomId) return null;
            tickets.Remove(key);
            return ticket.Expires > clock.GetUtcNow() ? ticket : null;
        }
    }

    public string? Attach(OnlineTicket ticket, out OnlinePeer? peer)
    {
        peer = null;
        lock (gate)
        {
            if (stopping) return "unavailable";
            string? error = CanEnter(ticket.RoomId, ticket.Account.Id, out Room? room);
            if (error != null) return error;
            Room admitted = room!;
            if (ticket.Resume != CanResume(admitted, ticket.Account.Id) ||
                !ticket.Resume && (admitted.Persistent != ticket.FreshElement.HasValue || !admitted.Persistent && admitted.World != null))
                return "reentryUnavailable";
            peer = AttachPeer(admitted, ticket.Account, ticket.FreshElement ?? rules.StartingElements[0], ticket.FreshElement.HasValue);
            return null;
        }
    }

    /// <summary>중계에서 인증한 참가자의 현재 대기실 및 기존 플레이어 복귀 자격을 확인한다.</summary>
    public string? AttachHosted(string roomId, AccountIdentity account, int? startElement, out OnlinePeer? peer)
    {
        peer = null;
        if (startElement.HasValue && !rules.StartingElements.Contains(startElement.Value)) return "invalidElement";
        lock (gate)
        {
            if (stopping || store != null) return "unavailable";
            string? error = CanEnter(roomId, account.Id, out Room? room);
            if (error != null) return error;
            peer = AttachPeer(room!, account, startElement ?? rules.StartingElements[0], joinFresh: false);
            if (peer.Owner > 0) BeginGame(room!.World!, peer, peer.Owner, resumed: true);
            return null;
        }
    }

    private OnlinePeer AttachPeer(Room room, AccountIdentity account, int element, bool joinFresh)
    {
        var peer = new OnlinePeer(account, element) { JoinFresh = joinFresh };
        if (room.MigratedMembers.Remove(account.Id, out MigrationMember? member))
        {
            peer.Element = member.Element;
            peer.ColorId = member.ColorId;
            peer.Ready = member.Ready;
            if (room.Players.TryGetValue(account.Id, out int owner) && room.World?.HasMigrationConnection(owner) == true) peer.Owner = owner;
        }
        else if (room.World == null)
        {
            var humans = room.Peers.Select(other => other.ColorId).Concat(room.RestoredPeers.Where(room.MigratedMembers.ContainsKey).Select(id => room.MigratedMembers[id].ColorId));
            peer.ColorId = LobbyColors.FirstAvailable(humans, room.AiColorIds);
        }
        if (account.Id == room.Creator) room.Peers.Insert(0, peer);
        else room.Peers.Add(peer);
        room.RestoredPeers.Remove(account.Id);
        connected.Add(account.Id, peer);
        if (room.World == null) BroadcastLobby(room);
        return peer;
    }

    private string? CanEnter(string roomId, string accountId, out Room? room)
    {
        if (!rooms.TryGetValue(roomId, out room) || room.Closing || room.Finished ||
            room.World == null && room.Peers.Count == 0 && room.Deadline <= clock.GetUtcNow()) return "roomNotFound";
        if (connected.ContainsKey(accountId) || HasPendingRoom(accountId, except: room.Id)) return "alreadyInRoom";
        if (room.World != null)
        {
            if (CanResume(room, accountId)) return null;
            if (!room.Persistent) return "reentryUnavailable";
            return room.Players.Count + room.Peers.Count(peer => peer.JoinFresh && peer.Owner == 0) >= room.Capacity ? "roomFull" : null;
        }
        if (room.Peers.Count == 0 && room.Creator != accountId && !room.RestoredPeers.Contains(accountId)) return "hostPending";
        int occupied = room.Peers.Count + room.RestoredPeers.Count + room.AiCount;
        if (room.RestoredPeers.Contains(accountId)) occupied--;
        return occupied >= room.Capacity ? "roomFull" : null;
    }

    private static bool CanResume(Room room, string accountId) => room.World != null &&
        room.Players.TryGetValue(accountId, out int owner) && room.World.World.TryGetView(owner, out _);

    private bool HasPendingRoom(string accountId, string? except = null)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return rooms.Values.Any(room => room.Id != except && room.World == null && room.Peers.Count == 0 &&
            room.Creator == accountId && room.Deadline > now);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var maintenanceLifetime = new CancellationTokenSource();
        Task maintenance = accounts == null ? Task.CompletedTask : Maintain(accounts, maintenanceLifetime.Token);
        var clock = Stopwatch.StartNew();
        double deadline = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                lock (gate) Tick();
                deadline += rules.DeltaTime;
                double delay = deadline - clock.Elapsed.TotalSeconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
                else if (delay < -1) deadline = clock.Elapsed.TotalSeconds;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Task<bool>[] final;
            lock (gate)
            {
                stopping = true;
                foreach (OnlinePeer peer in connected.Values) peer.Abort();
                foreach (Room room in rooms.Values)
                {
                    while (room.Captures.TryDequeue(out MigrationCapture? capture))
                        capture.Completion.TrySetException(new OperationCanceledException("방 서버가 종료되었습니다."));
                    if (room.World == null) continue;
                    if (room.Closing || room.Finished) ScheduleClose(room, retry: true);
                    else ScheduleSave(room);
                }
                final = rooms.Values.Select(room => room.SaveTask).ToArray();
            }
            await maintenanceLifetime.CancelAsync();
            await maintenance;
            await Task.WhenAll(final);
        }
    }

    private void Tick()
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach (Room room in rooms.Values)
        {
            if (room.Closing)
            {
                if (!room.CloseQueued) ScheduleClose(room);
                if (room.CloseQueued && room.SaveTask.IsCompletedSuccessfully && room.SaveTask.Result) rooms.Remove(room.Id);
                else if (room.SaveTask.IsCompleted && now >= room.NextCloseAttempt)
                    ScheduleClose(room, retry: true);
                continue;
            }
            bool changed = false;
            if (now >= room.Deadline)
                foreach (string account in room.RestoredPeers.ToArray())
                {
                    room.RestoredPeers.Remove(account);
                    if (room.World == null) room.MigratedMembers.Remove(account);
                    if (room.Players.TryGetValue(account, out int abandonedOwner)) room.World?.Leave(abandonedOwner);
                }
            for (int index = room.Peers.Count - 1; index >= 0; index--)
            {
                OnlinePeer peer = room.Peers[index];
                if (!peer.Disconnected && peer.IsSlow())
                {
                    OnlineGameServer.RecordRejected("slowConnection");
                    peer.Abort();
                }
                if (!peer.Disconnected) continue;
                room.Peers.RemoveAt(index);
                connected.Remove(peer.Account.Id);
                if (peer.Owner > 0) room.World?.Leave(peer.Owner);
                changed = true;
            }
            if (room.World == null)
            {
                if (room.Peers.Count == 0 && (changed || room.Deadline <= now)) { rooms.Remove(room.Id); continue; }
                if (changed) BroadcastLobby(room);
            }
            foreach (OnlinePeer peer in room.Peers)
            {
                if (room.World != null && peer.Owner == 0)
                {
                    if (!EnterGame(room, peer)) continue;
                }
                while (!peer.Disconnected && peer.Input.Reader.TryRead(out byte[]? packet))
                    if (!room.Frozen && !room.InitialBackupPending) Handle(room, peer, packet);
            }
            if (room.InitialBackupPending && room.InitialBackupConfirmed)
            {
                room.InitialBackupPending = false;
                foreach (OnlinePeer peer in room.Peers) BeginGame(room.World!, peer, peer.Owner);
            }
            if (room.World != null && !room.Frozen && !room.InitialBackupPending && (room.Peers.Count > 0 || room.RestoredPeers.Count == 0))
            {
                long? allocatedBefore = LoadTrace == null ? null : GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                long previousTick = room.World.World.Metrics.Tick;
                Step(room);
                CaptureMigrations(room);
                OnlineGameServer.RoomTick.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (room.World.World.Metrics.Tick != previousTick) LoadTrace?.Record(room.World, start, allocatedBefore);
            }
            else CaptureMigrations(room);
        }
    }

    private void CaptureMigrations(Room room)
    {
        while (room.Captures.TryDequeue(out MigrationCapture? request))
        {
            try
            {
                if (room.Closing || room.Finished) throw new InvalidOperationException("이미 종료한 방은 승계할 수 없습니다.");
                if (request.LeavingAccount != null && room.Players.TryGetValue(request.LeavingAccount, out int owner))
                    room.World?.Leave(owner);
                room.Frozen |= request.Freeze;
                var snapshot = new HostMigrationSnapshot
                {
                    RoomId = room.Id, Title = room.Title, MinimumPlayers = room.MinimumPlayers, Capacity = room.Capacity,
                    AiCount = room.AiCount, AiDifficulties = room.AiDifficulties.ToArray(), AiColorIds = room.AiColorIds.ToArray(), DataHash = rules.DataHash, Generation = request.Generation,
                    BackupNumber = checked(++room.BackupNumber), Tick = room.World?.World.Metrics.Tick ?? 0,
                    NextPlayerId = room.World?.NextPlayerId ?? 1,
                    Members = room.Peers.Where(peer => !peer.Disconnected).Select(peer => new MigrationMember(peer.Account.Id,
                        peer.Account.Nickname, peer.Element, peer.Ready, peer.Owner, peer.ColorId))
                        .Concat(room.RestoredPeers.Where(room.MigratedMembers.ContainsKey).Select(account => room.MigratedMembers[account]))
                        .DistinctBy(member => member.AccountId).ToArray(),
                    Players = new(room.Players, StringComparer.Ordinal), World = room.World?.World.CaptureMigrationState(),
                    Ai = room.World?.CaptureAiMigration() ?? [], Chat = room.Chat.CaptureMigration()
                };
                request.Completion.TrySetResult(snapshot);
            }
            catch (Exception error) { request.Completion.TrySetException(error); }
        }
    }

    private bool EnterGame(Room room, OnlinePeer peer)
    {
        NetworkWorld world = room.World!;
        if (peer.JoinFresh)
        {
            if (world.TryJoin(peer.Element, out int owner, peer.Account.Nickname) == JoinFailure.None)
            {
                room.Players[peer.Account.Id] = owner;
                room.NextCheckpointTick = 0;
                BeginGame(world, peer, owner);
                return true;
            }
            SendLobbyError(peer, LobbyError.SpawnFailed);
        }
        else
        {
            if (room.Players.TryGetValue(peer.Account.Id, out int owner) && world.TryResume(owner))
            {
                BeginGame(world, peer, owner, resumed: true);
                return true;
            }
            SendLobbyError(peer, LobbyError.InvalidState);
        }
        peer.Finish();
        return false;
    }

    private void Handle(Room room, OnlinePeer peer, byte[] packet)
    {
        var reader = new NetDataReader(packet);
        PacketKind kind = (PacketKind)packet[0];
        if (room.World == null || kind is PacketKind.LobbyStart or PacketKind.LobbyReady or PacketKind.LobbyAiCount or PacketKind.LobbyAiDifficulty or PacketKind.LobbyColor)
        {
            if (room.World != null) { SendLobbyError(peer, LobbyError.InvalidState); return; }
            if (kind == PacketKind.LobbyReady && M5Protocol.TryReadLobbyReady(reader, rules, out int element, out bool ready))
            {
                if (element != peer.Element || ready != peer.Ready)
                {
                    peer.Element = element;
                    peer.Ready = ready;
                    BroadcastLobby(room);
                }
                return;
            }
            if (kind == PacketKind.LobbyColor && M5Protocol.TryReadLobbyColor(reader, out int colorId))
            {
                if (peer.Ready || room.RestoredPeers.Count > 0) { SendLobbyError(peer, LobbyError.InvalidState); return; }
                var others = room.Peers.Where(other => !ReferenceEquals(other, peer)).Select(other => other.ColorId)
                    .Concat(room.RestoredPeers.Where(room.MigratedMembers.ContainsKey).Select(id => room.MigratedMembers[id].ColorId));
                LobbyError? error = LobbyColors.Select(colorId, others, room.AiColorIds);
                if (error.HasValue) { SendLobbyError(peer, error.Value); return; }
                peer.ColorId = colorId;
                BroadcastLobby(room);
                return;
            }
            if (kind == PacketKind.LobbyAiCount && M5Protocol.TryReadLobbyAiCount(reader, out int count))
            {
                if (!ReferenceEquals(room.Peers[0], peer)) { SendLobbyError(peer, LobbyError.NotHost); return; }
                if (room.Peers.Count + room.RestoredPeers.Count + count > room.Capacity) { SendLobbyError(peer, LobbyError.RoomFull); return; }
                if (room.AiCount != count)
                {
                    if (count < room.AiCount)
                    {
                        room.AiColorIds.RemoveRange(count, room.AiCount - count);
                        room.AiDifficulties.RemoveRange(count, room.AiCount - count);
                    }
                    while (room.AiCount < count)
                    {
                        room.AiColorIds.Add(LobbyColors.FirstAvailable(room.Peers.Select(member => member.ColorId)
                            .Concat(room.RestoredPeers.Where(room.MigratedMembers.ContainsKey).Select(account => room.MigratedMembers[account].ColorId)), room.AiColorIds));
                        room.AiDifficulties.Add(AiDifficulty.Normal);
                    }
                    BroadcastLobby(room);
                }
                return;
            }
            if (kind == PacketKind.LobbyAiDifficulty && M5Protocol.TryReadLobbyAiDifficulty(reader, out int aiIndex, out AiDifficulty difficulty))
            {
                if (!ReferenceEquals(room.Peers[0], peer)) { SendLobbyError(peer, LobbyError.NotHost); return; }
                if (aiIndex >= room.AiCount) { SendLobbyError(peer, LobbyError.InvalidState); return; }
                room.AiDifficulties[aiIndex] = difficulty;
                BroadcastLobby(room);
                return;
            }
            if (kind == PacketKind.LobbyStart && M5Protocol.TryReadLobbyStart(reader)) { TryStart(room, peer); return; }
            SendLobbyError(peer, LobbyError.InvalidPacket);
            return;
        }

        if (kind == PacketKind.ChatSend)
        {
            if (M5Protocol.TryReadChat(reader, settings, out ChatRequest chat))
                room.Chat.Send(room.World, peer.Owner, chat, (recipient, message) => DeliverChat(room, recipient, message));
            return;
        }
        if (kind == PacketKind.ChatReport)
        {
            if (reportDirectory != null && M5Protocol.TryReadReport(reader, out ulong id, out string reason) &&
                room.Chat.Report(peer.Owner, id) is ChatReport report)
            {
                string? sender = room.Players.FirstOrDefault(entry => entry.Value == report.From).Key;
                if (!reports.Writer.TryWrite(new StoredReport(room.Id, clock.GetUtcNow(), peer.Account.Id, report.From, sender, report.Id, report.Text, reason)))
                    OnlineGameServer.RecordRejected("reportQueueFull");
            }
            return;
        }
        bool parsed = Protocol.TryReadCommand(reader, room.World.Rules, out SimCommand command);
        CommandAdmission admission = parsed ? room.World.Command(peer.Owner, command) : CommandAdmission.InvalidInput;
        writer.Reset();
        writer.Put((byte)(admission == CommandAdmission.Accepted ? PacketKind.Accepted : PacketKind.Rejected));
        writer.Put(parsed ? command.Sequence : 0UL);
        if (admission != CommandAdmission.Accepted) writer.Put((byte)admission);
        peer.Send(writer);
    }

    private void TryStart(Room room, OnlinePeer peer)
    {
        if (!ReferenceEquals(room.Peers[0], peer)) { SendLobbyError(peer, LobbyError.NotHost); return; }
        if (room.Peers.Count + room.AiCount < room.MinimumPlayers || room.Peers.Any(member => !member.Ready || member.Disconnected))
        { SendLobbyError(peer, LobbyError.NotReady); return; }
        NetworkWorld world;
        try { world = new NetworkWorld(rules.ForRoom(room.Peers.Count + room.AiCount), settings); }
        catch (ArgumentException) { SendLobbyError(peer, LobbyError.SpawnFailed); return; }
        var players = new Dictionary<string, int>(room.Peers.Count, StringComparer.Ordinal);
        foreach (OnlinePeer member in room.Peers)
        {
            if (world.TryJoin(member.Element, out int owner, member.Account.Nickname, member.ColorId) != JoinFailure.None)
            { SendLobbyError(peer, LobbyError.SpawnFailed); return; }
            players.Add(member.Account.Id, owner);
        }
        if (world.AddAiOpponents(room.AiCount, peer.Element, room.AiDifficulties, room.AiColorIds) != JoinFailure.None)
        { SendLobbyError(peer, LobbyError.SpawnFailed); return; }
        room.Players = players;
        room.World = world;
        if (room.RequireBackup)
        {
            room.InitialBackupPending = true;
            foreach (OnlinePeer member in room.Peers) member.Owner = players[member.Account.Id];
            hostedBackups.Writer.TryWrite(0);
            return;
        }
        foreach (OnlinePeer member in room.Peers) BeginGame(world, member, players[member.Account.Id]);
    }

    private void BeginGame(NetworkWorld world, OnlinePeer peer, int owner, bool resumed = false)
    {
        peer.Owner = owner;
        peer.LastView = world.World.GetView(owner);
        peer.ColorId = world.World.GetView(owner).OwnerColors.Single(color => color.PlayerId == owner).ColorId;
        peer.Fog = new FogReplicationState(world.Rules);
        peer.Gameplay = new GameplayReplicationState(world.Rules);
        Protocol.WriteWelcome(writer, owner, resumed, roomPlayers: world.Rules.RoomPlayers);
        peer.Send(writer);
        try
        {
            WorldView view = world.World.GetView(owner);
            SendReliableView(peer, view);
            if (resumed) SendSnapshot(peer, view, world.Rules.MapSize, includeUnits: true, includePose: true);
        }
        catch (ReplicationOverflowException) { peer.Abort(); }
    }

    private void Step(Room room)
    {
        NetworkWorld world = room.World!;
        world.Step();
        long tick = world.World.Metrics.Tick;
        foreach (OnlinePeer peer in room.Peers)
        {
            if (peer.Disconnected || peer.Owner == 0) continue;
            if (!world.World.TryGetView(peer.Owner, out WorldView view))
            {
                Protocol.WriteSessionEnded(writer,SessionEndReason.BaseDestroyed,peer.LastView!.Journey);
                peer.Send(writer);
                peer.Finish();
                continue;
            }
            if (world.HasWinner)
            {
                room.Finished = true;
                Protocol.WriteSessionEnded(writer,SessionEndReason.Victory,view.Journey);
                peer.Send(writer);
                peer.Finish();
                continue;
            }
            try
            {
                peer.Gameplay!.Capture(view);
                bool units = tick % settings.UnitsEveryTicks == 0;
                if (units) SendReliableView(peer, view);
                bool pose = tick % settings.LegionEveryTicks == 0;
                if (units || pose) SendSnapshot(peer, view, world.Rules.MapSize, units, pose);
            }
            catch (ReplicationOverflowException) { peer.Abort(); }
        }
        if (room.Persistent)
        {
            foreach ((string account, int owner) in room.Players)
                if (!world.World.TryGetView(owner, out _)) room.Players.Remove(account);
        }
        if (!room.Persistent && room.Peers.Count == 0 && (room.Finished || !world.World.HasGhosts))
        {
            if (store == null) rooms.Remove(room.Id);
            else
            {
                room.Closing = true;
                ScheduleClose(room);
            }
            return;
        }
        if (store != null && !room.Finished && tick >= room.NextCheckpointTick && room.SaveTask.IsCompleted) ScheduleSave(room);
    }

    private void SendReliableView(OnlinePeer peer, WorldView view)
    {
        FogReplicationState fog = peer.Fog!;
        GameplayReplicationState gameplay = peer.Gameplay!;
        fog.Prepare(view);
        for (int part = 0; part < fog.ForgetChunkCount; part++) { fog.WriteForget(writer, part); peer.Send(writer); }
        for (int part = 0; part < fog.ChunkCount; part++) { fog.WriteChunk(writer, part); peer.Send(writer); }
        fog.Commit();
        gameplay.Prepare(view, fog.Revision);
        for (int part = 0; part < gameplay.ChunkCount; part++) { gameplay.WriteChunk(writer, part); peer.Send(writer); }
        gameplay.Commit();
    }

    private void SendSnapshot(OnlinePeer peer, WorldView view, float mapSize, bool includeUnits, bool includePose)
    {
        byte[]? pose = null;
        byte[][]? units = null;
        if (includePose)
        {
            Protocol.WritePose(writer, view);
            pose = writer.CopyData();
        }
        if (includeUnits)
        {
            units = new byte[Protocol.ChunkCount(view.Units.Count)][];
            for (int part = 0; part < units.Length; part++)
            {
                Protocol.WriteUnits(writer, view, mapSize, part, peer.Fog!.Revision);
                units[part] = writer.CopyData();
            }
        }
        peer.ReplaceSnapshot(pose, units);
    }

    private void BroadcastLobby(Room room)
    {
        if (room.RequireBackup) hostedBackups.Writer.TryWrite(0);
        var roster = new LobbyMember[room.Peers.Count];
        for (int index = 0; index < roster.Length; index++)
        {
            OnlinePeer peer = room.Peers[index];
            roster[index] = new LobbyMember(peer.Account.Nickname, peer.Element, peer.Ready, peer.ColorId);
        }
        for (int index = 0; index < roster.Length; index++)
        {
            M5Protocol.WriteLobbyState(writer, room.Title, index, room.MinimumPlayers, room.Capacity, roster, room.AiCount, room.AiDifficulties, room.AiColorIds);
            room.Peers[index].Send(writer);
        }
    }

    private void SendLobbyError(OnlinePeer peer, LobbyError error)
    {
        M5Protocol.WriteLobbyError(writer, error);
        peer.Send(writer);
    }

    private void DeliverChat(Room room, int owner, ChatMessage message)
    {
        foreach (OnlinePeer peer in room.Peers)
        {
            if (peer.Owner != owner) continue;
            M5Protocol.WriteChatMessage(writer, message);
            peer.Send(writer);
            return;
        }
    }

    private void ScheduleSave(Room room)
    {
        if (store == null) return;
        NetworkWorld world = room.World!;
        long started = Stopwatch.GetTimestamp();
        var players = new Dictionary<string, int>(room.Players, StringComparer.Ordinal);
        WorldCheckpoint state = world.World.CaptureCheckpoint();
        OnlineGameServer.SaveCopy.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var checkpoint = room.Persistent ? new WorldSave(room.Id, state, players, world.NextPlayerId) :
            new WorldSave(room.Id, state, players, world.NextPlayerId, room.Title, room.MinimumPlayers, room.Capacity,
                AiOwners: world.CaptureAiOwners(), AiDifficulties: world.CaptureAiDifficulties());
        room.NextCheckpointTick = world.World.Metrics.Tick + settings.SaveIntervalSeconds * (long)rules.TickRate;
        room.SaveTask = QueueFile(room.SaveTask, () => store.Save(checkpoint), room.Id);
    }

    private void ScheduleClose(Room room, bool retry = false)
    {
        if (store == null || room.CloseQueued && !retry) return;
        room.CloseQueued = true;
        room.NextCloseAttempt = clock.GetUtcNow().AddMinutes(1);
        room.SaveTask = QueueFile(room.SaveTask, () => store.Close(room.Id), room.Id);
    }

    private Task<bool> QueueFile(Task<bool> previous, Action write, string id) => Task.Run(async () =>
    {
        await previous;
        try { write(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        { logger.LogError("온라인 방 {RoomId} 저장 실패 ({ErrorType}).", id, error.GetType().Name); return false; }
    });

    private async Task Maintain(IAccountStore accountStore, CancellationToken cancellation)
    {
        DateTimeOffset nextPrune = default;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                OnlinePeer[] peers;
                lock (gate) peers = connected.Values.ToArray();
                foreach (OnlinePeer peer in peers)
                    if (!accountStore.IsActive(peer.Account.Id)) peer.Abort();
                while (reports.Reader.TryRead(out StoredReport? report)) PersistReport(report);
                DateTimeOffset now = clock.GetUtcNow();
                if (now >= nextPrune)
                {
                    nextPrune = now.AddHours(1);
                    PruneReports(now - ReportRetention);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { while (reports.Reader.TryRead(out StoredReport? report)) PersistReport(report); }
    }

    private void PersistReport(StoredReport report)
    {
        if (reportDirectory == null) return;
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(reportDirectory);
            else Directory.CreateDirectory(reportDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string path = Path.Combine(reportDirectory, Guid.NewGuid().ToString("N") + ".report");
            string pending = path + ".tmp";
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    System.Text.Json.JsonSerializer.Serialize(stream, report);
                    stream.Flush(true);
                }
                File.Move(pending, path);
            }
            finally { File.Delete(pending); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.LogError("온라인 채팅 신고 기록 실패 ({ErrorType}).", error.GetType().Name); }
    }

    private void PruneReports(DateTimeOffset cutoff)
    {
        try
        {
            if (reportDirectory == null || !Directory.Exists(reportDirectory)) return;
            foreach (string path in Directory.EnumerateFiles(reportDirectory, "*.report"))
                if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime) File.Delete(path);
            foreach (string path in Directory.EnumerateFiles(reportDirectory, "*.report.tmp"))
                if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.LogError("만료된 온라인 채팅 신고 정리 실패 ({ErrorType}).", error.GetType().Name); }
    }

    private static bool IsRoomId(string id) => Guid.TryParseExact(id, "N", out Guid value) && value.ToString("N") == id;
}
