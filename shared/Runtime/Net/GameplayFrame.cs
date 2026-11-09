using System;
using System.Collections.Generic;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Shared.Net
{
    /// <summary>종류별 ID 공간을 구분한다. 건물·영역의 ID가 유닛과 같아도 충돌하지 않는다.</summary>
    public readonly struct ObservedEntityId : IEquatable<ObservedEntityId>
    {
        public ObservedEntityKind Kind { get; }
        public int Id { get; }
        public ObservedEntityId(ObservedEntityKind kind,int id) { Kind=kind;Id=id; }
        public bool Equals(ObservedEntityId other)=>Kind==other.Kind&&Id==other.Id;
        public override bool Equals(object value)=>value is ObservedEntityId other&&Equals(other);
        public override int GetHashCode()=>unchecked(Id*397^(int)Kind);
    }

    /// <summary>연결별 중복 방지 번호와 확정 틱을 가진, 이미 시야로 거른 사건이다.</summary>
    public readonly struct NetworkEvent
    {
        public ulong Serial { get; }
        public long Tick { get; }
        public SimEvent Data { get; }
        internal NetworkEvent(ulong serial,long tick,SimEvent data) { Serial=serial;Tick=tick;Data=data; }
    }

    /// <summary>재사용 수신 프레임이다. 구독자는 다음 적용 전에 필요한 값을 소비한다.</summary>
    public sealed class GameplayFrame
    {
        internal readonly int[] Composition;
        internal readonly BuildingView[] BuildingBuffer=new BuildingView[GameplayCodec.MaxBuildings];
        internal readonly CoreView[] CoreBuffer=new CoreView[GameplayCodec.MaxObjects];
        internal readonly AreaView[] AreaBuffer=new AreaView[GameplayCodec.MaxObjects];
        internal readonly NetworkEvent[] EventBuffer=new NetworkEvent[GameplayCodec.MaxEvents];
        internal readonly List<PlayerStanding> StandingBuffer;
        internal readonly List<OwnerColorView> OwnerColorBuffer;
        internal readonly List<TrainingOrderView> TrainingQueueBuffer = new List<TrainingOrderView>();
        internal readonly SquadView[] SquadBuffer = new SquadView[4];
        internal readonly System.Numerics.Vector2[] RecallSlotBuffer;
        internal int RecallSlotCount;
        internal int BuildingCount,CoreCount,AreaCount,EventCount;
        public long Tick { get; internal set; }=-1;
        public int PlayerId { get; internal set; }=-1;
        public uint Revision { get; internal set; }
        public bool SpatialReady { get; internal set; }
        public System.Numerics.Vector2 BasePosition { get; internal set; }
        public int MainElement { get; internal set; }
        public int BaseLevel { get; internal set; }
        public long CoreBalance { get; internal set; }
        public int Population { get; internal set; }
        public int ReservedPopulation { get; internal set; }
        public int PopulationCap { get; internal set; }
        public int GarrisonCount { get; internal set; }
        public int RallyingCount { get; internal set; }
        public int WaitingGarrisonCount => GarrisonCount - RallyingCount;
        public bool IsRallying => RallyingCount > 0;
        public long GarrisonArrivals { get; internal set; }
        public int TrainingElement { get; internal set; }=-1;
        public byte TrainingTargetSquad { get; internal set; }
        public long TrainingStartTick { get; internal set; }
        public long TrainingEndTick { get; internal set; }
        public long TrainedCount { get; internal set; }
        public long ConvertedCount { get; internal set; }
        public int OwnBuildingCount { get; internal set; }
        public long BattleWins { get; internal set; }
        public byte FocusAnchor { get; internal set; } = 1;
        public ReadOnlySpan<SquadView> Squads => SquadBuffer;
        public byte RecallSquad { get; internal set; }
        public long RecallEndTick { get; internal set; }
        public long RecallReadyTick { get; internal set; }
        public ReadOnlySpan<System.Numerics.Vector2> RecallArrivalSlots => RecallSlotBuffer.AsSpan(0,RecallSlotCount);
        public IReadOnlyList<PlayerStanding> Standings { get; }
        public IReadOnlyList<OwnerColorView> OwnerColors { get; }
        public IReadOnlyList<TrainingOrderView> TrainingQueue { get; }
        public double? ConvertedShare=>TrainedCount+ConvertedCount==0?(double?)null:(double)ConvertedCount/(TrainedCount+ConvertedCount);
        public ReadOnlySpan<int> ElementCounts=>Composition;
        public ReadOnlySpan<BuildingView> Buildings=>BuildingBuffer.AsSpan(0,BuildingCount);
        public ReadOnlySpan<CoreView> Cores=>CoreBuffer.AsSpan(0,CoreCount);
        public ReadOnlySpan<AreaView> Areas=>AreaBuffer.AsSpan(0,AreaCount);
        public ReadOnlySpan<NetworkEvent> Events=>EventBuffer.AsSpan(0,EventCount);
        internal GameplayFrame(GameRules rules)
        {
            Composition = new int[rules.Elements.Count];
            RecallSlotBuffer = new System.Numerics.Vector2[rules.MaxPopulation];
            OwnerColorBuffer = new List<OwnerColorView>(rules.MaxPlayers);
            OwnerColors = OwnerColorBuffer.AsReadOnly();
            StandingBuffer = new List<PlayerStanding>(rules.MaxPlayers);
            Standings = StandingBuffer.AsReadOnly();
            TrainingQueue = TrainingQueueBuffer.AsReadOnly();
        }
        internal void Clear()
        {
            Tick=-1;PlayerId=-1;Revision=0;SpatialReady=false;BasePosition=default;MainElement=BaseLevel=0;CoreBalance=0;Population=ReservedPopulation=PopulationCap=GarrisonCount=0;
            RallyingCount=0;GarrisonArrivals=0;TrainingElement=-1;TrainingStartTick=TrainingEndTick=TrainedCount=ConvertedCount=0;BuildingCount=CoreCount=AreaCount=EventCount=0;
            OwnBuildingCount=0;BattleWins=0;FocusAnchor=1;StandingBuffer.Clear();OwnerColorBuffer.Clear();TrainingQueueBuffer.Clear();
            Array.Clear(SquadBuffer,0,SquadBuffer.Length);
            RecallSquad=0;RecallEndTick=RecallReadyTick=0;RecallSlotCount=0;
            TrainingTargetSquad=0;
            Array.Clear(Composition,0,Composition.Length);
        }
    }
}
