using System;
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
        public bool Ghost { get; }
        public byte SquadId { get; }
        public byte DestinationSquadId { get; }
        public bool IsReinforcing { get; }
        public uint MovementRevision { get; }
        public float AttackSpeedMultiplier { get; }
        public float RangeMultiplier { get; }
        public ushort StatusMask { get; }
        internal UnitView(UnitState unit, float maxHealth, double time = 0, bool own = false)
        {
            Id = unit.Id; OwnerId = unit.OwnerId; Element = unit.Element; Position = unit.Position;
            Health = unit.Health; MaxHealth = maxHealth; Shield = unit.Shield; Converted = unit.Converted;
            Garrison = unit.Garrison; AttackSpeedMultiplier = unit.SpeedMultiplier; RangeMultiplier = unit.RangeMultiplier;
            Ghost = unit.Ghost;
            SquadId = unit.SquadId; DestinationSquadId = own ? unit.DestinationSquadId : (byte)0; IsReinforcing = unit.IsReinforcing; MovementRevision = unit.MovementRevision;
            int statuses=0;
            for(int index=0;index<unit.Statuses.Length;index++) if(unit.Statuses[index].Expires>time) statuses|=1<<index;
            if(unit.Haste>0) statuses|=1<<7;
            if(unit.SmokeDebuff<0) statuses|=1<<6;
            if(unit.SteamDebuff<0) statuses|=1<<8;
            if(unit.SandDebuff<0) statuses|=1<<9;
            StatusMask=(ushort)statuses;
        }
    }

    /// <summary>현재 시야에 있는 핵의 값만 전달한다.</summary>
    public readonly struct CoreView
    {
        public int Id { get; }
        public int Amount { get; }
        public Vector2 Position { get; }
        public long EndTick { get; }
        public CoreView(int id,int amount,Vector2 position,long endTick) {Id=id;Amount=amount;Position=position;EndTick=endTick;}
        internal CoreView(CoreState core,int tickRate) : this(core.Id,core.Amount,core.Position,(long)Math.Ceiling(core.Expires*tickRate)) { }
    }

    /// <summary>자기 상태와 시야 안의 정보만 담는다. 내용은 다음 Step에서 갱신된다.</summary>
    public sealed class WorldView
    {
        internal readonly List<UnitView> UnitBuffer = new List<UnitView>();
        internal readonly List<CoreView> CoreBuffer = new List<CoreView>();
        internal readonly List<SimEvent> EventBuffer = new List<SimEvent>();
        internal readonly List<BuildingView> BuildingBuffer = new List<BuildingView>();
        internal readonly List<AreaView> AreaBuffer = new List<AreaView>();
        internal readonly List<OwnerColorView> OwnerColorBuffer = new List<OwnerColorView>();
        internal readonly List<TrainingOrderView> TrainingQueueBuffer = new List<TrainingOrderView>();
        internal readonly SquadView[] SquadBuffer = new SquadView[4];
        public ReadOnlyCollection<SquadView> Squads { get; }
        public byte FocusAnchor { get; internal set; } = 1;
        public byte RecallSquad { get; internal set; }
        public long RecallEndTick { get; internal set; }
        public long RecallReadyTick { get; internal set; }
        public ReadOnlyCollection<Vector2> RecallArrivalSlots { get; internal set; } = Array.AsReadOnly(Array.Empty<Vector2>());
        internal int[] ElementBuffer = Array.Empty<int>();
        private List<Vector2> visionSources;
        private CameraSight? cameraSight;
        private float radiusSquared, mapSize;
        public ReadOnlyCollection<UnitView> Units { get; }
        public ReadOnlyCollection<CoreView> Cores { get; }
        public ReadOnlyCollection<SimEvent> Events { get; }
        public ReadOnlyCollection<BuildingView> Buildings { get; }
        public ReadOnlyCollection<AreaView> Areas { get; }
        public ReadOnlyCollection<int> ElementCounts { get; private set; }
        public int GarrisonCount { get; internal set; }
        public int RallyingCount { get; internal set; }
        public bool IsRallying => RallyingCount > 0;
        public long GarrisonArrivals { get; internal set; }
        public int TrainingElement { get; internal set; } = -1;
        public byte TrainingTargetSquad { get; internal set; }
        public long TrainingStartTick { get; internal set; }
        public long TrainingEndTick { get; internal set; }
        public ReadOnlyCollection<TrainingOrderView> TrainingQueue { get; }
        public long TrainedCount { get; internal set; }
        public long ConvertedCount { get; internal set; }
        public int PlayerId { get; internal set; }
        public long Tick { get; internal set; }
        public Vector2 LegionPosition { get; internal set; }
        public Vector2 Direction { get; internal set; }
        public ulong AppliedSequence { get; internal set; }
        public Vector2 BasePosition { get; internal set; }
        public VisionGrid Vision { get; internal set; }
        public int MainElement { get; internal set; }
        public int Population { get; internal set; }
        public int PopulationCap { get; internal set; }
        public int ReservedPopulation { get; internal set; }
        public long CoreBalance { get; internal set; }
        public bool IsTraining { get; internal set; }
        public bool NatureForge { get; internal set; }
        public bool EnergyForge { get; internal set; }
        public int BaseLevel { get; internal set; }
        public int OwnBuildingCount { get; internal set; }
        public long BattleWins { get; internal set; }
        public IReadOnlyList<OwnerColorView> OwnerColors { get; }
        public IReadOnlyList<PlayerStanding> Standings { get; internal set; } = Array.Empty<PlayerStanding>();
        internal WorldView()
        {
            Units=UnitBuffer.AsReadOnly(); Cores=CoreBuffer.AsReadOnly(); Events=EventBuffer.AsReadOnly();
            Buildings=BuildingBuffer.AsReadOnly(); Areas=AreaBuffer.AsReadOnly(); ElementCounts=Array.AsReadOnly(ElementBuffer);
            TrainingQueue = TrainingQueueBuffer.AsReadOnly();
            OwnerColors = OwnerColorBuffer.AsReadOnly();
            Squads = Array.AsReadOnly(SquadBuffer);
        }
        internal void ConfigureObservation(ElementalLegion.Shared.Data.GameRules rules,List<Vector2> sources, CameraSight? sight)
        {
            visionSources=sources;radiusSquared=rules.VisionRadius*rules.VisionRadius;mapSize=rules.MapSize;
            cameraSight = sight;
            if(ElementBuffer.Length==rules.Elements.Count)return;
            ElementBuffer=new int[rules.Elements.Count];ElementCounts=Array.AsReadOnly(ElementBuffer);
        }
        /// <summary>격자 보간 없이 서버의 카메라와 원형 시야를 조회한다.</summary>
        public bool CanSeePoint(Vector2 point)
        {
            if(!(point.X>=0&&point.Y>=0&&point.X<=mapSize&&point.Y<=mapSize)||visionSources==null)return false;
            if (cameraSight.HasValue && cameraSight.Value.Contains(point)) return true;
            foreach(Vector2 source in visionSources)if(Vector2.DistanceSquared(source,point)<=radiusSquared)return true;
            return false;
        }
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
