using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>현재 시야에서 관측한 유닛 값이다. 가변 월드 객체를 노출하지 않는다.</summary>
    public readonly struct UnitView
    {
        public int Id { get; }
        public int OwnerId { get; }
        public int Element { get; }
        public Vector2 Position { get; }
        public float Health { get; }
        public float MaxHealth { get; }
        public float Shield { get; }
        public bool Converted { get; }
        public bool Garrison { get; }
        public float AttackSpeedMultiplier { get; }
        public float RangeMultiplier { get; }
        internal UnitView(UnitState unit, float maxHealth)
        {
            Id = unit.Id; OwnerId = unit.OwnerId; Element = unit.Element; Position = unit.Position;
            Health = unit.Health; MaxHealth = maxHealth; Shield = unit.Shield; Converted = unit.Converted;
            Garrison = unit.Garrison; AttackSpeedMultiplier = unit.SpeedMultiplier; RangeMultiplier = unit.RangeMultiplier;
        }
    }

    /// <summary>현재 시야에 있는 핵의 값만 전달한다.</summary>
    public readonly struct CoreView
    {
        public int Id { get; }
        public int Amount { get; }
        public Vector2 Position { get; }
        internal CoreView(CoreState core) { Id = core.Id; Amount = core.Amount; Position = core.Position; }
    }

    /// <summary>자기 상태와 시야 안의 정보만 담는다. 내용은 다음 Step에서 갱신된다.</summary>
    public sealed class WorldView
    {
        internal readonly List<UnitView> UnitBuffer = new List<UnitView>();
        internal readonly List<CoreView> CoreBuffer = new List<CoreView>();
        internal readonly List<SimEvent> EventBuffer = new List<SimEvent>();
        public ReadOnlyCollection<UnitView> Units { get; }
        public ReadOnlyCollection<CoreView> Cores { get; }
        public ReadOnlyCollection<SimEvent> Events { get; }
        public int PlayerId { get; internal set; }
        public long Tick { get; internal set; }
        public Vector2 LegionPosition { get; internal set; }
        public Vector2 BasePosition { get; internal set; }
        public int MainElement { get; internal set; }
        public int Population { get; internal set; }
        public int PopulationCap { get; internal set; }
        public int ReservedPopulation { get; internal set; }
        public long CoreBalance { get; internal set; }
        public bool IsTraining { get; internal set; }
        public bool NatureForge { get; internal set; }
        public bool EnergyForge { get; internal set; }
        public int BaseLevel { get; internal set; }
        internal WorldView() { Units = UnitBuffer.AsReadOnly(); Cores = CoreBuffer.AsReadOnly(); Events = EventBuffer.AsReadOnly(); }
    }

    /// <summary>서버 전용 집계다. 봇 관측이나 네트워크 전송 자료로 사용하지 않는다.</summary>
    public sealed class WorldMetrics
    {
        internal readonly long[] StageBuffer = new long[8];
        internal readonly long[] ReactionBuffer = new long[6];
        public ReadOnlyCollection<long> StageTicks { get; }
        public ReadOnlyCollection<long> Reactions { get; }
        public long Tick { get; internal set; }
        public int PlayerUnits { get; internal set; }
        public int NeutralUnits { get; internal set; }
        public int DroppedCores { get; internal set; }
        public long Attacks { get; internal set; }
        public long Deaths { get; internal set; }
        public long Conversions { get; internal set; }
        public long Drops { get; internal set; }
        public long Pickups { get; internal set; }
        public long Trained { get; internal set; }
        internal WorldMetrics() { StageTicks = System.Array.AsReadOnly(StageBuffer); Reactions = System.Array.AsReadOnly(ReactionBuffer); }
    }
}
