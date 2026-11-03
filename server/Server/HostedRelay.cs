using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using Microsoft.AspNetCore.Http;

namespace ElementalLegion.Server;

/// <summary>플레이어 호스트의 임시 방을 검색 목록에 올리고 인증된 연결을 중계한다.</summary>
internal sealed class HostedRelay(GameRules rules, IAccountStore accounts, TimeProvider clock)
{
    private const int MaxTickets = 512;
    private const int HostMessagesPerSecond = 4096;
    private const int KeyBytes = FileAccountStore.TokenBytes;
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PasswordWindow = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions StateJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 3,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly object gate = new();
    private readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OnlineTicket> tickets = new(StringComparer.Ordinal);
    private readonly byte[] passwordPepper = RandomNumberGenerator.GetBytes(KeyBytes);

    private sealed class Room(OnlineRoomInfo info, AccountIdentity creator, byte[] hostProof, byte[]? passwordDigest,
        DateTimeOffset deadline)
    {
        public OnlineRoomInfo Info = info;
        public AccountIdentity Creator { get; } = creator;
        public DateTimeOffset Deadline { get; } = deadline;
        public byte[]? HostProof = hostProof;
        public byte[]? PasswordDigest { get; } = passwordDigest;
        public DateTimeOffset LastState;
        public bool CreatorPaired;
        public long StateWindow = Stopwatch.GetTimestamp();
        public int StateMessages;
        public CancellationTokenSource Ended { get; } = new();
        public Dictionary<string, Slot> Players { get; } = new(StringComparer.Ordinal);
        public Channel<Slot> Requests { get; } = Channel.CreateBounded<Slot>(info.Capacity);
        public DateTimeOffset PasswordReset;
        public int RoomFailures;
        public Dictionary<string, int> AccountFailures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> IpFailures { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Slot(AccountIdentity account, int? element, string key, byte[] proof, DateTimeOffset deadline)
    {
        public AccountIdentity Account { get; } = account;
        public int? Element { get; } = element;
        public string? Key = key;
        public byte[] Proof { get; } = proof;
        public DateTimeOffset Deadline { get; } = deadline;
        public CancellationTokenSource Revoked { get; } = new();
        public TaskCompletionSource<WebSocket> Paired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RoomState(int? Count, bool? Running);

    public OnlineRoomInfo[] List()
    {
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            return rooms.Values.Where(room => IsLive(room, now)).Select(room => room.Info).ToArray();
        }
    }

    public bool Owns(string id)
    {
        lock (gate) return rooms.ContainsKey(id);
    }

    public int ReservedCount
    {
        get
        {
            lock (gate)
            {
                DateTimeOffset now = clock.GetUtcNow();
                return rooms.Values.Count(room => IsReserved(room, now));
            }
        }
    }

    public bool PendingCreation(string accountId)
    {
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            return rooms.Values.Any(room => room.Creator.Id == accountId && !room.CreatorPaired && room.Deadline > now);
        }
    }

    public string? Create(AccountIdentity account, string? title, int minimumPlayers, int capacity, string? password,
        int version, string? dataHash, out OnlineRoomInfo? info, out string? hostKey)
    {
        info = null;
        hostKey = null;
        string? error = CompatibilityError(version, dataHash);
        if (error != null) return error;
        title = OnlineRooms.ValidateRoom(rules, title, minimumPlayers, capacity);
        if (title == null) return "invalidRoom";
        bool isProtected = !string.IsNullOrEmpty(password);
        if (isProtected && !ValidPassword(password)) return "invalidPassword";

        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            foreach (Room expired in rooms.Values.Where(room => room.HostProof != null && room.Deadline <= now).ToArray())
                rooms.Remove(expired.Info.Id);
            if (rooms.Values.Any(room => room.Creator.Id == account.Id && IsReserved(room, now))) return "alreadyInRoom";

            var id = Guid.NewGuid();
            hostKey = NewKey(out byte[] proof);
            info = new OnlineRoomInfo(id.ToString("N"), title, 0, capacity, minimumPlayers, false, false, true, isProtected);
            rooms.Add(info.Id, new Room(info, account, proof, isProtected ? PasswordDigest(id, password!) : null, now + TicketLifetime));
            return null;
        }
    }

    public string? IssueTicket(string id, AccountIdentity account, string ip, int version, string? dataHash, int? element, string? password,
        out string? value)
    {
        value = null;
        string? error = CompatibilityError(version, dataHash);
        if (error != null) return error;
        if (element.HasValue && !rules.StartingElements.Contains(element.Value)) return "invalidElement";
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (!rooms.TryGetValue(id, out Room? room) || !IsLive(room, now)) return "roomNotFound";
            error = CheckPassword(room, account.Id, ip, password, now) ?? CanPlay(room, account.Id);
            if (error != null) return error;
            foreach (var previous in tickets.Where(entry => entry.Value.Expires <= now || entry.Value.Account.Id == account.Id).ToArray())
                tickets.Remove(previous.Key);
            if (tickets.Count >= MaxTickets) return "rateLimited";
            value = NewKey(out byte[] proof);
            tickets.Add(Convert.ToHexStringLower(proof), new OnlineTicket(id, account, false, element, now + TicketLifetime));
            return null;
        }
    }

    public OnlineTicket? Redeem(string id, ReadOnlySpan<byte> value)
    {
        if (value.Length != KeyBytes) return null;
        lock (gate)
        {
            string key = Convert.ToHexStringLower(SHA256.HashData(value));
            if (!tickets.TryGetValue(key, out OnlineTicket? ticket) || ticket.RoomId != id) return null;
            tickets.Remove(key);
            return ticket.Expires > clock.GetUtcNow() ? ticket : null;
        }
    }

    public async Task Play(OnlineTicket ticket, WebSocket player, int commandLimit, CancellationToken cancellation)
    {
        Room? room;
        Slot? slot = null;
        string? error;
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            error = rooms.TryGetValue(ticket.RoomId, out room) && IsLive(room, now)
                ? CanPlay(room, ticket.Account.Id) : "roomNotFound";
            if (error == null)
            {
                string key = NewKey(out byte[] proof);
                slot = new Slot(ticket.Account, ticket.FreshElement, key, proof, now + PairTimeout);
                if (room!.Requests.Writer.TryWrite(slot)) room.Players.Add(ticket.Account.Id, slot);
                else error = "unavailable";
            }
        }
        if (error != null) { await OnlineGameServer.RejectSocket(player, error); return; }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, room!.Ended.Token, slot!.Revoked.Token);
        try
        {
            WebSocket bridge;
            try { bridge = await slot!.Paired.Task.WaitAsync(PairTimeout, lifetime.Token); }
            catch (TimeoutException) { await OnlineGameServer.RejectSocket(player, "unavailable"); return; }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { await OnlineGameServer.RejectSocket(player, slot.Revoked.IsCancellationRequested ? "invalidAccount" : "roomNotFound"); return; }
            await Forward(player, bridge, commandLimit, lifetime.Token);
        }
        finally
        {
            lock (gate) room.Players.Remove(ticket.Account.Id);
            slot!.Finished.TrySetResult();
        }
    }

    public async Task Connect(HttpContext context, string id, bool bridge, CancellationToken stopping)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = false });
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        authentication.CancelAfter(PairTimeout);
        byte[] key = new byte[KeyBytes + 1];
        try
        {
            int length = await OnlinePeer.ReceiveBinary(socket, key, authentication.Token);
            if (length != KeyBytes) { await OnlineGameServer.RejectSocket(socket, "invalidTicket"); return; }
            byte[] proof = SHA256.HashData(key.AsSpan(0, length));
            CryptographicOperations.ZeroMemory(key);
            authentication.CancelAfter(Timeout.InfiniteTimeSpan);
            if (bridge) await ConnectBridge(id, socket, proof, connection.Token);
            else await ConnectHost(id, socket, proof, connection.Token);
        }
        catch (InvalidDataException error)
        { await OnlineGameServer.RejectSocket(socket, error.Message is "rateLimited" or "packetTooLarge" ? error.Message : "invalidPacket"); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { OnlineGameServer.RecordRejected("socketClosed"); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task ConnectHost(string id, WebSocket socket, byte[] proof, CancellationToken cancellation)
    {
        Room? room;
        lock (gate)
        {
            if (!rooms.TryGetValue(id, out room) || room.Deadline <= clock.GetUtcNow() || room.HostProof == null ||
                !CryptographicOperations.FixedTimeEquals(proof, room.HostProof) || !accounts.IsActive(room.Creator.Id))
                room = null;
            else room.HostProof = null;
        }
        if (room == null) { await OnlineGameServer.RejectSocket(socket, "invalidTicket"); return; }
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task monitor = CheckAccounts();
        try
        {
            await SendJson(socket, new
            {
                kind = "ready", room.Info.Title, room.Info.MinimumPlayers, room.Info.Capacity,
                creatorId = room.Creator.Id, creatorNickname = room.Creator.Nickname,
                version = Protocol.Version, dataHash = rules.DataHash
            }, connection.Token);
            var stateBuffer = new byte[256];
            using var stateTimeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
            if (!await ReceiveState(socket, room, stateBuffer, stateTimeout)) return;
            await SendJson(socket, new { kind = "listed" }, connection.Token);
            Task receive = ReceiveStates();
            Task send = NotifyHost();
            await Task.WhenAny(receive, send);
            await connection.CancelAsync();
            await Task.WhenAll(receive, send);

            async Task ReceiveStates()
            {
                while (await ReceiveState(socket, room, stateBuffer, stateTimeout)) { }
            }

            async Task NotifyHost()
            {
                await foreach (Slot slot in room.Requests.Reader.ReadAllAsync(connection.Token))
                {
                    string? bridgeKey = slot.Key;
                    slot.Key = null;
                    if (slot.Finished.Task.IsCompleted || slot.Revoked.IsCancellationRequested) continue;
                    await SendJson(socket, new
                    {
                        kind = "peer", bridgeKey, accountId = slot.Account.Id,
                        nickname = slot.Account.Nickname, startElement = slot.Element
                    }, connection.Token);
                }
            }

        }
        finally
        {
            await connection.CancelAsync();
            lock (gate)
            {
                rooms.Remove(id);
                foreach (var entry in tickets.Where(entry => entry.Value.RoomId == id).ToArray()) tickets.Remove(entry.Key);
            }
            await room.Ended.CancelAsync();
            await monitor;
        }

        async Task CheckAccounts()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(connection.Token))
                {
                    Slot[] players;
                    bool reserved;
                    lock (gate)
                    {
                        reserved = IsReserved(room, clock.GetUtcNow());
                        players = room.Players.Values.ToArray();
                    }
                    if (!reserved || !accounts.IsActive(room.Creator.Id))
                    {
                        await connection.CancelAsync();
                        return;
                    }
                    foreach (Slot player in players)
                        if (!accounts.IsActive(player.Account.Id)) await player.Revoked.CancelAsync();
                }
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
        }
    }

    private async Task<bool> ReceiveState(WebSocket socket, Room room, Memory<byte> buffer, CancellationTokenSource timeout)
    {
        timeout.CancelAfter(StateTimeout);
        int length = 0;
        for (int fragments = 0; fragments < buffer.Length; fragments++)
        {
            ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer[length..], timeout.Token);
            if (part.MessageType == WebSocketMessageType.Close) return false;
            length += part.Count;
            if (part.MessageType != WebSocketMessageType.Text || length == buffer.Length) break;
            if (!part.EndOfMessage) continue;

            RoomState? state;
            try { state = JsonSerializer.Deserialize<RoomState>(buffer.Span[..length], StateJson); }
            catch (JsonException) { break; }
            if (state?.Count is not int count || state.Running is not bool running || count < 0 || count > room.Info.Capacity) break;
            if (Stopwatch.GetElapsedTime(room.StateWindow) >= TimeSpan.FromSeconds(1))
            {
                room.StateWindow = Stopwatch.GetTimestamp();
                room.StateMessages = 0;
            }
            if (++room.StateMessages > 10) throw new InvalidDataException("rateLimited");
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            lock (gate)
            {
                DateTimeOffset now = clock.GetUtcNow();
                if (!IsReserved(room, now)) return false;
                room.Info = room.Info with { Count = count, Running = running };
                room.LastState = now;
            }
            return true;
        }
        throw new InvalidDataException("invalidPacket");
    }

    private async Task ConnectBridge(string id, WebSocket socket, byte[] proof, CancellationToken cancellation)
    {
        Room? room;
        Slot? slot = null;
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (rooms.TryGetValue(id, out room) && IsLive(room, now))
            {
                slot = room.Players.Values.FirstOrDefault(candidate => candidate.Deadline > now && !candidate.Revoked.IsCancellationRequested &&
                    CryptographicOperations.FixedTimeEquals(proof, candidate.Proof));
                if (slot != null)
                {
                    if (!slot.Paired.TrySetResult(socket)) slot = null;
                    else if (slot.Account.Id == room.Creator.Id) room.CreatorPaired = true;
                }
            }
        }
        if (slot == null) { await OnlineGameServer.RejectSocket(socket, "invalidTicket"); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, room!.Ended.Token);
        await slot.Finished.Task.WaitAsync(lifetime.Token);
    }

    private static async Task Forward(WebSocket player, WebSocket host, int commandLimit, CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task commands = Pump(player, host, commandLimit, false, lifetime.Token);
        Task observations = Pump(host, player, HostMessagesPerSecond, true, lifetime.Token);
        Task first = await Task.WhenAny(commands, observations);
        // 호스트의 종료 프레임을 받았으면 역방향 전송 오류가 먼저 끝나도 종료 사유를 전달한다.
        if (first == commands && host.CloseStatus.HasValue && !observations.IsCompleted)
            await Task.WhenAny(observations, Task.Delay(SendTimeout, cancellation));
        await lifetime.CancelAsync();
        await Task.WhenAll(commands, observations);
    }

    private static async Task Pump(WebSocket source, WebSocket destination, int limit, bool fromHost, CancellationToken cancellation)
    {
        var buffer = new byte[Protocol.MaxDatagramBytes + 1];
        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        long interval = Stopwatch.GetTimestamp();
        int received = 0;
        while (!cancellation.IsCancellationRequested)
        {
            int length = await OnlinePeer.ReceiveBinary(source, buffer, cancellation);
            if (length == 0)
            {
                if (source.CloseStatus == null) throw new InvalidDataException("invalidPacket");
                if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    string reason = fromHost ? SafeHostClose(source.CloseStatusDescription) : "";
                    await OnlineGameServer.CloseSocket(destination, reason.Length == 0
                        ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation, reason);
                }
                return;
            }
            if (Stopwatch.GetElapsedTime(interval) >= TimeSpan.FromSeconds(1))
            {
                interval = Stopwatch.GetTimestamp();
                received = 0;
            }
            if (++received > limit) throw new InvalidDataException("rateLimited");
            sendTimeout.CancelAfter(SendTimeout);
            await destination.SendAsync(buffer.AsMemory(0, length), WebSocketMessageType.Binary, true, sendTimeout.Token);
            sendTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    private static async Task SendJson<T>(WebSocket socket, T value, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(SendTimeout);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string SafeHostClose(string? reason) => reason is "reentryUnavailable" or "roomNotFound" or "roomFull" or
        "alreadyInRoom" or "hostPending" or "unavailable" or "invalidElement" or "invalidPacket" or "packetTooLarge" or "rateLimited" ? reason : "";

    private string? CompatibilityError(int version, string? dataHash) => version != Protocol.Version ? "versionMismatch" :
        string.Equals(dataHash, rules.DataHash, StringComparison.OrdinalIgnoreCase) ? null : "dataMismatch";

    private static bool IsReserved(Room room, DateTimeOffset now) => room.CreatorPaired || room.Deadline > now;

    private static bool IsLive(Room room, DateTimeOffset now) =>
        IsReserved(room, now) && room.LastState != default && now - room.LastState < StateTimeout;

    private static string? CanPlay(Room room, string accountId)
    {
        if (room.Players.ContainsKey(accountId)) return "alreadyInRoom";
        if (room.Players.Count >= room.Info.Capacity || !room.Info.Running && room.Info.Count >= room.Info.Capacity) return "roomFull";
        if (room.Creator.Id != accountId && (!room.CreatorPaired || !room.Info.Running && room.Info.Count == 0)) return "hostPending";
        return null;
    }

    private string? CheckPassword(Room room, string accountId, string ip, string? password, DateTimeOffset now)
    {
        if (room.PasswordDigest == null || room.Creator.Id == accountId) return null;
        if (room.PasswordReset <= now)
        {
            room.PasswordReset = now + PasswordWindow;
            room.RoomFailures = 0;
            room.AccountFailures.Clear();
            room.IpFailures.Clear();
        }
        int accountFailures = room.AccountFailures.GetValueOrDefault(accountId);
        int ipFailures = room.IpFailures.GetValueOrDefault(ip);
        if (accountFailures >= 5 || ipFailures >= 10 || room.RoomFailures >= 20) return "rateLimited";
        if (ValidPassword(password) && CryptographicOperations.FixedTimeEquals(room.PasswordDigest,
                PasswordDigest(Guid.ParseExact(room.Info.Id, "N"), password!))) return null;
        room.RoomFailures++;
        room.AccountFailures[accountId] = accountFailures + 1;
        room.IpFailures[ip] = ipFailures + 1;
        return string.IsNullOrEmpty(password) ? "passwordRequired" : "incorrectPassword";
    }

    private byte[] PasswordDigest(Guid salt, string password)
    {
        Span<byte> input = stackalloc byte[16 + password.Length];
        salt.TryWriteBytes(input);
        Encoding.ASCII.GetBytes(password.AsSpan(), input[16..]);
        byte[] digest = HMACSHA256.HashData(passwordPepper, input);
        CryptographicOperations.ZeroMemory(input);
        return digest;
    }

    private static string NewKey(out byte[] proof)
    {
        Span<byte> key = stackalloc byte[KeyBytes];
        RandomNumberGenerator.Fill(key);
        proof = SHA256.HashData(key);
        string value = Convert.ToHexStringLower(key);
        CryptographicOperations.ZeroMemory(key);
        return value;
    }

    private static bool ValidPassword(string? password) =>
        password is { Length: >= 4 and <= 8 } && password.All(digit => digit is >= '0' and <= '9');
}
