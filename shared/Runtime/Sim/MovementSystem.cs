using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>군단 중심 충돌과 개별 유닛의 진형 이동·건물 우회를 처리한다.</summary>
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
            float deltaTime = (float)world.Rules.DeltaTime;
            float step = world.Rules.LegionSpeed * deltaTime;
            float catchUpStep = world.Rules.LegionSpeed * world.Rules.CatchUpSpeedMultiplier * deltaTime;
            float rallyStep = world.Rules.LegionSpeed * world.Rules.RallySpeedMultiplier * deltaTime;
            float neutralStep = step * world.Rules.NeutralMoveSpeedMultiplier;
            float neutralLeashSquared = Square(world.Rules.NeutralLeashRadius);
            float catchUpDistanceSquared = Square(world.Rules.CatchUpDistance);
            float slotSpacingSquared = Square(world.Rules.SlotSpacing);
            float stepSquared = Square(step);
            foreach (PlayerState player in world.Players)
            {
                player.Legion.Clear();
                player.OwnedUnits.Clear();
            }
            foreach (UnitState unit in world.Units)
            {
                if (unit.OwnerId < 0 || unit.Health <= 0) continue;
                unit.MoveStart = unit.Position;
                unit.MoveStep = step;
                PlayerState player = world.PlayerById[unit.OwnerId];
                player.OwnedUnits.Add(unit);
                if (!unit.Garrison) player.Legion.Add(unit);
            }
            foreach (PlayerState player in world.Players)
            {
                if (player.GhostEndTick != 0) continue;
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
                    Vector2 slot = player.LegionPosition + right * unit.Slot.X + player.Heading * unit.Slot.Y;
                    unit.Home = ResolveSlot(world, player.LegionPosition, slot);
                    Vector2 destination = unit.Home;
                    UnitState target = unit.Target;
                    // 이동 입력 중에는 대형을 따르고, 사거리 안 공격은 전투 단계에서 계속 처리한다.
                    bool pursuing = player.Direction == Vector2.Zero;
                    if (pursuing && target != null && target.Health > 0 && target.OwnerId != unit.OwnerId && Vector2.DistanceSquared(unit.Home, target.Position) <= Square(world.Rules.LeashDistance + world.Rules.Elements[unit.Element].Range))
                    {
                        Vector2 offset = target.Position - unit.Position;
                        float length = offset.Length(), range = world.Rules.Elements[unit.Element].Range * unit.RangeMultiplier;
                        if (length > range) destination = unit.Position + offset / length * (length - range);
                        else destination = unit.Position;
                    }
                    else if (pursuing && unit.BuildingTarget is BuildingState building && building.Health > 0 &&
                        SiegeSystem.InRange(unit.Home, building, world.Rules.LeashDistance + world.Rules.Elements[unit.Element].Range))
                    {
                        Vector2 offset = building.Position - unit.Position;
                        float length = offset.Length();
                        float range = world.Rules.Elements[unit.Element].Range * unit.RangeMultiplier;
                        float reach = building.Radius + range;
                        // 경계의 좌표 반올림에 걸리지 않도록 접근할 때 한 걸음 안쪽을 겨냥한다.
                        float approach = reach - Math.Min(step, range * 0.5f);
                        destination = length > reach ? unit.Position + offset * ((length - approach) / length) : unit.Position;
                    }
                    else pursuing = false;

                    if (pursuing)
                    {
                        unit.CatchingUp = false;
                        Vector2 leash = destination - unit.Home;
                        if (leash.LengthSquared() > Square(world.Rules.LeashDistance)) destination = unit.Home + Vector2.Normalize(leash) * world.Rules.LeashDistance;
                    }
                    else if (unit.CatchingUp || Vector2.DistanceSquared(unit.Position, player.LegionPosition) >= catchUpDistanceSquared)
                    {
                        // 진형 안의 원소는 그대로 두고, 복귀를 시작하면 기본 이동 한 번으로 슬롯에 닿을 때까지 속도를 유지한다.
                        float remaining = unit.CatchingUp ? stepSquared : slotSpacingSquared;
                        unit.CatchingUp = Vector2.DistanceSquared(unit.Position, destination) > remaining;
                        if (unit.CatchingUp) unit.MoveStep = catchUpStep;
                    }
                    MoveTowards(world, unit, destination);
                }
            }
            foreach (UnitState unit in world.Units)
            {
                if (unit.Health <= 0) continue;
                if (unit.OwnerId < 0)
                {
                    if (unit.EcologyNeutral) MoveNeutral(world, unit, neutralStep, neutralLeashSquared);
                    continue;
                }
                if (!unit.Garrison) continue;
                PlayerState player = world.PlayerById[unit.OwnerId];
                unit.Home = unit.Position;
                if (!player.Rallies(unit)) continue;
                unit.MoveStep = rallyStep;
                MoveTowards(world, unit, player.LegionPosition);
            }
            foreach (PlayerState player in world.Players)
                if (player.GhostEndTick == 0) SeparateAllies(world, player);
        }

        private static void MoveNeutral(WorldState world, UnitState unit, float step, float leashSquared)
        {
            UnitState target = unit.Target;
            if (!unit.NeutralReturning && (target == null || target.OwnerId < 0)) return;
            unit.MoveStep = step;
            if (!unit.NeutralReturning && RetainNeutralTarget(unit, leashSquared))
            {
                Vector2 offset = target.Position - unit.Position;
                float range = world.Rules.Elements[unit.Element].Range * unit.RangeMultiplier;
                if (offset.LengthSquared() <= Square(range)) return;

                float distance = offset.Length();
                float approach = range - Math.Min(step, range * 0.5f);
                Vector2 start = unit.Position;
                MoveTowards(world, unit, start + offset * ((distance - approach) / distance));
                if (Vector2.DistanceSquared(unit.Position, unit.Home) <= leashSquared) return;
                unit.Position = start;
            }

            unit.Target = null;
            Vector2 home = ResolveSlot(world, unit.Position, unit.Home);
            MoveTowards(world, unit, home);
            unit.NeutralReturning = unit.Position != home;
        }

        internal static bool RetainNeutralTarget(UnitState unit, float leashSquared)
            => unit.Target.Health > 0 && unit.Target.OwnerId >= 0 &&
                Vector2.DistanceSquared(unit.Home, unit.Target.Position) <= leashSquared;

        private static void SeparateAllies(WorldState world, PlayerState player)
        {
            float distance = world.Rules.MinimumSeparation;
            float squared = Square(distance);
            var units = player.OwnedUnits;
            // 현재 플레이어당 50기까지라 같은 소유자의 목록에서만 쌍을 검사한다.
            for (int left = 0; left < units.Count; left++)
            {
                UnitState unit = units[left];
                for (int right = left + 1; right < units.Count; right++)
                {
                    UnitState other = units[right];
                    Vector2 offset = other.Position - unit.Position;
                    float lengthSquared = offset.LengthSquared();
                    if (lengthSquared >= squared) continue;
                    float length = MathF.Sqrt(lengthSquared);
                    Vector2 previous = other.MoveStart - unit.MoveStart;
                    // 좌표가 같으면 직전 틱의 배치를 유지하고 완전히 겹친 경우 ID 순서로 벌린다.
                    Vector2 direction = length > 0 ? offset / length :
                        previous == Vector2.Zero ? Vector2.UnitX : Vector2.Normalize(previous);
                    Vector2 correction = direction * ((distance - length) * 0.5f);
                    // 마주 오는 유닛은 양쪽으로 비켜야 같은 선에서 계속 서로를 밀지 않는다.
                    if (Vector2.Dot(unit.Position - unit.MoveStart, direction) > 0 &&
                        Vector2.Dot(other.Position - other.MoveStart, direction) < 0)
                        correction += Perpendicular(correction);
                    unit.Separation -= correction;
                    other.Separation += correction;
                }
            }
            foreach (UnitState unit in units)
            {
                if (unit.Separation == Vector2.Zero) continue;
                Vector2 destination = StepTowards(unit.MoveStart, unit.Position + unit.Separation, unit.MoveStep);
                unit.Position = MovePoint(world, unit.MoveStart, destination);
                unit.Separation = Vector2.Zero;
            }
        }

        private static int CompareSlots(UnitState left, UnitState right)
        {
            int role = ((int)left.Role).CompareTo((int)right.Role);
            return role != 0 ? role : left.Id.CompareTo(right.Id);
        }
        private static void MoveTowards(WorldState world, UnitState unit, Vector2 destination)
        {
            Vector2 start = unit.Position;
            destination = ClampToMap(world, destination);
            float step = unit.MoveStep;
            BuildingState obstacle = unit.Detour;
            Vector2 offset = destination - start;
            if (obstacle == null || obstacle.Health <= 0 || CircleCollision.Limit(start, offset, obstacle.Position, obstacle.Radius) == 1)
            {
                unit.Position = MovePoint(world, start, StepTowards(start, destination, step), out obstacle);
                unit.Detour = obstacle;
                if (obstacle == null) return;
                Vector2 radial = start - obstacle.Position;
                unit.DetourSide = Vector2.Dot(Perpendicular(radial), destination - obstacle.Position) >= 0 ? 1 : -1;
                return;
            }

            Vector2 normal = Vector2.Normalize(start - obstacle.Position);
            Vector2 tangent = Perpendicular(normal) * unit.DetourSide;
            // 한 틱 이동분만큼 벽에서 여유를 두면 접선 이동이 연속해서 원에 걸리지 않는다.
            Vector2 edge = obstacle.Position + normal * (obstacle.Radius + step);
            Vector2 next = MovePoint(world, start, StepTowards(start, edge + tangent * step, step));
            // 맵 경계나 이웃 건물이 선택한 쪽을 막으면 반대쪽을 검사한다.
            if (Vector2.Dot(next - start, tangent) <= 0)
            {
                Vector2 reverse = MovePoint(world, start, StepTowards(start, edge - tangent * step, step));
                if (Vector2.Dot(reverse - start, tangent) < 0)
                {
                    next = reverse;
                    unit.DetourSide = -unit.DetourSide;
                }
            }
            unit.Position = next;
        }

        private static Vector2 ResolveSlot(WorldState world, Vector2 center, Vector2 slot)
        {
            if (slot == center) return slot;
            foreach (BuildingState building in world.Buildings)
                if (building.BlocksMovement && Vector2.DistanceSquared(slot, building.Position) < Square(building.Radius))
                    return MovePoint(world, center, slot);
            return slot;
        }

        private static Vector2 StepTowards(Vector2 start, Vector2 destination, float step)
        {
            Vector2 offset = destination - start;
            float distanceSquared = offset.LengthSquared();
            return distanceSquared > Square(step) ? start + offset * (step / MathF.Sqrt(distanceSquared)) : destination;
        }

        private static Vector2 MovePoint(WorldState world, Vector2 start, Vector2 destination)
            => MovePoint(world, start, destination, out _);

        private static Vector2 MovePoint(WorldState world, Vector2 start, Vector2 destination, out BuildingState obstacle)
        {
            Vector2 delta = ClampToMap(world, destination) - start;
            obstacle = default;
            if (delta == Vector2.Zero) return start;
            float fraction = 1;
            foreach (BuildingState building in world.Buildings)
            {
                if (!building.BlocksMovement) continue;
                float limit = CircleCollision.Limit(start, delta, building.Position, building.Radius, fraction);
                if (limit >= fraction) continue;
                fraction = limit;
                obstacle = building;
            }
            return start + delta * fraction;
        }

        private static Vector2 ClampToMap(WorldState world, Vector2 position)
            => Vector2.Clamp(position, Vector2.Zero, new Vector2(world.Rules.MapSize));
        private static Vector2 Perpendicular(Vector2 vector) => new Vector2(-vector.Y, vector.X);
        private static float Square(float value) => value * value;
    }
}
