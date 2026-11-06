using System.Numerics;
using Newtonsoft.Json;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>봇의 중복 판단 방지·명령 순번과 순찰 위치를 승계한다.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class BotBrainState
    {
        [JsonProperty(Required = Required.Always)] public int PlayerId { get; internal set; }
        [JsonProperty(Required = Required.Always)] internal bool PlayMatch, HasDirection;
        [JsonProperty(Required = Required.Always)] internal ulong Sequence;
        [JsonProperty(Required = Required.Always)] internal long LastTick;
        [JsonProperty(Required = Required.Always)] internal Vector2 PreviousDirection;
        [JsonProperty(Required = Required.Always)] internal int Patrol;
        [JsonProperty] public AiDifficulty Difficulty { get; internal set; } = AiDifficulty.Normal;
        [JsonProperty] internal bool Retreating;
    }
}
