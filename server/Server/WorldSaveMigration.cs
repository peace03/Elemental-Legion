using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ElementalLegion.Server;

/// <summary>원본 폴더를 읽기만 하며 검증을 끝낸 별도 저장 폴더를 한 번에 공개한다.</summary>
public static class WorldSaveMigration
{
    private const string QuickTransferCountPath = "legion.squads.quickTransferCount";
    private static readonly UTF8Encoding Encoding = new(false, true);

    /// <summary>명시한 원본/목표 규칙으로 변환하고 성공하면 0을 반환한다. 실패는 예외이며 원본은 변경하지 않는다.</summary>
    public static int Run(string sourceDirectory, string destinationDirectory, string sourceDataPath, string targetDataPath)
    {
        string source = FullDirectory(sourceDirectory), destination = FullDirectory(destinationDirectory);
        if (Related(source, destination) || Related(destination, source))
            throw new ArgumentException("원본과 출력 경로는 서로 같거나 상하위 경로일 수 없습니다.");
        RejectReparse(source); RejectReparse(destination);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("원본 저장 폴더가 없습니다.");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("출력 경로가 이미 있습니다.");
        string parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("출력 상위 폴더가 없습니다.");
        string sourceJson = ReadJson(Path.GetFullPath(sourceDataPath));
        JObject sourceData = WorldCheckpointUpgrade.ParseJson(sourceJson);
        string sourceHash = new GameDataDocument(sourceJson).Sha256;
        string targetJson = ReadJson(Path.GetFullPath(targetDataPath));
        JObject targetData = WorldCheckpointUpgrade.ParseJson(targetJson);
        var targetRules = new GameRules(new GameDataDocument(targetJson));
        bool currentRulesMatch = OnlyQuickTransferAdded(sourceData, targetData);
        WorldCheckpointUpgrade.LegacyRules? legacyRules = null;
        string[] files = Directory.GetFiles(source).Order(StringComparer.Ordinal).ToArray();
        if (Directory.GetDirectories(source).Length != 0) throw new IOException("월드 파일이 직접 들어 있는 폴더를 지정해야 합니다.");
        foreach (string file in files) RejectReparse(file);
        string temporary = Path.Combine(parent, ".world-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var summaries = new JArray();
            var converted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in files)
            {
                string name = Path.GetFileName(path);
                if (!WorldFile(name, out string worldId, out bool backup)) continue;
                if (File.Exists(Path.Combine(source, "world-" + worldId + ".json.closed"))) continue;
                string text = ReadJson(path);
                JObject wrapper = WorldCheckpointUpgrade.ParseJson(text);
                if (wrapper["Format"]?.Type != JTokenType.Integer) throw new InvalidDataException("저장 형식 번호가 정수가 아닙니다.");
                // 정상 로더/저장기의 필수 필드와 목표 규칙 복원 검사를 그대로 재사용한다.
                string validation = Path.Combine(temporary, ".validate");
                var store = new FileWorldStore(validation, targetRules);
                string written = Path.Combine(validation, "world-" + worldId + ".json");
                JObject before;
                switch ((long)wrapper["Format"]!)
                {
                    case 1:
                        legacyRules ??= new WorldCheckpointUpgrade.LegacyRules(sourceJson);
                        Envelope envelope = wrapper.ToObject<Envelope>(Serializer()) ?? throw new InvalidDataException("저장 래퍼가 없습니다.");
                        ValidateEnvelope(envelope, worldId, legacyRules);
                        before = envelope.Save.World;
                        WorldCheckpoint checkpoint = WorldCheckpointUpgrade.Upgrade(before.ToString(Formatting.None), legacyRules, targetRules);
                        store.Save(new WorldSave(envelope.Save.WorldId, checkpoint, envelope.Save.Players, envelope.Save.NextPlayerId,
                            envelope.Save.Title, envelope.Save.MinimumPlayers, envelope.Save.Capacity, envelope.Save.LanPort, envelope.Save.AiOwners, envelope.Save.AiDifficulties));
                        break;
                    case 2:
                        if (!currentRulesMatch) throw new InvalidDataException("현재 저장은 빠른 전송 수량 추가만 있는 데이터 쌍으로 이관할 수 있습니다.");
                        if (wrapper["Save"] is not JObject payload || payload["World"] is not JObject current)
                            throw new InvalidDataException("저장된 월드가 없습니다.");
                        before = current;
                        WriteHashMigration(written, wrapper, payload, before, sourceHash, targetRules.DataHash);
                        break;
                    default:
                        throw new InvalidDataException("지원하지 않는 저장 형식입니다.");
                }
                WorldSave loaded = store.Load(worldId) ?? throw new InvalidDataException("변환 결과를 복원할 수 없습니다.");
                File.Move(written, Path.Combine(temporary, name));
                Directory.Delete(validation, recursive: true);
                converted.Add(worldId);
                summaries.Add(Summary(name, text, before, JObject.FromObject(loaded.World), backup));
            }
            foreach (string path in files)
            {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(".json.closed", StringComparison.Ordinal) || !WorldFile(name[..^7], out _, out _)) continue;
                // 종료 기록은 내용에 비밀값을 허용하지 않고 원래의 단일 마커 형식만 복사한다.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != 1 || stream.ReadByte() != 1) throw new InvalidDataException("종료 기록이 잘못됐습니다.");
                File.WriteAllBytes(Path.Combine(temporary, name), [1]);
            }
            if (converted.Count == 0) throw new InvalidDataException("변환할 월드 파일이 없습니다.");
            // 백업만 있는 월드도 정상 Load 경로가 실제로 복원하는지 마지막으로 확인한다.
            var outputStore = new FileWorldStore(temporary, targetRules);
            foreach (string id in converted)
                if (outputStore.Load(id) == null) throw new InvalidDataException("최종 출력의 월드를 복원할 수 없습니다.");
            var report = new JObject { ["SourceDataHash"] = sourceHash, ["TargetDataHash"] = targetRules.DataHash, ["Files"] = summaries };
            File.WriteAllText(Path.Combine(temporary, "migration-report.json"), report.ToString(Formatting.Indented), Encoding);
            RejectReparse(source); RejectReparse(destination); RejectReparse(temporary);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("출력 경로가 이미 있습니다.");
            Directory.Move(temporary, destination);
            return 0;
        }
        finally
        {
            // 생성한 임시 폴더만 삭제하며 원본/출력 경로를 재귀 삭제 대상으로 사용하지 않는다.
            if (Directory.Exists(temporary))
            {
                RejectReparse(temporary);
                foreach (string path in Directory.EnumerateFileSystemEntries(temporary, "*", SearchOption.AllDirectories)) RejectReparse(path);
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static bool OnlyQuickTransferAdded(JObject source, JObject target)
    {
        if (source.SelectToken(QuickTransferCountPath) != null ||
            target.SelectToken(QuickTransferCountPath) is not JValue { Type: JTokenType.Integer, Parent: JProperty count })
            return false;
        count.Remove();
        return SameJson(source, target);
    }

    // 키 순서는 무시하되 실수의 근사 비교로 다른 밸런스 값이 통과하지 않게 한다.
    private static bool SameJson(JToken source, JToken target) => (source, target) switch
    {
        (JObject before, JObject after) => before.Count == after.Count &&
            before.Properties().All(property => after.TryGetValue(property.Name, out JToken? value) && SameJson(property.Value, value)),
        (JArray before, JArray after) => before.Count == after.Count &&
            before.Zip(after).All(pair => SameJson(pair.First, pair.Second)),
        (JValue before, JValue after) => before.Type == after.Type && Equals(before.Value, after.Value),
        _ => false
    };

    private static void WriteHashMigration(string path, JObject wrapper, JObject payload, JObject world, string sourceHash, string targetHash)
    {
        if (wrapper["DataHash"] is not JValue { Value: string envelopeHash } || envelopeHash != sourceHash ||
            world["DataHash"] is not JValue { Value: string checkpointHash } || checkpointHash != sourceHash ||
            world["SchemaVersion"] is not JValue { Type: JTokenType.Integer } schema || (long)schema != 3)
            throw new InvalidDataException("현재 저장의 스키마 또는 원본 데이터 해시가 일치하지 않습니다.");

        var updatedWorld = (JObject)world.DeepClone();
        updatedWorld["DataHash"] = targetHash;
        payload["World"] = updatedWorld;
        wrapper["DataHash"] = targetHash;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var output = new StreamWriter(stream, Encoding, leaveOpen: true))
        using (var json = new JsonTextWriter(output))
            wrapper.WriteTo(json);
        stream.Flush(flushToDisk: true);
    }

    private static JObject Summary(string name, string text, JObject before, JObject after, bool backup)
    {
        static JObject Totals(JObject world) => new()
        {
            ["Players"] = ((JArray)world["Players"]!).Count, ["Units"] = ((JArray)world["Units"]!).Count,
            ["Buildings"] = ((JArray)world["Buildings"]!).Count, ["Cores"] = ((JArray)world["Cores"]!).Count,
            ["Areas"] = ((JArray)world["Areas"]!).Count, ["Balance"] = world["Players"]!.Sum(player => (long)player["Cores"]!),
            ["CoreAmount"] = world["Cores"]!.Sum(core => (long)core["Amount"]!)
        };
        JObject oldTotals = Totals(before), newTotals = Totals(after);
        if (!JToken.DeepEquals(oldTotals, newTotals)) throw new InvalidDataException("변환 전후 개체 수 또는 잔고가 달라졌습니다.");
        return new JObject { ["File"] = name, ["Backup"] = backup,
            ["SourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.GetBytes(text))),
            ["Before"] = oldTotals, ["After"] = newTotals };
    }

    private static string ReadJson(string path)
    {
        RejectReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0 || stream.Length > WorldCheckpointUpgrade.MaxJsonBytes) throw new InvalidDataException("이관 파일 크기가 잘못됐습니다.");
        using var reader = new StreamReader(stream, Encoding, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static string FullDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("이관 폴더를 명시해야 합니다.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("파일 시스템 루트는 이관 폴더로 사용할 수 없습니다.");
        return full;
    }

    private static bool Related(string parent, string child) => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("재분석 지점은 이관 경로로 사용할 수 없습니다.");
    }

    private static bool WorldFile(string name, out string id, out bool backup)
    {
        backup = name.EndsWith(".json.bak", StringComparison.Ordinal);
        string ending = backup ? ".json.bak" : ".json";
        id = "";
        if (!name.StartsWith("world-", StringComparison.Ordinal) || !name.EndsWith(ending, StringComparison.Ordinal)) return false;
        id = name[6..^ending.Length];
        if (id.Length is < 1 or > 64 || id.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidDataException("월드 파일 ID가 잘못됐습니다.");
        return true;
    }

    private static void ValidateEnvelope(Envelope envelope, string id, WorldCheckpointUpgrade.LegacyRules rules)
    {
        LegacySave saved = envelope.Save;
        if (envelope.Format != 1 || envelope.DataHash != rules.DataHash || saved == null || saved.World == null || saved.WorldId != id ||
            (string?)saved.World["DataHash"] != rules.DataHash || saved.Players == null || saved.Players.Count > rules.MaxPlayers || saved.NextPlayerId <= 0 ||
            saved.LanPort < 0 || saved.LanPort > ushort.MaxValue || saved.LanPort != 0 && saved.Title == null)
            throw new InvalidDataException("v1 저장 형식/해시/월드 연결이 잘못됐습니다.");
        int aiCount = saved.AiOwners?.Length ?? 0;
        if (saved.AiDifficulties != null && (saved.AiOwners == null || saved.AiDifficulties.Length != aiCount || saved.AiDifficulties.Any(value => value > AiDifficulty.Hard)))
            throw new InvalidDataException("AI 연결이 잘못됐습니다.");
        int room = (int?)saved.World["RoomPlayers"] ?? 0;
        if (saved.Title != null)
        {
            if (saved.Title.Length is < 1 or > 32 || saved.Title != saved.Title.Trim() || saved.Title.Any(char.IsControl) || saved.MinimumPlayers < 1 ||
                saved.MinimumPlayers > saved.Capacity || saved.Capacity > rules.MaxPlayers || saved.Players.Count < 1 ||
                saved.Players.Count + aiCount < saved.MinimumPlayers || saved.Players.Count + aiCount > saved.Capacity ||
                aiCount > 0 && room != saved.Players.Count + aiCount || room != 0 && (room < saved.Players.Count || room > saved.Capacity))
                throw new InvalidDataException("방 저장 연결이 잘못됐습니다.");
        }
        else if (saved.MinimumPlayers != 0 || saved.Capacity != 0 || room != 0 || aiCount != 0) throw new InvalidDataException("일반 월드의 방 필드가 잘못됐습니다.");
        var owners = new HashSet<int>();
        foreach ((string account, int owner) in saved.Players)
            if (!Guid.TryParseExact(account, "N", out Guid guid) || guid == Guid.Empty || guid.ToString("N") != account || owner <= 0 || owner >= saved.NextPlayerId || !owners.Add(owner))
                throw new InvalidDataException("참가 계정 연결이 잘못됐습니다.");
        foreach (int owner in saved.AiOwners ?? [])
            if (owner <= 0 || owner >= saved.NextPlayerId || !owners.Add(owner)) throw new InvalidDataException("AI 소유자 연결이 잘못됐습니다.");
        if (saved.World["Players"] is not JArray worldPlayers || worldPlayers.Any(player => player is not JObject || player["Id"]?.Type != JTokenType.Integer))
            throw new InvalidDataException("저장된 플레이어 목록이 잘못됐습니다.");
        if (worldPlayers.Any(player => (int)player["Id"]! >= saved.NextPlayerId))
            throw new InvalidDataException("다음 플레이어 번호가 충돌합니다.");
    }

    private static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings
    { MissingMemberHandling = MissingMemberHandling.Error, TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore, Culture = CultureInfo.InvariantCulture });

    [JsonObject(ItemRequired = Required.Always)]
    private sealed class Envelope
    {
        public int Format = 0;
        public string DataHash = "";
        public LegacySave Save = null!;
    }
    [JsonObject(ItemRequired = Required.Always)]
    private sealed class LegacySave
    {
        public string WorldId = "";
        public JObject World = null!;
        public Dictionary<string, int> Players = null!;
        public int NextPlayerId = 0;
        [JsonProperty(Required = Required.AllowNull)] public string? Title = null;
        public int MinimumPlayers = 0, Capacity = 0;
        [JsonProperty(Required = Required.Default)] public int LanPort = 0;
        [JsonProperty(Required = Required.Default)] public int[]? AiOwners = null;
        [JsonProperty(Required = Required.Default)] public AiDifficulty[]? AiDifficulties = null;
    }
}
