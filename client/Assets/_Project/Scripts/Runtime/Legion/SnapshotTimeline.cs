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
        private int head = -1, count;
        private readonly Dictionary<int, long> forgotten = new Dictionary<int, long>();
        private readonly List<int> expired = new List<int>();
        private readonly HashSet<int> dead = new HashSet<int>();
        private readonly Dictionary<int,(long Tick,int Owner,float Health)> conversions = new Dictionary<int,(long,int,float)>();
        public int Count => head < 0 ? 0 : frames[head].Count;
        public long LatestTick => head < 0 ? -1 : frames[head].Tick;

        public SnapshotTimeline(int capacity)
        {
            if (capacity < 2 || capacity > 128) throw new ArgumentOutOfRangeException(nameof(capacity));
            frames = new Frame[capacity];
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
                if (!dead.Contains(unit.Id)&&(!forgotten.TryGetValue(unit.Id, out long hiddenAt) || snapshot.Tick > hiddenAt)) frame.ById[unit.Id] = unit;
            }
            expired.Clear();
            foreach (var item in forgotten) if (item.Value < snapshot.Tick) expired.Add(item.Key);
            foreach (int id in expired) forgotten.Remove(id);
            expired.Clear();foreach(var item in conversions)if(item.Value.Tick<snapshot.Tick)expired.Add(item.Key);
            foreach(int id in expired)conversions.Remove(id);
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

        public void ResolveDeath(int id,long tick)
        {
            if(id<=0||tick<0)throw new ArgumentOutOfRangeException(nameof(id));
            dead.Add(id);conversions.Remove(id);
            for(int index=0;index<frames.Length;index++)if(frames[index]!=null)frames[index].ById.Remove(id);
        }

        public void ResolveConversion(int id,long tick,int owner,float health)
        {
            if(id<=0||tick<0||owner<=0||health<=0)throw new ArgumentOutOfRangeException(nameof(id));
            if(dead.Contains(id)||conversions.TryGetValue(id,out var previous)&&previous.Tick>=tick)return;
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

        private static NetworkUnit Converted(NetworkUnit unit,int owner,float health)=>new NetworkUnit(unit.Id,owner,unit.Element,unit.Position,false,health,0,true,0);

        public bool TrySample(int id, double tick, out Vector2 position)
        {
            position = default;
            if (head < 0 || !frames[head].ById.TryGetValue(id, out NetworkUnit current)) return false;
            Bounds(tick, out Frame before, out Frame after, out float fraction);
            if (!after.ById.TryGetValue(id, out NetworkUnit right)) { position = current.Position; return true; }
            position = before.ById.TryGetValue(id, out NetworkUnit left) && left.OwnerId == right.OwnerId
                ? Vector2.Lerp(left.Position, right.Position, fraction) : right.Position;
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
