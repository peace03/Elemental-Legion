using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>군단 중심·역할별 진형·이탈 공격 이동과 원형 건물의 선분 충돌을 처리한다.</summary>
    internal static class MovementSystem
    {
        internal static void MoveLegions(WorldState world)
        {
            float distance = world.Rules.LegionSpeed * (float)world.Rules.DeltaTime;
            foreach (PlayerState player in world.Players)
            {
                if (player.Direction != Vector2.Zero) player.Heading = player.Direction;
                player.LegionPosition = MovePoint(world, player.LegionPosition, player.LegionPosition + player.Direction * distance);
            }
        }

        internal static void MoveUnits(WorldState world)
        {
            foreach (PlayerState player in world.Players) player.Legion.Clear();
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId >= 0 && !unit.Garrison && unit.Health > 0) world.PlayerById[unit.OwnerId].Legion.Add(unit);
            foreach (PlayerState player in world.Players)
            {
                if (player.FormationDirty)
                {
                    player.Legion.Sort(CompareSlots);
                    int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(player.Legion.Count)));
                    int rows = (player.Legion.Count + columns - 1) / columns;
                    for (int i = 0; i < player.Legion.Count; i++)
                        player.Legion[i].Slot = new Vector2(i % columns - (columns - 1) * 0.5f, (rows - 1) * 0.5f - i / columns) * world.Rules.SlotSpacing;
                    player.FormationDirty = false;
                }
                Vector2 right = new Vector2(player.Heading.Y, -player.Heading.X);
                foreach (UnitState unit in player.Legion)
                {
                    unit.Home = player.LegionPosition + right * unit.Slot.X + player.Heading * unit.Slot.Y;
                    Vector2 destination = unit.Home;
                    UnitState target = unit.Target;
                    if (target != null && target.Health > 0 && target.OwnerId != unit.OwnerId && Vector2.DistanceSquared(unit.Home, target.Position) <= Square(world.Rules.LeashDistance + world.Rules.Elements[unit.Element].Range))
                    {
                        Vector2 offset = target.Position - unit.Position;
                        float length = offset.Length(), range = world.Rules.Elements[unit.Element].Range * unit.RangeMultiplier;
                        if (length > range) destination = unit.Position + offset / length * (length - range);
                        else destination = unit.Position;
                        Vector2 leash = destination - unit.Home;
                        if (leash.LengthSquared() > Square(world.Rules.LeashDistance)) destination = unit.Home + Vector2.Normalize(leash) * world.Rules.LeashDistance;
                    }
                    MoveTowards(world, unit, destination, world.Rules.LegionSpeed);
                }
            }
            foreach (UnitState unit in world.Units)
            {
                if (unit.OwnerId < 0 || !unit.Garrison || unit.Health <= 0) continue;
                PlayerState player = world.PlayerById[unit.OwnerId];
                unit.Home = unit.Position;
                if (player.Rallying) MoveTowards(world, unit, player.LegionPosition, world.Rules.LegionSpeed * world.Rules.RallySpeedMultiplier);
            }
        }

        private static int CompareSlots(UnitState left, UnitState right)
        {
            int role = ((int)left.Role).CompareTo((int)right.Role);
            return role != 0 ? role : left.Id.CompareTo(right.Id);
        }
        private static void MoveTowards(WorldState world, UnitState unit, Vector2 destination, float speed)
        {
            Vector2 offset = destination - unit.Position;
            float distance = offset.Length(), step = speed * (float)world.Rules.DeltaTime;
            if (distance > step) destination = unit.Position + offset / distance * step;
            unit.Position = MovePoint(world, unit.Position, destination);
        }

        internal static Vector2 MovePoint(WorldState world, Vector2 start, Vector2 destination)
        {
            destination = Vector2.Clamp(destination, Vector2.Zero, new Vector2(world.Rules.MapSize));
            Vector2 delta = destination - start;
            float fraction = 1;
            foreach (BuildingState building in world.Buildings)
                fraction = CircleCollision.Limit(start, delta, building.Position, building.Radius, fraction);
            return start + delta * fraction;
        }
        private static float Square(float value) => value * value;
    }
}
