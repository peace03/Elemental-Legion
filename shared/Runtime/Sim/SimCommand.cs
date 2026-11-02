using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    public enum CommandKind { MoveDir, Train, Rally }
    public enum CommandAdmission { Accepted, UnknownPlayer, InvalidInput, StaleSequence, RateLimited, QueueFull }
    public enum CommandFailure { None, InsufficientCores, PopulationCap, Busy, NotUnlocked }
    public enum SimEventKind { CommandRejected, Attack, Reaction, Death, Converted, CoreDropped, CorePickedUp, Trained, Healed, ShieldPulse, Impact }

    /// <summary>위치나 결과 대신 의도만 전달한다. 소유자는 호출자가 인증한 세션에서 따로 전달한다.</summary>
    public readonly struct SimCommand
    {
        public CommandKind Kind { get; }
        public ulong Sequence { get; }
        public Vector2 Direction { get; }
        public string Element { get; }
        private SimCommand(CommandKind kind, ulong sequence, Vector2 direction, string element)
        { Kind = kind; Sequence = sequence; Direction = direction; Element = element; }
        public static SimCommand Move(ulong sequence, Vector2 direction) => new SimCommand(CommandKind.MoveDir, sequence, direction, null);
        public static SimCommand Train(ulong sequence, string element) => new SimCommand(CommandKind.Train, sequence, Vector2.Zero, element);
        public static SimCommand Rally(ulong sequence) => new SimCommand(CommandKind.Rally, sequence, Vector2.Zero, null);
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
        internal SimEvent(SimEventKind kind, int owner, int source, int target, Vector2 position, float amount = 0, int detail = 0, ulong sequence = 0,
            int element=-1,Vector2 sourcePosition=default,bool hasSourcePosition=false,int entityId=0,ObservedEntityKind entityKind=ObservedEntityKind.Unit)
        {
            Kind=kind;OwnerId=owner;SourceId=source;TargetId=target;Position=position;Amount=amount;Detail=detail;Sequence=sequence;
            Element=element;SourcePosition=sourcePosition;HasSourcePosition=hasSourcePosition;EntityId=entityId;EntityKind=entityKind;
        }
    }
}
