using System.Globalization;
using System.Text;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;

namespace ElementalLegion.Server;

/// <summary>월드 상태와 서버가 발급한 참가 계정 연결을 함께 보관한다.</summary>
[JsonObject(MemberSerialization.OptIn, ItemRequired = Required.Always)]
public sealed record WorldSave(
    [property: JsonProperty] string WorldId,
    [property: JsonProperty] WorldCheckpoint World,
    [property: JsonProperty] Dictionary<string, int> Players,
    [property: JsonProperty] int NextPlayerId,
    [property: JsonProperty(Required = Required.AllowNull)] string? Title = null,
    [property: JsonProperty] int MinimumPlayers = 0,
    [property: JsonProperty] int Capacity = 0,
    [property: JsonProperty(Required = Required.Default, DefaultValueHandling = DefaultValueHandling.Ignore)] int LanPort = 0,
    [property: JsonProperty(Required = Required.Default, NullValueHandling = NullValueHandling.Ignore)] int[]? AiOwners = null)
{
    public bool IsRoom => Title != null;
}

public interface IWorldStore
{
    void Save(WorldSave checkpoint);
    WorldSave? Load(string worldId);
    IEnumerable<string> ListIds();
    void Close(string worldId);
}

/// <summary>한 서버가 소유하는 저장 폴더에서 정상본, 백업과 종료 기록을 교체한다.</summary>
public sealed class FileWorldStore : IWorldStore
{
    private const int Format = 1;
    private const long MaxSaveBytes = 32L * 1024 * 1024;
    private const string Prefix = "world-";
    private const string Extension = ".json";
    private static readonly UTF8Encoding Encoding = new(false, true);
    private readonly string directory;
    private readonly GameRules rules;
    private readonly object gate = new();
    private readonly JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        Culture = CultureInfo.InvariantCulture,
        DateParseHandling = DateParseHandling.None,
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Error,
        TypeNameHandling = TypeNameHandling.None
    });

    public FileWorldStore(string directory, GameRules rules)
    {
        this.directory = Path.GetFullPath(directory);
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        Directory.CreateDirectory(this.directory);
    }

    public void Save(WorldSave checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        string path = SavePath(checkpoint.WorldId);
        lock (gate)
        {
            if (File.Exists(path + ".closed")) throw new InvalidOperationException("종료한 방은 저장할 수 없습니다.");
            if (!string.Equals(checkpoint.World?.DataHash, rules.DataHash, StringComparison.Ordinal))
                throw new InvalidDataException("저장할 월드의 게임 데이터가 현재 서버와 다릅니다.");
            string temporary = TemporaryPath(path);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var output = new StreamWriter(stream, Encoding, leaveOpen: true))
                    using (var json = new JsonTextWriter(output))
                    {
                        json.WriteStartObject();
                        json.WritePropertyName("Format");
                        json.WriteValue(Format);
                        json.WritePropertyName("DataHash");
                        json.WriteValue(rules.DataHash);
                        json.WritePropertyName("Save");
                        serializer.Serialize(json, checkpoint);
                        json.WriteEndObject();
                    }
                    stream.Flush(flushToDisk: true);
                }
                _ = Read(temporary, checkpoint.WorldId) ?? throw new InvalidDataException("임시 월드 파일을 검증하지 못했습니다.");

                bool primaryExists = File.Exists(path);
                string backup = path + ".bak";
                // 임시 파일과 정상본은 모두 서버 전용 폴더의 권한을 상속한다.
                if (primaryExists && Read(path, checkpoint.WorldId) != null)
                    File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                else
                {
                    // 손상된 본문을 교체하더라도 직전 정상 백업은 그대로 둔다.
                    if ((primaryExists || File.Exists(backup)) && Read(backup, checkpoint.WorldId) == null)
                        throw new InvalidDataException("기존 월드의 정상본 또는 백업을 검증하지 못했습니다.");
                    if (primaryExists) File.Replace(temporary, path, null, ignoreMetadataErrors: true);
                    else File.Move(temporary, path);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    public WorldSave? Load(string worldId)
    {
        string path = SavePath(worldId);
        lock (gate)
        {
            if (File.Exists(path + ".closed")) return null;
            WorldSave? saved = Read(path, worldId) ?? Read(path + ".bak", worldId);
            if (saved != null || !File.Exists(path) && !File.Exists(path + ".bak")) return saved;
            throw new InvalidDataException("월드 저장 파일과 백업에서 복원할 수 없습니다.");
        }
    }

    public IEnumerable<string> ListIds()
    {
        lock (gate)
        {
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(directory, Prefix + "*"))
            {
                string file = Path.GetFileName(path);
                string extension = file.EndsWith(Extension + ".bak", StringComparison.Ordinal) ? Extension + ".bak" : Extension;
                if (!file.EndsWith(extension, StringComparison.Ordinal)) continue;
                string id = file[Prefix.Length..^extension.Length];
                if (ValidId(id) && !File.Exists(SavePath(id) + ".closed")) ids.Add(id);
            }
            return ids;
        }
    }

    public void Close(string worldId)
    {
        string path = SavePath(worldId) + ".closed";
        lock (gate)
        {
            if (File.Exists(path)) return;
            string temporary = TemporaryPath(path);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(1);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private WorldSave? Read(string path, string worldId)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0 || stream.Length > MaxSaveBytes) return null;
            using var input = new StreamReader(stream, Encoding, detectEncodingFromByteOrderMarks: false);
            using var json = new JsonTextReader(input) { MaxDepth = 24 };
            if (!json.Read() || json.TokenType != JsonToken.StartObject ||
                !ReadProperty(json, "Format") || json.TokenType != JsonToken.Integer) return null;
            if (json.Value is not long version || version != Format)
                throw new InvalidDataException("현재 서버가 지원하지 않는 월드 저장 형식입니다.");
            if (!ReadProperty(json, "DataHash") || json.Value is not string hash ||
                hash.Length != rules.DataHash.Length || !hash.All(Uri.IsHexDigit)) return null;
            if (!string.Equals(hash, rules.DataHash, StringComparison.Ordinal))
                throw new InvalidDataException("저장된 월드의 게임 데이터가 현재 서버와 다릅니다.");
            if (!ReadProperty(json, "Save") || json.TokenType != JsonToken.StartObject) return null;
            WorldSave? saved = serializer.Deserialize<WorldSave>(json);
            if (!json.Read() || json.TokenType != JsonToken.EndObject || json.Read() || !ValidSave(saved, worldId)) return null;
            SimulationWorld.RestoreCheckpoint(rules, saved!.World);
            return saved.NextPlayerId > saved.World.MaxPlayerId ? saved : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
        catch (OverflowException) { return null; }
    }

    private bool ValidSave(WorldSave? saved, string id)
    {
        if (saved == null || saved.WorldId != id || saved.World?.DataHash != rules.DataHash ||
            saved.Players == null || saved.Players.Count > rules.MaxPlayers || saved.NextPlayerId <= 0 ||
            saved.LanPort < 0 || saved.LanPort > ushort.MaxValue || saved.LanPort != 0 && !saved.IsRoom) return false;
        int aiCount = saved.AiOwners?.Length ?? 0;
        if (saved.IsRoom)
        {
            if (saved.Title!.Length is < 1 or > 32 || saved.Title != saved.Title.Trim() || saved.Title.Any(char.IsControl) ||
                saved.MinimumPlayers < 1 || saved.MinimumPlayers > saved.Capacity || saved.Capacity > rules.MaxPlayers ||
                saved.Players.Count < 1 || saved.Players.Count + aiCount < saved.MinimumPlayers ||
                saved.Players.Count + aiCount > saved.Capacity) return false;
            if (aiCount > 0 && saved.World.RoomPlayers != saved.Players.Count + aiCount) return false;
            if (saved.World.RoomPlayers != 0 && (saved.World.RoomPlayers < saved.Players.Count || saved.World.RoomPlayers > saved.Capacity))
                return false;
        }
        else if (saved.MinimumPlayers != 0 || saved.Capacity != 0 || saved.World.RoomPlayers != 0 || aiCount != 0) return false;
        var owners = new HashSet<int>();
        foreach ((string account, int owner) in saved.Players)
            if (!Guid.TryParseExact(account, "N", out Guid guid) || guid == Guid.Empty || guid.ToString("N") != account ||
                owner <= 0 || owner >= saved.NextPlayerId || !owners.Add(owner)) return false;
        if (saved.AiOwners != null)
            foreach (int owner in saved.AiOwners)
                if (owner <= 0 || owner >= saved.NextPlayerId || !owners.Add(owner)) return false;
        return true;
    }

    private static bool ReadProperty(JsonTextReader reader, string property)
        => reader.Read() && reader.TokenType == JsonToken.PropertyName && (string?)reader.Value == property && reader.Read();

    private string SavePath(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("월드 저장 ID가 잘못됐습니다.", nameof(id));
        return Path.Combine(directory, Prefix + id + Extension);
    }

    private static bool ValidId(string id) => id is { Length: >= 1 and <= 64 } &&
        id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static string TemporaryPath(string path) => path + "." + Guid.NewGuid().ToString("N") + ".tmp";
}
