using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    public enum CommandKind { MoveDir, Train, Rally, Build, UpgradeBase, CancelTraining, MoveTo, Transfer, Focus, Recall, AcknowledgeJourney = 10, QuickTransfer = 11, UpgradeFacility = 12 }
    public enum CommandAdmission { Accepted, UnknownPlayer, InvalidInput, StaleSequence, RateLimited, QueueFull }
    public enum CommandFailure { None, InsufficientCores, PopulationCap, Busy, NotUnlocked, InvalidPlacement, BuildingLimit, MaxLevel, TrainingNotWaiting, RecallInterrupted }
    public enum SimEventKind { CommandRejected, Attack, Reaction, Death, Converted, CoreDropped, CorePickedUp, Trained, Healed, ShieldPulse, Impact, CommandApplied, BaseUnderAttack, Rescued, CoresProduced }

    /// <summary>위치나 결과 대신 의도만 전달한다. 소유자는 호출자가 인증한 세션에서 따로 전달한다.</summary>
    public readonly struct SimCommand
    {
        public CommandKind Kind { get; }
        public ulong Sequence { get; }
        public Vector2 Direction { get; }
        public string Element { get; }
        public BuildingKind BuildingKind { get; }
        public int CellX { get; }
        public int CellZ { get; }
        public int TowerElement { get; }
        public ulong TrainingOrderId { get; }
        public byte SquadMask { get; }
        public byte FromSquad { get; }
        public byte TargetSquad { get; }
        public int Count { get; }
        public int BuildingId { get; }
        public FacilityUpgradeBranch UpgradeBranch { get; }
        public byte TargetRank { get; }
        private SimCommand(CommandKind kind, ulong sequence, Vector2 direction, string element, BuildingKind buildingKind = default, int cellX = 0, int cellZ = 0, int towerElement = -1, ulong trainingOrderId = 0, byte squadMask = 0, byte fromSquad = 0, byte targetSquad = 0, int count = 0,
            int buildingId = 0, FacilityUpgradeBranch upgradeBranch = default, byte targetRank = 0)
        {
            Kind = kind; Sequence = sequence; Direction = direction; Element = element;
            BuildingKind = buildingKind; CellX = cellX; CellZ = cellZ; TowerElement = towerElement;
            TrainingOrderId = trainingOrderId;
            SquadMask = squadMask; FromSquad = fromSquad; TargetSquad = targetSquad; Count = count;
            BuildingId = buildingId; UpgradeBranch = upgradeBranch; TargetRank = targetRank;
        }
        public static SimCommand Move(ulong sequence, Vector2 direction) => Move(sequence, 1, direction);
        public static SimCommand Move(ulong sequence, byte mask, Vector2 direction) => new SimCommand(CommandKind.MoveDir, sequence, direction, null, squadMask: mask);
        public static SimCommand MoveTo(ulong sequence, byte mask, Vector2 destination) => new SimCommand(CommandKind.MoveTo, sequence, destination, null, squadMask: mask);
        public static SimCommand Transfer(ulong sequence, byte from, byte to, int count) => new SimCommand(CommandKind.Transfer, sequence, Vector2.Zero, null, fromSquad: from, targetSquad: to, count: count);
        /// <summary>서버가 정한 한도까지 출발 분대의 합류 병력을 목적 분대로 보낸다.</summary>
        public static SimCommand QuickTransfer(ulong sequence, byte from, byte to)
            => new SimCommand(CommandKind.QuickTransfer, sequence, Vector2.Zero, null, fromSquad: from, targetSquad: to);
        public static SimCommand Focus(ulong sequence, byte anchor) => new SimCommand(CommandKind.Focus, sequence, Vector2.Zero, null, targetSquad: anchor);
        public static SimCommand Recall(ulong sequence, byte squad) => new SimCommand(CommandKind.Recall, sequence, Vector2.Zero, null, targetSquad: squad);
        public static SimCommand AcknowledgeJourney(ulong sequence) => new SimCommand(CommandKind.AcknowledgeJourney, sequence, Vector2.Zero, null);
        public static SimCommand Train(ulong sequence, string element) => Train(sequence, element, 1, 4);
        public static SimCommand Train(ulong sequence, string element, int count, byte targetSquad)
            => new SimCommand(CommandKind.Train, sequence, Vector2.Zero, element, targetSquad: targetSquad, count: count);
        public static SimCommand CancelTraining(ulong sequence, ulong orderId)
            => new SimCommand(CommandKind.CancelTraining, sequence, Vector2.Zero, null, trainingOrderId: orderId);
        public static SimCommand Rally(ulong sequence) => Rally(sequence, 1, 0);
        public static SimCommand Rally(ulong sequence, byte target, int count) => new SimCommand(CommandKind.Rally, sequence, Vector2.Zero, null, targetSquad: target, count: count);
        public static SimCommand Build(ulong sequence, BuildingKind kind, int cellX, int cellZ, int towerElement = -1)
            => new SimCommand(CommandKind.Build, sequence, Vector2.Zero, null, kind, cellX, cellZ, towerElement);
        public static SimCommand UpgradeBase(ulong sequence) => new SimCommand(CommandKind.UpgradeBase, sequence, Vector2.Zero, null);
        public static SimCommand UpgradeFacility(ulong sequence, int buildingId, FacilityUpgradeBranch branch, byte targetRank)
            => new SimCommand(CommandKind.UpgradeFacility, sequence, Vector2.Zero, null,
                buildingId: buildingId, upgradeBranch: branch, targetRank: targetRank);
    }

    /// <summary>확정된 한 틱의 결과다. 시야 조회에서는 숨겨진 원인 유닛 ID를 제거한다.</summary>
    public readonly struct SimEvent
    {
        public SimEventKind Kind { get; }
        public int OwnerId { get; }
        public int SourceId { get; }
        public int TargetId { get; }
        public Vector2 Position { get; }
        public float Amount { get; }
        public int Detail { get; }
        public ulong Sequence { get; }
        public int Element { get; }
        public Vector2 SourcePosition { get; }
        public bool HasSourcePosition { get; }
        public int EntityId { get; }
        public ObservedEntityKind EntityKind { get; }
        public ObservedEntityKind SourceKind { get; }
        public ObservedEntityKind TargetKind { get; }
        internal SimEvent(SimEventKind kind, int owner, int source, int target, Vector2 position, float amount = 0, int detail = 0, ulong sequence = 0,
            int element=-1,Vector2 sourcePosition=default,bool hasSourcePosition=false,int entityId=0,ObservedEntityKind entityKind=ObservedEntityKind.Unit,
            ObservedEntityKind sourceKind=ObservedEntityKind.Unit,ObservedEntityKind targetKind=ObservedEntityKind.Unit)
        {
            Kind=kind;OwnerId=owner;SourceId=source;TargetId=target;Position=position;Amount=amount;Detail=detail;Sequence=sequence;
            Element=element;SourcePosition=sourcePosition;HasSourcePosition=hasSourcePosition;EntityId=entityId;EntityKind=entityKind;
            SourceKind=sourceKind;TargetKind=targetKind;
        }
    }
}
