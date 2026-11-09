using System.Collections.Generic;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    public enum SquadOrderKind : byte { Hold, MoveDir, MoveTo }

    /// <summary>고정 분대 하나의 합류 병력과 독립적인 이동 명령을 보존한다.</summary>
    internal sealed class SquadState
    {
        internal readonly byte Id;
        internal Vector2 Position, Direction, Heading = Vector2.UnitY, Destination;
        internal SquadOrderKind Order;
        internal ulong AppliedSequence, ElementMask;
        internal uint MovementRevision;
        internal bool FormationDirty = true;
        internal bool FollowFormation;
        internal readonly List<UnitState> Units = new List<UnitState>();
        internal SquadState(byte id) { Id = id; }
    }
}
