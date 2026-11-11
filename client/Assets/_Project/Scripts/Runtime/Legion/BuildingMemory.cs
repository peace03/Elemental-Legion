using System;
using System.Collections.Generic;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    /// <summary>정찰 당시의 모습과 색을 전장·미니맵이 함께 사용한다.</summary>
    internal readonly struct RememberedBuilding
    {
        internal readonly BuildingView Building;
        internal readonly long LastSeenTick;
        internal readonly Color32 Color;

        internal RememberedBuilding(BuildingView building, long lastSeenTick, Color32 color)
        {
            Building = building;
            LastSeenTick = lastSeenTick;
            Color = color;
        }
    }

    /// <summary>받은 적 시설 관측을 표시용으로 복사하고, 같은 접속의 서버 확인으로 갱신한다.</summary>
    internal sealed class BuildingMemory
    {
        private readonly Dictionary<int, RememberedBuilding> records = new Dictionary<int, RememberedBuilding>();
        private readonly HashSet<int> visible = new HashSet<int>();
        private readonly PlayerColorCache colors = new PlayerColorCache();
        private long observationTick = -1, fogTick = -1;
        private uint revision;
        private int observer;

        internal Dictionary<int, RememberedBuilding>.ValueCollection Records => records.Values;
        internal bool IsVisible(int id) => visible.Contains(id);
        internal bool TryGet(int id, out RememberedBuilding record) => records.TryGetValue(id, out record);

        internal bool Observe(GameplayFrame frame)
        {
            if (!frame.SpatialReady || frame.PlayerId <= 0 || frame.Revision == 0 ||
                frame.Revision < revision || frame.Tick < fogTick || frame.Tick <= observationTick ||
                observer != 0 && frame.PlayerId != observer) return false;

            observer = frame.PlayerId;
            revision = frame.Revision;
            observationTick = frame.Tick;
            colors.Clear();
            colors.Observe(frame);
            visible.Clear();
            foreach (BuildingView building in frame.Buildings)
            {
                if (building.OwnerId == observer) records.Remove(building.Id);
                else if (building.OwnerId > 0) visible.Add(building.Id);
            }
            foreach (BuildingView building in frame.Buildings)
            {
                if (!visible.Contains(building.Id)) continue;
                if (!records.ContainsKey(building.Id) && records.Count >= FogProtocol.MaxRememberedBuildings &&
                    !RemoveOldestHidden()) continue;
                records[building.Id] = new RememberedBuilding(building, frame.Tick, colors.Get(building.OwnerId));
            }
            return true;
        }

        internal bool Forget(int id, long tick) =>
            records.TryGetValue(id, out var record) && record.LastSeenTick <= tick && visible.Remove(id);

        internal bool ConfirmAbsent(ReadOnlySpan<int> ids, long tick, uint fogRevision)
        {
            if (fogRevision == 0 || fogRevision < revision || tick < fogTick) return false;
            revision = fogRevision;
            fogTick = tick;
            bool changed = false;
            foreach (int id in ids)
            {
                if (!records.TryGetValue(id, out var record) || record.LastSeenTick > tick) continue;
                records.Remove(id);
                visible.Remove(id);
                changed = true;
            }
            return changed;
        }

        internal void Clear()
        {
            records.Clear();
            visible.Clear();
            colors.Clear();
            observationTick = fogTick = -1;
            revision = 0;
            observer = 0;
        }

        private bool RemoveOldestHidden()
        {
            int oldestId = 0;
            long oldestTick = long.MaxValue;
            foreach (var entry in records)
            {
                if (visible.Contains(entry.Key)) continue;
                long seen = entry.Value.LastSeenTick;
                if (oldestId != 0 && (seen > oldestTick || seen == oldestTick && entry.Key > oldestId)) continue;
                oldestId = entry.Key;
                oldestTick = seen;
            }
            return oldestId != 0 && records.Remove(oldestId);
        }
    }
}
