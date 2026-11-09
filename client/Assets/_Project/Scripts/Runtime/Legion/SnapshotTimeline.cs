using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Net;

namespace ElementalLegion.Client.Legion
{
    /// <summary>완성된 관측 프레임을 제한된 링 버퍼에 보관한다. 상대 상태는 보간하고 최신 시야에서 빠진 ID는 즉시 감춘다.</summary>
    public sealed class SnapshotTimeline
    {
        private readonly Frame[] frames;
        private readonly LegionPose[,] poses;
        private readonly int[] poseHeads = { -1, -1, -1, -1 }, poseCounts = new int[4];
        private int head = -1, count;
        private readonly Dictionary<int, long> forgotten = new Dictionary<int, long>();
        private readonly List<int> expired = new List<int>();
        private readonly Dictionary<int, long> dead = new Dictionary<int, long>();
        private readonly Dictionary<int,(long Tick,int Owner,float Health)> conversions = new Dictionary<int,(long,int,float)>();
        private const double PlaybackRateCorrection = 0.2;
        private double? renderClockAt;
        private double renderClockTick;
        public int Count => head < 0 ? 0 : frames[head].Count;
        public long LatestTick => head < 0 ? -1 : frames[head].Tick;

        public SnapshotTimeline(int capacity)
        {
            if (capacity < 2 || capacity > 128) throw new ArgumentOutOfRangeException(nameof(capacity));
            frames = new Frame[capacity];
            poses = new LegionPose[4, capacity];
        }

        /// <summary>관측 시간의 흔들림을 천천히 따라가며 완성된 유닛 틱을 넘지 않는다.</summary>
        public double SampleRenderTick(double observedTick, double receivedAt, double now, double delay, double tickSeconds)
        {
            double target = observedTick + (now - receivedAt - delay) / tickSeconds;
            if (LatestTick < 0) return target;
            if (renderClockAt.HasValue)
            {
                double elapsedTicks = Math.Max(0, now - renderClockAt.Value) / tickSeconds;
                double next = renderClockTick + elapsedTicks;
                double limit = elapsedTicks * PlaybackRateCorrection;
                renderClockTick = Math.Min(LatestTick, next + Math.Clamp(target - next, -limit, limit));
            }
            else renderClockTick = Math.Min(LatestTick, target);
            // 수신이 재개되어 지난 이력이 밀려나면 실제로 보간할 수 있는 구간부터 재생한다.
            if (count == frames.Length)
                renderClockTick = Math.Max(renderClockTick, frames[(head + 1) % frames.Length].Tick);
            renderClockAt = now;
            return renderClockTick;
        }

        public void Push(UnitSnapshotAssembler snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.Tick <= LatestTick) return;
            head = (head + 1) % frames.Length;
            Frame frame = frames[head] ?? (frames[head] = new Frame());
            frame.Tick = snapshot.Tick; frame.Anchor = snapshot.Anchor; frame.Count = snapshot.Count;
            Array.Copy(snapshot.Units, frame.Units, snapshot.Count); frame.ById.Clear();
            for (int index = 0; index < frame.Count; index++)
            {
                NetworkUnit unit = frame.Units[index];
                if(conversions.TryGetValue(unit.Id,out var conversion)&&snapshot.Tick<=conversion.Tick)
                    frame.Units[index]=unit=Converted(unit,conversion.Owner,conversion.Health);
                // 후보에서 복귀한 같은 ID는 사망보다 새로운 권위 관측에서만 다시 표시한다.
                if ((!dead.TryGetValue(unit.Id, out long deathTick) || snapshot.Tick > deathTick)&&
                    (!forgotten.TryGetValue(unit.Id, out long hiddenAt) || snapshot.Tick > hiddenAt)) frame.ById[unit.Id] = unit;
            }
            expired.Clear();
            foreach (var item in forgotten) if (item.Value < snapshot.Tick) expired.Add(item.Key);
            foreach (int id in expired) forgotten.Remove(id);
            expired.Clear();foreach(var item in conversions)if(item.Value.Tick<snapshot.Tick)expired.Add(item.Key);
            foreach(int id in expired)conversions.Remove(id);
            expired.Clear();foreach(var item in dead)if(item.Value<snapshot.Tick)expired.Add(item.Key);
            foreach(int id in expired)dead.Remove(id);
            count = Math.Min(count + 1, frames.Length);
        }

        /// <summary>이전에 본 ID의 표시만 지운다. 더 최신 재관측은 허용하지만 과거 보간 이력은 다시 사용하지 않는다.</summary>
        public void Forget(int id, long tick)
        {
            if (id <= 0 || tick < 0) throw new ArgumentOutOfRangeException(nameof(id));
            if (head >= 0 && frames[head].Tick > tick) return;
            if (forgotten.TryGetValue(id, out long previous) && previous >= tick) return;
            forgotten[id] = tick;
            for (int index = 0; index < frames.Length; index++) if (frames[index] != null) frames[index].ById.Remove(id);
        }

        public NetworkUnit GetLatest(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            return frames[head].Units[index];
        }

        public bool TryGetVisibleLatest(int index, out NetworkUnit unit)
        {
            NetworkUnit candidate = GetLatest(index);
            return frames[head].ById.TryGetValue(candidate.Id, out unit);
        }

        public void ResolveDeath(int id,long tick)
        {
            if(id<=0||tick<0)throw new ArgumentOutOfRangeException(nameof(id));
            if (dead.TryGetValue(id, out long previous) && previous >= tick) return;
            // 과거 사망은 보간 이력만 지우며, 이후에 수락할 관측을 차단할 기록은 남기지 않는다.
            if (tick >= LatestTick) dead[id] = tick;
            if (conversions.TryGetValue(id, out var conversion) && conversion.Tick <= tick) conversions.Remove(id);
            for(int index=0;index<frames.Length;index++)if(frames[index]!=null&&frames[index].Tick<=tick)frames[index].ById.Remove(id);
        }

        public void ResolveConversion(int id,long tick,int owner,float health)
        {
            if(id<=0||tick<0||owner<=0||health<=0)throw new ArgumentOutOfRangeException(nameof(id));
            if(dead.TryGetValue(id,out long deathTick)&&deathTick>=tick||conversions.TryGetValue(id,out var previous)&&previous.Tick>=tick)return;
            conversions[id]=(tick,owner,health);
            foreach(Frame frame in frames)
            {
                if(frame==null||frame.Tick>tick)continue;
                for(int index=0;index<frame.Count;index++)if(frame.Units[index].Id==id)
                {
                    NetworkUnit unit=Converted(frame.Units[index],owner,health);frame.Units[index]=unit;
                    if(frame.ById.ContainsKey(id))frame.ById[id]=unit;
                }
            }
        }

        private static NetworkUnit Converted(NetworkUnit unit,int owner,float health)
        {
            if(unit.OwnerId==owner)return unit;
            // 전향 사건에는 새 분대가 없으므로 다음 유닛 관측 전까지 예측 보정을 제외한다.
            return new NetworkUnit(unit.Id,owner,unit.Element,unit.Position,false,health,0,true,0,squadId:0,movementRevision:unit.MovementRevision);
        }

        public bool TrySample(int id, double tick, out Vector2 position)
        {
            position = default;
            if (head < 0 || !frames[head].ById.TryGetValue(id, out NetworkUnit current)) return false;
            Bounds(tick, out Frame before, out Frame after, out float fraction);
            if (before.ById.TryGetValue(id, out NetworkUnit prior) && (prior.MovementRevision != current.MovementRevision || prior.SquadId != current.SquadId || prior.OwnerId != current.OwnerId || prior.IsReinforcing != current.IsReinforcing)) { position = current.Position; return true; }
            if (!after.ById.TryGetValue(id, out NetworkUnit right)) { position = current.Position; return true; }
            position = before.ById.TryGetValue(id, out NetworkUnit left) && left.OwnerId == right.OwnerId && left.SquadId == right.SquadId && left.IsReinforcing == right.IsReinforcing && left.MovementRevision == right.MovementRevision
                ? Vector2.Lerp(left.Position, right.Position, fraction) : right.Position;
            return true;
        }

        public void ObservePose(LegionPose pose)
        {
            int squad = pose.SquadId - 1;
            if (squad < 0 || squad >= 4) return;
            int head = poseHeads[squad];
            if (head >= 0 && poses[squad, head].Tick >= pose.Tick) return;
            head = (head + 1) % frames.Length;
            poses[squad, head] = pose;
            poseHeads[squad] = head;
            poseCounts[squad] = Math.Min(poseCounts[squad] + 1, frames.Length);
        }

        public Vector2 SampleSquadAnchor(byte squadId, double tick)
        {
            int squad = squadId - 1;
            if (squad < 0 || squad >= 4) return Vector2.Zero;
            int head = poseHeads[squad];
            if (head < 0) return squadId == 1 ? SampleAnchor(tick) : Vector2.Zero;
            LegionPose latest = poses[squad, head], after = latest, before = after;
            for (int offset = 1; offset < poseCounts[squad]; offset++)
            {
                LegionPose older = poses[squad, (head - offset + frames.Length) % frames.Length];
                if (older.MovementRevision != latest.MovementRevision) return latest.Position;
                if (older.Tick <= tick) { before = older; break; }
                after = older; before = older;
            }
            float fraction = after.Tick == before.Tick ? 0 : (float)Math.Max(0, Math.Min(1, (tick - before.Tick) / (after.Tick - before.Tick)));
            return Vector2.Lerp(before.Position, after.Position, fraction);
        }

        public static bool UsesPrediction(NetworkUnit unit, int owner, byte selectedSquad) =>
            unit.OwnerId == owner && unit.SquadId == selectedSquad && !unit.IsReinforcing;

        public bool TrySampleDisplay(NetworkUnit unit, double tick, int owner, byte selectedSquad, Vector2 offset, out Vector2 position)
        {
            if (!TrySample(unit.Id, tick, out position)) return false;
            if (UsesPrediction(unit, owner, selectedSquad)) position += offset;
            return true;
        }

        public Vector2 SampleAnchor(double tick)
        {
            if (head < 0) return Vector2.Zero;
            Bounds(tick, out Frame before, out Frame after, out float fraction);
            return Vector2.Lerp(before.Anchor, after.Anchor, fraction);
        }

        private void Bounds(double tick, out Frame before, out Frame after, out float fraction)
        {
            after = frames[head]; before = after;
            for (int offset = 1; offset < count; offset++)
            {
                Frame older = frames[(head - offset + frames.Length) % frames.Length];
                if (older.Tick <= tick) { before = older; break; }
                after = older; before = older;
            }
            fraction = after.Tick == before.Tick ? 0 : (float)Math.Max(0, Math.Min(1, (tick - before.Tick) / (after.Tick - before.Tick)));
        }

        private sealed class Frame
        {
            public readonly NetworkUnit[] Units = new NetworkUnit[Protocol.MaxUnits];
            public readonly Dictionary<int, NetworkUnit> ById = new Dictionary<int, NetworkUnit>();
            public long Tick;
            public int Count;
            public Vector2 Anchor;
        }
    }
}
