using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>쓰러진 원소의 신원과 막타 당시 권한 및 독립된 예약 수명을 보존한다.</summary>
    internal struct ConversionCandidateState
    {
        internal int Id, Element, PreviousOwner, SecureOwner, Source;
        internal byte SecureSquad;
        internal Vector2 Position;
        internal long CreatedTick, EndTick;
        internal int SecureProgress, RescueProgress;
        internal bool Converted, RescuedOnce, WasNeutral, SecureAuthorityLost;
        internal uint MovementRevision;
    }
}
