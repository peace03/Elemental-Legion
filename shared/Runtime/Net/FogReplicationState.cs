using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>한 peer에게 마지막으로 보낸 시야와 관측 ID를 보관한다. Prepare→전송→Commit은 한 틱에서 수행한다.</summary>
    public sealed class FogReplicationState
    {
        private readonly byte[] previousCurrent, previousExplored, preparedCurrent, preparedExplored;
        private HashSet<ObservedEntityId> known = new HashSet<ObservedEntityId>(), observed = new HashSet<ObservedEntityId>();
        private readonly List<ObservedEntityId> forgotten = new List<ObservedEntityId>(Protocol.MaxUnits);
        private readonly Dictionary<int, (Vector2 Position, long Tick)> rememberedBuildings = new Dictionary<int, (Vector2, long)>();
        private readonly List<int> clearedBuildingMemories = new List<int>();
        private readonly List<(long Tick, int Id)> evictionCandidates = new List<(long, int)>();
        private readonly NetDataWriter body = new NetDataWriter();
        private readonly int cellCount;
        private bool initialized, prepared, changed;
        private long tick;
        private int owner;
        private uint committedRevision;
        private PacketKind kind;
        public uint Revision { get; private set; }
        public int ChunkCount => changed ? (body.Length + FogProtocol.PayloadBytes - 1) / FogProtocol.PayloadBytes : 0;
        public int ForgetChunkCount => (forgotten.Count + FogProtocol.ForgetPerChunk - 1) / FogProtocol.ForgetPerChunk;
        public bool IsBaseline => kind == PacketKind.FogBaseline;

        public FogReplicationState(GameRules rules)
        {
            int width = checked((int)Math.Ceiling(rules.MapSize / rules.CellSize)); cellCount = checked(width * width);
            if (cellCount > FogProtocol.MaxCells) throw new ArgumentException("시야 전송 격자가 기술 상한을 넘었습니다.", nameof(rules));
            int bytes = (cellCount + 7) / 8;
            previousCurrent = new byte[bytes]; previousExplored = new byte[bytes];
            preparedCurrent = new byte[bytes]; preparedExplored = new byte[bytes];
        }

        public bool Prepare(WorldView view)
        {
            if (prepared) throw new InvalidOperationException("이전 시야 전송을 Commit해야 합니다.");
            if (view.Vision.Current.Length != previousCurrent.Length || owner != 0 && owner != view.PlayerId) throw new ArgumentException("시야 수신자나 격자가 다릅니다.", nameof(view));
            if (view.Buildings.Count > FogProtocol.MaxRememberedBuildings) throw new ReplicationOverflowException();
            owner = view.PlayerId; tick = view.Tick;
            view.Vision.Current.CopyTo(preparedCurrent); view.Vision.Explored.CopyTo(preparedExplored);
            observed.Clear();foreach(UnitView unit in view.Units)observed.Add(new ObservedEntityId(ObservedEntityKind.Unit,unit.Id));
            foreach(CoreView core in view.Cores)observed.Add(new ObservedEntityId(ObservedEntityKind.Core,core.Id));
            foreach(BuildingView building in view.Buildings)observed.Add(new ObservedEntityId(ObservedEntityKind.Building,building.Id));
            foreach(AreaView area in view.Areas)observed.Add(new ObservedEntityId(ObservedEntityKind.Area,area.Id));
            foreach(ConversionCandidateView candidate in view.Candidates)observed.Add(new ObservedEntityId(ObservedEntityKind.Candidate,candidate.Id));
            forgotten.Clear(); foreach (ObservedEntityId id in known) if (!observed.Contains(id)) forgotten.Add(id);
            UpdateBuildingMemories(view);
            int changes = 0;
            for (int index = 0; index < previousCurrent.Length; index++)
            {
                int difference = (previousCurrent[index] ^ preparedCurrent[index]) | (previousExplored[index] ^ preparedExplored[index]);
                for (; difference != 0; difference >>= 1) changes += difference & 1;
            }
            kind = initialized ? PacketKind.FogDelta : PacketKind.FogBaseline;
            changed = !initialized || changes > 0 || forgotten.Count > 0 || clearedBuildingMemories.Count > 0;
            Revision = changed ? checked(committedRevision + 1) : committedRevision;
            body.Reset();
            if (!initialized) { body.Put(preparedCurrent); body.Put(preparedExplored); }
            else if (changed)
            {
                body.Put(changes);
                for (int index = 0; index < previousCurrent.Length; index++)
                {
                    int difference = (previousCurrent[index] ^ preparedCurrent[index]) | (previousExplored[index] ^ preparedExplored[index]);
                    for (int bit = 0; difference != 0; bit++, difference >>= 1)
                    {
                        if ((difference & 1) == 0) continue;
                        int mask = 1 << bit;
                        body.Put(index * 8 + bit);
                        body.Put((byte)(((preparedCurrent[index] & mask) != 0 ? 1 : 0) | ((preparedExplored[index] & mask) != 0 ? 2 : 0)));
                    }
                }
                body.Put((ushort)clearedBuildingMemories.Count);
                foreach (int id in clearedBuildingMemories) body.Put(id);
            }
            prepared = true; return changed;
        }

        private void UpdateBuildingMemories(WorldView view)
        {
            clearedBuildingMemories.Clear();
            // 이전에 관측한 건물의 위치가 정확한 시야 안에 들어왔을 때만 빈자리를 확인한다.
            foreach (var entry in rememberedBuildings)
                if (!observed.Contains(new ObservedEntityId(ObservedEntityKind.Building, entry.Key)) && view.CanSeePoint(entry.Value.Position))
                    clearedBuildingMemories.Add(entry.Key);
            foreach (int id in clearedBuildingMemories) rememberedBuildings.Remove(id);

            int incoming = 0;
            foreach (BuildingView building in view.Buildings)
                if (building.OwnerId != owner && !rememberedBuildings.ContainsKey(building.Id)) incoming++;
            int overflow = rememberedBuildings.Count + incoming - FogProtocol.MaxRememberedBuildings;
            if (overflow > 0)
            {
                evictionCandidates.Clear();
                foreach (var entry in rememberedBuildings)
                    if (!observed.Contains(new ObservedEntityId(ObservedEntityKind.Building, entry.Key)))
                        evictionCandidates.Add((entry.Value.Tick, entry.Key));
                evictionCandidates.Sort();
                for (int index = 0; index < overflow; index++)
                {
                    int id = evictionCandidates[index].Id;
                    rememberedBuildings.Remove(id);
                    clearedBuildingMemories.Add(id);
                }
            }

            foreach (BuildingView building in view.Buildings)
                if (building.OwnerId != owner) rememberedBuildings[building.Id] = (building.Position, view.Tick);
            clearedBuildingMemories.Sort();
        }

        public void WriteChunk(NetDataWriter writer, int part)
        {
            if (!prepared || !changed || part < 0 || part >= ChunkCount) throw new ArgumentOutOfRangeException(nameof(part));
            FogProtocol.WriteChunk(writer, kind, tick, owner, Revision, kind == PacketKind.FogBaseline ? 0 : committedRevision, body, part);
        }

        public void WriteForget(NetDataWriter writer, int part)
        {
            if (!prepared || part < 0 || part >= ForgetChunkCount) throw new ArgumentOutOfRangeException(nameof(part));
            int start = part * FogProtocol.ForgetPerChunk, count = Math.Min(FogProtocol.ForgetPerChunk, forgotten.Count - start);
            writer.Reset(); writer.Put((byte)PacketKind.Forget); writer.Put(tick); writer.Put(owner); writer.Put(Revision); writer.Put((ushort)count);
            for (int index = start; index < start + count; index++) {writer.Put((byte)forgotten[index].Kind);writer.Put(forgotten[index].Id);}
        }

        public void Commit()
        {
            if (!prepared) throw new InvalidOperationException("준비한 시야 전송이 없습니다.");
            Array.Copy(preparedCurrent, previousCurrent, previousCurrent.Length); Array.Copy(preparedExplored, previousExplored, previousExplored.Length);
            var swap = known; known = observed; observed = swap;
            committedRevision = Revision; initialized = true; prepared = false;
        }
    }
}
