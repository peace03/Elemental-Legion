using System.Globalization;
using System.IO.Compression;
using System.Text;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;

namespace ElementalLegion.Server;

/// <summary>백업 크기와 내용을 검증한 후에만 승계 런타임에 전달한다.</summary>
internal static class MigrationSnapshotCodec
{
    internal const int ChunkBytes = 32 * 1024;
    internal const int MaxCompressedBytes = 8 * 1024 * 1024;
    internal const int MaxExpandedBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(HostMigrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        byte[] expanded = Utf8.GetBytes(JsonConvert.SerializeObject(snapshot, Settings()));
        if (expanded.Length > MaxExpandedBytes) throw new InvalidDataException("승계 백업의 압축 전 크기가 상한을 넘었습니다.");
        using var target = new MemoryStream();
        using (var compressed = new BrotliStream(target, CompressionLevel.Fastest, leaveOpen: true)) compressed.Write(expanded);
        if (target.Length > MaxCompressedBytes) throw new InvalidDataException("승계 백업의 압축 크기가 상한을 넘었습니다.");
        return target.ToArray();
    }

    internal static HostMigrationSnapshot Decode(byte[] compressed, GameRules rules)
    {
        if (compressed == null || compressed.Length == 0 || compressed.Length > MaxCompressedBytes)
            throw new InvalidDataException("승계 백업의 압축 크기가 잘못됐습니다.");
        try
        {
            using var source = new MemoryStream(compressed, writable: false);
            using var decompressor = new BrotliStream(source, CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            byte[] block = new byte[ChunkBytes];
            int count;
            while ((count = decompressor.Read(block)) > 0)
            {
                if (expanded.Length + count > MaxExpandedBytes) throw new InvalidDataException("승계 백업의 압축 해제 크기가 상한을 넘었습니다.");
                expanded.Write(block, 0, count);
            }
            var snapshot = JsonConvert.DeserializeObject<HostMigrationSnapshot>(Utf8.GetString(expanded.GetBuffer(), 0, checked((int)expanded.Length)), Settings())
                ?? throw new InvalidDataException("승계 백업이 비어 있습니다.");
            Validate(snapshot, rules);
            return snapshot;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new InvalidDataException("승계 백업의 내용이 잘못됐습니다.", error); }
    }

    internal static void Validate(HostMigrationSnapshot snapshot, GameRules rules)
    {
        if (snapshot.Format != 1 || snapshot.Version != Protocol.Version || snapshot.DataHash != rules.DataHash ||
            !ValidId(snapshot.RoomId) || snapshot.Generation == 0 || snapshot.BackupNumber == 0 || snapshot.Tick < 0 ||
            OnlineRooms.ValidateRoom(rules, snapshot.Title, snapshot.MinimumPlayers, snapshot.Capacity) != snapshot.Title ||
            snapshot.Members == null || snapshot.Members.Length > snapshot.Capacity || snapshot.Ai == null ||
            snapshot.AiCount < 0 || snapshot.AiCount > snapshot.Capacity || snapshot.Players == null ||
            snapshot.Players.Count > snapshot.Capacity || snapshot.NextPlayerId < 1 || snapshot.Chat == null)
            throw new InvalidDataException("승계 백업의 방 정보가 잘못됐습니다.");
        if (snapshot.AiDifficulties != null && (snapshot.AiDifficulties.Length != snapshot.AiCount ||
            snapshot.AiDifficulties.Any(difficulty => difficulty > AiDifficulty.Hard)))
            throw new InvalidDataException("승계 AI 난이도가 잘못됐습니다.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var owners = new HashSet<int>();
        foreach (MigrationMember member in snapshot.Members)
            if (member == null || !ValidId(member.AccountId) || !identities.Add(member.AccountId) ||
                !PlayerNickname.IsValid(member.Nickname) || !rules.StartingElements.Contains(member.Element) ||
                member.Owner < 0 || member.Owner >= snapshot.NextPlayerId)
                throw new InvalidDataException("승계 참가자 정보가 잘못됐습니다.");
        _ = ResolveColors(snapshot);
        foreach ((string account, int owner) in snapshot.Players)
            if (!ValidId(account) || owner < 1 || owner >= snapshot.NextPlayerId || !owners.Add(owner))
                throw new InvalidDataException("승계 플레이어 연결이 잘못됐습니다.");
        if (snapshot.World == null)
        {
            if (snapshot.Tick != 0 || snapshot.Players.Count != 0 || snapshot.Ai.Length != 0 ||
                snapshot.Members.Length + snapshot.AiCount > snapshot.Capacity || snapshot.Members.Any(member => member.Owner != 0))
                throw new InvalidDataException("승계 대기실 상태가 잘못됐습니다.");
        }
        else
        {
            if (snapshot.World.Tick != snapshot.Tick || snapshot.World.DataHash != snapshot.DataHash ||
                snapshot.World.RoomPlayers < snapshot.MinimumPlayers || snapshot.World.RoomPlayers > snapshot.Capacity ||
                snapshot.Ai.Length > snapshot.AiCount || snapshot.NextPlayerId <= snapshot.World.MaxPlayerId)
                throw new InvalidDataException("승계 전장의 상태가 잘못됐습니다.");
            var world = SimulationWorld.RestoreMigrationState(rules, snapshot.World);
            foreach (MigrationMember member in snapshot.Members)
                if (member.Owner > 0 && (!snapshot.Players.TryGetValue(member.AccountId, out int owner) || owner != member.Owner))
                    throw new InvalidDataException("승계 참가자의 플레이어 연결이 다릅니다.");
                else if (member.Owner > 0 && member.ColorId != -1 && world.TryGetView(member.Owner, out WorldView view) &&
                    view.OwnerColors.Single(color => color.PlayerId == member.Owner).ColorId != member.ColorId)
                    throw new InvalidDataException("승계 참가자의 소유 색이 월드와 다릅니다.");
            foreach (BotBrainState state in snapshot.Ai)
            {
                if (state == null || !owners.Add(state.PlayerId) || !world.TryGetView(state.PlayerId, out _))
                    throw new InvalidDataException("승계 AI의 소유자가 잘못됐습니다.");
                _ = BotBrain.RestoreState(rules.ForRoom(snapshot.World.RoomPlayers), state);
            }
        }
        ChatRouter.ValidateMigration(snapshot.Chat, snapshot.NextPlayerId);
    }

    internal static int[] ResolveColors(HostMigrationSnapshot snapshot)
    {
        if (snapshot.AiColorIds != null && snapshot.AiColorIds.Length != snapshot.AiCount)
            throw new InvalidDataException("승계 AI 소유 색의 수가 잘못됐습니다.");
        return LobbyColors.Restore(snapshot.Members.Select(member => member.ColorId)
            .Concat(snapshot.AiColorIds ?? Enumerable.Repeat(-1, snapshot.AiCount)).ToArray());
    }

    private static bool ValidId(string value) => Guid.TryParseExact(value, "N", out Guid id) && id != Guid.Empty && id.ToString("N") == value;

    private static JsonSerializerSettings Settings() => new()
    {
        Culture = CultureInfo.InvariantCulture, DateParseHandling = DateParseHandling.None,
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore, MissingMemberHandling = MissingMemberHandling.Error,
        TypeNameHandling = TypeNameHandling.None, MaxDepth = 32, CheckAdditionalContent = true
    };
}
