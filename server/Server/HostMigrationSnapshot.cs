using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;

namespace ElementalLegion.Server;

/// <summary>계정 비밀값 없이 한 인터넷 방의 대기실·전장·명령 상태를 이전한다.</summary>
[JsonObject(MemberSerialization.OptIn)]
internal sealed class HostMigrationSnapshot
{
    [JsonProperty(Required = Required.Always)] public int Format { get; init; } = 1;
    [JsonProperty(Required = Required.Always)] public int Version { get; init; } = Protocol.Version;
    [JsonProperty(Required = Required.Always)] public string DataHash { get; init; } = "";
    [JsonProperty(Required = Required.Always)] public string RoomId { get; init; } = "";
    [JsonProperty(Required = Required.Always)] public ulong Generation { get; init; }
    [JsonProperty(Required = Required.Always)] public ulong BackupNumber { get; init; }
    [JsonProperty(Required = Required.Always)] public long Tick { get; init; }
    [JsonProperty(Required = Required.Always)] public string Title { get; init; } = "";
    [JsonProperty(Required = Required.Always)] public int MinimumPlayers { get; init; }
    [JsonProperty(Required = Required.Always)] public int Capacity { get; init; }
    [JsonProperty(Required = Required.Always)] public int AiCount { get; init; }
    [JsonProperty] public AiDifficulty[]? AiDifficulties { get; init; }
    [JsonProperty] public int[]? AiColorIds { get; init; }
    [JsonProperty(Required = Required.Always)] public int NextPlayerId { get; init; } = 1;
    [JsonProperty(Required = Required.Always)] public MigrationMember[] Members { get; init; } = [];
    [JsonProperty(Required = Required.Always)] public Dictionary<string, int> Players { get; init; } = new(StringComparer.Ordinal);
    [JsonProperty(Required = Required.AllowNull)] public MigrationWorldState? World { get; init; }
    [JsonProperty(Required = Required.Always)] public BotBrainState[] Ai { get; init; } = [];
    [JsonProperty(Required = Required.Always)] public ChatMigrationState Chat { get; init; } = new();
}

[JsonObject(MemberSerialization.OptIn, ItemRequired = Required.Always)]
internal sealed record MigrationMember(
    [property: JsonProperty] string AccountId,
    [property: JsonProperty] string Nickname,
    [property: JsonProperty] int Element,
    [property: JsonProperty] bool Ready,
    [property: JsonProperty] int Owner,
    [property: JsonProperty(Required = Required.Default)] int ColorId = -1);

/// <summary>다른 PC에서도 단조 시계의 원점에 의존하지 않고 채팅 제한을 복원한다.</summary>
[JsonObject(MemberSerialization.OptIn)]
internal sealed class ChatMigrationState
{
    [JsonProperty(Required = Required.Always)] public ulong NextId { get; init; }
    [JsonProperty(Required = Required.Always)] public ChatCooldown[] Cooldowns { get; init; } = [];
    [JsonProperty(Required = Required.Always)] public ChatDelivery[] History { get; init; } = [];
}

[JsonObject(MemberSerialization.OptIn, ItemRequired = Required.Always)]
internal sealed record ChatCooldown(
    [property: JsonProperty] int Owner,
    [property: JsonProperty] bool Global,
    [property: JsonProperty] long RemainingMilliseconds);

[JsonObject(MemberSerialization.OptIn, ItemRequired = Required.Always)]
internal sealed record ChatDelivery(
    [property: JsonProperty] ulong Id,
    [property: JsonProperty] int Owner,
    [property: JsonProperty] string Text,
    [property: JsonProperty] int[] Recipients,
    [property: JsonProperty] long AgeMilliseconds);
