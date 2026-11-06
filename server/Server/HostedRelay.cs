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
using LiteNetLib.Utils;

namespace ElementalLegion.Server;

/// <summary>플레이어 호스트의 임시 방을 검색 목록에 올리고 인증된 연결을 중계한다.</summary>
internal sealed partial class HostedRelay(GameRules rules, IAccountStore accounts, TimeProvider clock, NetworkSettings settings)
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
        public AccountIdentity Creator { get; set; } = creator;
        public DateTimeOffset Deadline { get; } = deadline;
        public byte[]? HostProof = hostProof;
        public byte[] DataProof = (byte[])hostProof.Clone();
        public byte[]? PasswordDigest { get; } = passwordDigest;
        public DateTimeOffset LastState;
        public bool CreatorPaired;
        public CancellationTokenSource Ended { get; } = new();
        public Dictionary<string, Slot> Players { get; } = new(StringComparer.Ordinal);
        public HostSession? Active;
        public Candidate? Standby;
        public ulong Generation = 1;
        public bool Migrating, Graceful, AwaitingBridges;
        public long NextOrder;
        public byte[]? Backup;
        public HostMigrationSnapshot? BackupState;
        public DateTimeOffset BackupAt;
        public TaskCompletionSource FinalBackup = NewSignal();
        public readonly SemaphoreSlim UploadGate = new(1, 1);
        public Task? MigrationTask;
        public DateTimeOffset MigrationStarted;
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
        public byte[] Proof { get; set; } = proof;
        public DateTimeOffset Deadline { get; set; } = deadline;
        public CancellationTokenSource Revoked { get; } = new();
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Paired = NewSignal();
        public WebSocket? Player;
        public HostBridge? Bridge;
        public readonly SemaphoreSlim SendGate = new(1, 1);
        public bool Capable, Leaving;
        public bool AuthoritySent;
        public bool Terminal;
        public long Order;
    }


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
        !room.Ended.IsCancellationRequested && IsReserved(room, now) && room.LastState != default &&
        (room.Migrating || room.MigrationTask is { IsCompleted: false } || now - room.LastState < StateTimeout);

    private static string? CanPlay(Room room, string accountId)
    {
        if (room.Migrating || room.AwaitingBridges) return "unavailable";
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
