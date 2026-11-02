using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>사망을 먼저 제거한 뒤 전향·드롭을 확정하고 핵의 지급과 소멸을 처리한다.</summary>
    internal sealed class EconomySystem
    {
        private readonly List<UnitState> dead = new List<UnitState>();
        internal void Reserve(int count) { dead.Capacity = Math.Max(dead.Capacity, count); }

        internal void ResolveDeaths(WorldState world)
        {
            dead.Clear();
            int write = 0;
            for (int read = 0; read < world.Units.Count; read++)
            {
                UnitState unit = world.Units[read];
                if (!unit.Dead) { world.Units[write++] = unit; continue; }
                dead.Add(unit); world.UnitById.Remove(unit.Id); world.Metrics.Deaths++;
                world.Events.Add(new SimEvent(SimEventKind.Death, unit.KillerOwner, unit.KillerSource, unit.Id, unit.Position,element:unit.Element,entityId:unit.Id));
                if (unit.OwnerId >= 0)
                {
                    PlayerState previous = world.PlayerById[unit.OwnerId]; previous.Population--; previous.FormationDirty = true;
                }
            }
            if (write < world.Units.Count) world.Units.RemoveRange(write, world.Units.Count - write);
            foreach (UnitState unit in dead)
            {
                bool hasKiller = world.PlayerById.TryGetValue(unit.KillerOwner, out PlayerState killer);
                int available = hasKiller ? killer.PopulationCap - killer.Population - (killer.TrainingElement >= 0 ? 1 : 0) : 0;
                bool converts = ConversionRules.ShouldConvert(world.Rules, unit.Converted, unit.KillerTower, unit.OwnerId < 0, available, world.Random.NextDouble());
                if (hasKiller && converts)
                {
                    int source = unit.KillerSource;
                    unit.OwnerId = killer.Id; unit.Converted = true; unit.Dead = false; unit.Garrison = false;
                    unit.Health = world.Rules.Elements[unit.Element].Hp * world.Rules.ConvertedHealthRatio;
                    unit.Shield = 0; unit.AttackRemaining = 0; unit.ShieldRemaining = 0; unit.Target = null;
                    unit.SpeedMultiplier = 1; unit.RangeMultiplier = 1; unit.Haste = 0; unit.SmokeDebuff = 0; unit.SteamDebuff = 0; unit.SandDebuff = 0; unit.AreaDamage = 0; unit.SmokeOwners = 0;
                    Array.Clear(unit.Statuses, 0, unit.Statuses.Length);
                    for (int i = 0; i < unit.LastHitTimes.Length; i++) { unit.LastHitTimes[i] = double.NegativeInfinity; unit.LastHitOwners[i] = -1; }
                    killer.Population++; killer.FormationDirty = true;
                    killer.ConvertedCount++;
                    world.Units.Add(unit); world.UnitById.Add(unit.Id, unit); world.Metrics.Conversions++;
                    world.Events.Add(new SimEvent(SimEventKind.Converted, killer.Id, source, unit.Id, unit.Position, unit.Health,element:unit.Element,entityId:unit.Id));
                }
                else
                {
                    int amount = world.Rules.DropAmount(unit.Element);
                    if (amount == 0) continue;
                    int coreId=world.NextId++;
                    world.Cores.Add(new CoreState { Id = coreId, Amount = amount, Position = unit.Position, Expires = world.Time + world.Rules.CoreLifetime });
                    world.Metrics.Drops++;
                    world.Events.Add(new SimEvent(SimEventKind.CoreDropped, unit.KillerOwner, unit.KillerSource, unit.Id, unit.Position, amount,entityId:coreId,entityKind:ObservedEntityKind.Core));
                }
            }
            if (dead.Count > 0) world.Units.Sort(CompareIds);
        }

        internal void TickCores(WorldState world)
        {
            int write = 0;
            for (int read = 0; read < world.Cores.Count; read++)
            {
                CoreState core = world.Cores[read];
                if (core.Expires <= world.Time) continue;
                PlayerState winner = null;
                float distance = float.PositiveInfinity, radiusSquared = world.Rules.CorePickupRadius * world.Rules.CorePickupRadius;
                foreach (PlayerState player in world.Players)
                    Consider(player, Vector2.DistanceSquared(player.LegionPosition, core.Position), core.Amount, radiusSquared, ref winner, ref distance);
                world.Grid.Query(core.Position, world.Rules.CorePickupRadius, world.Query);
                foreach (UnitState unit in world.Query)
                    if (unit.OwnerId >= 0 && !unit.Garrison) Consider(world.PlayerById[unit.OwnerId], Vector2.DistanceSquared(unit.Position, core.Position), core.Amount, radiusSquared, ref winner, ref distance);
                if (winner == null) { world.Cores[write++] = core; continue; }
                winner.Cores += core.Amount; world.Metrics.Pickups++;
                world.Events.Add(new SimEvent(SimEventKind.CorePickedUp, winner.Id, 0, core.Id, core.Position, core.Amount,entityId:core.Id,entityKind:ObservedEntityKind.Core));
            }
            if (write < world.Cores.Count) world.Cores.RemoveRange(write, world.Cores.Count - write);
        }

        private static void Consider(PlayerState player, float candidate, int amount, float radiusSquared, ref PlayerState winner, ref float distance)
        {
            if (player.Cores > long.MaxValue - amount || candidate > radiusSquared) return;
            if (candidate < distance || candidate == distance && (winner == null || player.Id < winner.Id)) { winner = player; distance = candidate; }
        }
        private static int CompareIds(UnitState left, UnitState right) => left.Id.CompareTo(right.Id);
    }
}
