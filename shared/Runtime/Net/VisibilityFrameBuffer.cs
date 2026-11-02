using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Net
{
    /// <summary>아직 적용하지 않은 시야 revision의 유닛 프레임은 최신 하나만 대기시킨다.</summary>
    public sealed class VisibilityFrameBuffer
    {
        private readonly UnitSnapshotAssembler pending;
        private long delivered = -1;
        public VisibilityFrameBuffer(GameRules rules) { pending = new UnitSnapshotAssembler(rules); }
        public void Reset() { pending.Reset(); delivered = -1; }

        public bool Accept(UnitSnapshotAssembler frame, FogStateReplica fog, out UnitSnapshotAssembler publish)
        {
            publish = null;
            if (frame.Tick <= delivered) return false;
            if (fog.Ready && frame.Revision == fog.Revision && frame.PlayerId == fog.PlayerId)
            { delivered = frame.Tick; publish = frame; pending.Reset(); return true; }
            if ((!fog.Ready || frame.Revision > fog.Revision) && frame.Tick > pending.Tick) pending.CopyCompletedFrom(frame);
            return false;
        }

        public bool Release(FogStateReplica fog, out UnitSnapshotAssembler publish)
        {
            publish = null;
            if (!fog.Ready || pending.Tick <= delivered || pending.Tick < 0) return false;
            if (pending.Revision < fog.Revision) { pending.Reset(); return false; }
            if (pending.Revision != fog.Revision || pending.PlayerId != fog.PlayerId) return false;
            delivered = pending.Tick; publish = pending; return true;
        }
    }
}
