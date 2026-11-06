using System.Collections.Generic;
using ElementalLegion.Shared.Net;
using UnityEngine;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Legion
{
    public sealed partial class ElementalVisuals
    {
        [SerializeField, Range(.2f, 1f), Tooltip("밀집 중에도 본체를 읽을 수 있도록 유지하는 최소 표시 반경의 비율입니다.")]
        private float crowdedFootprintMinimum = .35f;
        private readonly DisplayUnit[] displayUnits = new DisplayUnit[Protocol.MaxUnits];
        private readonly Dictionary<Vector2Int, int> displayCells = new Dictionary<Vector2Int, int>(Protocol.MaxUnits);

        private struct DisplayUnit
        {
            internal NetworkUnit Unit;
            internal WorldPoint Position;
            internal Vector2Int Cell;
            internal int NextInCell;
        }

        private int CollectDisplayUnits(SnapshotTimeline timeline, int owner, WorldPoint offset, double tick)
        {
            displayCells.Clear();
            int count = 0;
            float diameter = unitFootprint * 2;
            for (int index = 0; index < timeline.Count; index++)
            {
                NetworkUnit unit = timeline.GetLatest(index);
                if (unit.Element >= profiles.Length || !timeline.TrySample(unit.Id, tick, out WorldPoint position)) continue;
                if (unit.OwnerId == owner && !unit.Garrison) position += offset;
                ref DisplayUnit item = ref displayUnits[count];
                item = new DisplayUnit { Unit = unit, Position = position };
                if (!unit.Ghost)
                {
                    item.Cell = new Vector2Int(Mathf.FloorToInt(position.X / diameter), Mathf.FloorToInt(position.Y / diameter));
                    displayCells.TryGetValue(item.Cell, out item.NextInCell);
                    displayCells[item.Cell] = count + 1;
                }
                count++;
            }
            return count;
        }

        private float FitDisplayFootprint(int index, float minimumFootprint)
        {
            ref DisplayUnit item = ref displayUnits[index];
            if (item.Unit.Ghost) return unitFootprint;
            float nearestSquared = 4 * unitFootprint * unitFootprint;
            float minimumSquared = 4 * minimumFootprint * minimumFootprint;
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    displayCells.TryGetValue(item.Cell + new Vector2Int(x, y), out int head);
                    for (int link = head; link != 0; link = displayUnits[link - 1].NextInCell)
                    {
                        if (link == index + 1) continue;
                        float squared = WorldPoint.DistanceSquared(item.Position, displayUnits[link - 1].Position);
                        // 최소 표시 폭까지 좁아졌다면 더 가까운 유닛을 찾아도 결과가 같다.
                        if (squared <= minimumSquared) return minimumFootprint;
                        nearestSquared = Mathf.Min(nearestSquared, squared);
                    }
                }
            }
            return .5f * Mathf.Sqrt(nearestSquared);
        }
    }
}
