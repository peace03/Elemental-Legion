using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>고정 크기 월드의 공간 해시 격자다. 머리·연결 배열을 재사용하고 실제 원 거리로 후보를 거른다.</summary>
    internal sealed class SpatialGrid
    {
        private readonly int width;
        private readonly float cellSize;
        private readonly int[] heads;
        private int[] next = System.Array.Empty<int>();
        private List<UnitState> units;
        internal SpatialGrid(float mapSize, float cellSize)
        {
            this.cellSize = cellSize;
            width = (int)Math.Ceiling(mapSize / cellSize) + 1;
            heads = new int[checked(width * width)];
        }
        internal void Rebuild(List<UnitState> source)
        {
            units = source;
            if (next.Length < source.Count) next = new int[Math.Max(source.Count, Math.Max(16, next.Length * 2))];
            for (int i = 0; i < heads.Length; i++) heads[i] = -1;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i].Health <= 0) continue;
                int cell = Cell(source[i].Position.X) + width * Cell(source[i].Position.Y);
                next[i] = heads[cell]; heads[cell] = i;
            }
        }
        internal void Query(Vector2 center, float radius, List<UnitState> result)
        {
            result.Clear();
            float squared = radius * radius;
            int minX = Cell(center.X - radius), maxX = Cell(center.X + radius), minY = Cell(center.Y - radius), maxY = Cell(center.Y + radius);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    for (int index = heads[x + width * y]; index >= 0; index = next[index])
                    {
                        UnitState unit = units[index];
                        if (unit.Health > 0 && Vector2.DistanceSquared(center, unit.Position) <= squared) result.Add(unit);
                    }
        }
        internal void MarkVisible(Vector2 center, float radius, ulong mask)
        {
            float squared = radius * radius;
            int minX = Cell(center.X - radius), maxX = Cell(center.X + radius), minY = Cell(center.Y - radius), maxY = Cell(center.Y + radius);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    for (int index = heads[x + width * y]; index >= 0; index = next[index])
                    {
                        UnitState unit = units[index];
                        if ((unit.VisibleTo & mask) == 0 && unit.Health > 0 && Vector2.DistanceSquared(center, unit.Position) <= squared) unit.VisibleTo |= mask;
                    }
        }
        internal UnitState FindNearest(UnitState source, float range, float leash, bool heal, GameRules rules)
        {
            float radius = range + leash;
            float bestDistance = radius * radius;
            UnitState best = null;
            int ownX = Cell(source.Position.X), ownY = Cell(source.Position.Y);
            FindInCell(ownX, ownY, source, radius, leash, heal, rules, ref best, ref bestDistance);
            int minX = Cell(source.Position.X - radius), maxX = Cell(source.Position.X + radius);
            int minY = Cell(source.Position.Y - radius), maxY = Cell(source.Position.Y + radius);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    if (x == ownX && y == ownY) continue;
                    float dx = Math.Max(0, Math.Max(x * cellSize - source.Position.X, source.Position.X - (x + 1) * cellSize));
                    float dy = Math.Max(0, Math.Max(y * cellSize - source.Position.Y, source.Position.Y - (y + 1) * cellSize));
                    // 칸까지의 최소 거리도 현재 표적보다 멀면 더 가까운 유닛이 있을 수 없다.
                    if (dx * dx + dy * dy > bestDistance) continue;
                    FindInCell(x, y, source, radius, leash, heal, rules, ref best, ref bestDistance);
                }
            return best;
        }

        private void FindInCell(int x, int y, UnitState source, float radius, float leash, bool heal, GameRules rules, ref UnitState best, ref float bestDistance)
        {
            for (int index = heads[x + width * y]; index >= 0; index = next[index])
            {
                UnitState target = units[index];
                if (target.Health <= 0) continue;
                if (heal)
                {
                    if (target.OwnerId != source.OwnerId || target.Health >= rules.Elements[target.Element].Hp) continue;
                }
                else if (target.OwnerId == source.OwnerId) continue;
                float distance = Vector2.DistanceSquared(source.Position, target.Position);
                if (distance > bestDistance || distance == bestDistance && best != null && target.Id >= best.Id) continue;
                if (leash > 0 && Vector2.DistanceSquared(target.Position, source.Home) > radius * radius) continue;
                best = target; bestDistance = distance;
            }
        }
        private int Cell(float value) => Math.Max(0, Math.Min(width - 1, (int)Math.Floor(value / cellSize)));
    }
}
