using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>건물 표적, 기본 피해, 타워 공격과 본진 방어막을 처리한다.</summary>
    internal static class SiegeSystem
    {
        internal static BuildingState FindTarget(WorldState world, UnitState unit, float range, float leash)
        {
            BuildingState best = null;
            float nearest = range + leash;
            foreach (BuildingState building in world.Buildings)
            {
                if (building.OwnerId == unit.OwnerId || building.Health <= 0 || !building.BlocksMovement) continue;
                float reach = nearest + building.Radius;
                if (Vector2.DistanceSquared(unit.Position, building.Position) > reach * reach ||
                    !InRange(unit.Home, building, range + leash) || IsProtected(world, building.OwnerId)) continue;
                float distance = Math.Max(0, Vector2.Distance(unit.Position, building.Position) - building.Radius);
                if (best != null && distance == nearest && building.Id >= best.Id) continue;
                nearest = distance;
                best = building;
            }
            return best;
        }

        internal static bool InRange(Vector2 position, BuildingState building, float range)
        {
            float reach = building.Radius + range;
            return Vector2.DistanceSquared(position, building.Position) <= reach * reach;
        }

        internal static bool IsProtected(WorldState world, int ownerId)
            => world.ProtectionEnabled && world.PlayerById.TryGetValue(ownerId, out PlayerState player) && player.ProtectionEndTick > world.Tick;

        internal static void BreakProtection(WorldState world, int ownerId, int targetOwner)
        {
            if (world.ProtectionEnabled && targetOwner >= 0 && ownerId != targetOwner && world.PlayerById.TryGetValue(ownerId, out PlayerState player))
                player.ProtectionEndTick = 0;
        }

        internal static void Attack(WorldState world, UnitState source, BuildingState target, float damage)
        {
            if (IsProtected(world, target.OwnerId)) return;
            BreakProtection(world, source.OwnerId, target.OwnerId);
            PlayerState defender = world.PlayerById[target.OwnerId];
            float remaining = damage;
            if (target.Kind == BuildingKind.Base && defender.Dome != null && defender.Dome.Health > 0)
                remaining -= ApplyDamage(defender.Dome, source, remaining);
            float actual = damage - remaining + ApplyDamage(target, source, remaining);
            if (actual > 0 && source.OwnerId != target.OwnerId && world.Tick >= defender.NextDefenseAlarmTick)
            {
                defender.NextDefenseAlarmTick = world.Tick + world.Rules.DefenseAlarmRepeatTicks;
                world.Events.Add(new SimEvent(SimEventKind.BaseUnderAttack, defender.Id, 0, target.Id, target.Position, actual,
                    entityId: target.Id, entityKind: ObservedEntityKind.Building, targetKind: ObservedEntityKind.Building));
            }
            if (target.Health == 0 && target.Kind == BuildingKind.Base && !defender.BaseDestroyed)
            {
                defender.BaseDestroyed = true;
                if (defender.GhostEndTick == 0 && world.PlayerById.TryGetValue(source.OwnerId, out PlayerState attacker) &&
                    attacker.GhostEndTick == 0)
                    attacker.BattleWins++;
            }
            world.Metrics.Attacks++;
            world.Events.Add(new SimEvent(SimEventKind.Attack, source.OwnerId, source.Id, target.Id, target.Position, actual,
                element: source.Element, sourcePosition: source.Position, hasSourcePosition: true,
                entityId: target.Id, entityKind: ObservedEntityKind.Building, targetKind: ObservedEntityKind.Building));
        }

        internal static void RunTowers(WorldState world)
        {
            float range = world.Rules.TowerRange;
            foreach (BuildingState tower in world.Buildings)
            {
                if (tower.TowerElement < 0 || tower.Health <= 0 || tower.IsUnderConstruction) continue;
                tower.AttackRemaining -= world.Rules.DeltaTime;
                if (tower.AttackRemaining > 0) continue;
                world.Grid.Query(tower.Position, range, world.Query);
                UnitState closest = null;
                float nearest = range * range;
                foreach (UnitState unit in world.Query)
                {
                    if (unit.OwnerId == tower.OwnerId || IsProtected(world, unit.OwnerId)) continue;
                    float distance = Vector2.DistanceSquared(tower.Position, unit.Position);
                    if (distance > nearest || closest != null && distance == nearest && unit.Id >= closest.Id) continue;
                    closest = unit; nearest = distance;
                }
                if (closest == null)
                {
                    tower.AttackRemaining = 0;
                    continue;
                }
                var element = world.Rules.Elements[tower.TowerElement];
                float damage = CombatSystem.Damage(world, closest, element.Attack * world.Rules.TowerDamageMultiplier,
                    tower.OwnerId, tower.Id, tower.TowerElement, tower: true, impact: false);
                tower.AttackRemaining += 1.0 / element.AttacksPerSecond;
                world.Metrics.Attacks++;
                world.Events.Add(new SimEvent(SimEventKind.Attack, tower.OwnerId, tower.Id, closest.Id, closest.Position, damage,
                    element: tower.TowerElement, sourcePosition: tower.Position, hasSourcePosition: true, entityId: closest.Id,
                    sourceKind: ObservedEntityKind.Building));
            }
        }

        private static float ApplyDamage(BuildingState target, UnitState source, float damage)
        {
            if (target.Health <= 0 || damage <= 0) return 0;
            float applied = Math.Min(target.Health, damage);
            target.Health -= applied;
            if (target.Health == 0)
            {
                target.KillerOwner = source.OwnerId;
                target.KillerSource = source.Id;
                target.KillerElement = source.Element;
                target.KillerSquad = SquadSystem.FieldMember(source) ? source.SquadId : (byte)0;
            }
            return applied;
        }
    }
}
