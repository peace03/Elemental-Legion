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

        internal void ResolveDeaths(WorldState world, bool fromCombat = true)
        {
            dead.Clear();
            int write = 0;
            for (int read = 0; read < world.Units.Count; read++)
            {
                UnitState unit = world.Units[read];
                if (!unit.Dead) { world.Units[write++] = unit; continue; }
                dead.Add(unit); world.UnitById.Remove(unit.Id); world.Metrics.Deaths++;
                world.Events.Add(new SimEvent(SimEventKind.Death, unit.KillerOwner, unit.KillerSource, unit.Id, unit.Position,
                    element:unit.Element,entityId:unit.Id,sourceKind:unit.KillerTower ? ObservedEntityKind.Building : ObservedEntityKind.Unit));
                if (unit.OwnerId >= 0)
                {
                    PlayerState previous = world.PlayerById[unit.OwnerId]; previous.Population--; previous.FormationDirty = true;
                    previous.JourneyTotal = previous.JourneyTotal.Add(lost: 1);
                }
            }
            if (write < world.Units.Count) world.Units.RemoveRange(write, world.Units.Count - write);
            foreach (UnitState unit in dead)
            {
                bool hasKiller = world.PlayerById.TryGetValue(unit.KillerOwner, out PlayerState killer) && !killer.BaseDestroyed;
                if (unit.FreeOrigin && (!fromCombat || !hasKiller || killer.Id == unit.OwnerId || killer.GhostEndTick != 0 ||
                    !world.PlayerById.TryGetValue(unit.OwnerId, out PlayerState victim) || victim.GhostEndTick != 0)) continue;
                int available = hasKiller ? killer.PopulationCap - killer.Population - killer.ReservedPopulation : 0;
                bool neutralVictim = unit.OwnerId < 0;
                byte squad = unit.KillerSquad;
                bool converts = ConversionRules.ShouldConvert(world.Rules, unit.Converted, unit.KillerTower, neutralVictim, available, world.Random.NextDouble());
                bool validPvpSource = unit.KillerOwner != unit.OwnerId && unit.KillerSource > 0 && squad >= 1 && squad <= 4;
                if (hasKiller && converts && (neutralVictim || validPvpSource) && world.AvailableUnitSlots > 0 && ConversionRecoverySystem.CanAccept(world, killer, squad))
                {
                    if (!neutralVictim && !world.Rules.PvpConversionInstant)
                    {
                        ConversionRecoverySystem.Add(world, new ConversionCandidateState
                        {
                            Id = unit.Id, Element = unit.Element, PreviousOwner = unit.OwnerId, SecureOwner = killer.Id,
                            SecureSquad = squad, Source = unit.KillerSource, Position = unit.Position,
                            CreatedTick = world.Tick, EndTick = world.Tick + world.Rules.PvpRecoveryLifetimeTicks,
                            Converted = unit.Converted, RescuedOnce = unit.RescuedOnce, WasNeutral = unit.WasNeutral,
                            MovementRevision = unit.MovementRevision, TrainingAttackBonus = unit.TrainingAttackBonus,
                            FreeOrigin = unit.FreeOrigin
                        });
                        continue;
                    }
                    int source = unit.KillerSource;
                    unit.OwnerId = killer.Id; unit.Converted = true; unit.Dead = false; unit.Garrison = squad == 4;
                    unit.IsSquadTransfer = false;
                    unit.IsReinforcing = unit.Garrison && Vector2.DistanceSquared(unit.Position, killer.BasePosition) > world.Rules.GarrisonRadius * world.Rules.GarrisonRadius;
                    unit.DestinationSquadId = unit.IsReinforcing ? (byte)4 : (byte)0;
                    unit.SquadId = unit.IsReinforcing ? (byte)0 : squad >= 1 && squad <= 4 ? squad : (byte)1;
                    if (!neutralVictim) unit.MovementRevision = unchecked(unit.MovementRevision + 1);
                    unit.CatchingUp = unit.NeutralReturning = unit.FreeOrigin = false;
                    unit.Ghost = killer.GhostEndTick != 0;
                    unit.Protected = killer.ProtectionEndTick > world.Tick;
                    unit.Home = unit.Position;
                    unit.Detour = unit.BuildingTarget = null;
                    unit.DetourSide = 0;
                    unit.Health = world.Rules.Elements[unit.Element].Hp *
                        (neutralVictim ? world.Rules.ConvertedHealthRatio : world.Rules.PvpRecoveryConvertedHealthRatio);
                    unit.TrainingAttackBonus = 0;
                    unit.Shield = 0; unit.AttackRemaining = 0; unit.ShieldRemaining = 0; unit.Target = null;
                    unit.SpeedMultiplier = 1; unit.RangeMultiplier = 1; unit.Haste = 0; unit.SmokeDebuff = 0; unit.SteamDebuff = 0; unit.SandDebuff = 0; unit.AreaDamage = 0; unit.SmokeOwners = unit.SmokeOwners2 = unit.SmokeOwners3 = 0;
                    unit.AreaOwner = unit.AreaSource = unit.AreaElement = unit.AreaSquad = 0;
                    unit.KillerOwner = -1; unit.KillerSource = unit.KillerElement = unit.KillerSquad = 0;
                    unit.ImpactTick = unit.ImpactIndex = -1;
                    Array.Clear(unit.LastHitSquads, 0, unit.LastHitSquads.Length);
                    Array.Clear(unit.Statuses, 0, unit.Statuses.Length);
                    for (int i = 0; i < unit.LastHitTimes.Length; i++) { unit.LastHitTimes[i] = double.NegativeInfinity; unit.LastHitOwners[i] = -1; }
                    if (killer.ConvertedCount < long.MaxValue - killer.TrainedCount) killer.ConvertedCount++;
                    killer.JourneyTotal = killer.JourneyTotal.Add(converted: 1);
                    world.InsertUnit(unit); world.Metrics.Conversions++;
                    world.Events.Add(new SimEvent(SimEventKind.Converted, killer.Id, source, unit.Id, unit.Position, unit.Health,element:unit.Element,entityId:unit.Id));
                }
                else
                {
                    int amount = world.Rules.DropAmount(unit.Element);
                    DropCore(world, unit.Position, amount, unit.KillerOwner, unit.KillerSource, unit.Id, unit.KillerTower);
                }
            }
            if (dead.Count > 0) { world.Units.Sort(CompareIds); world.Candidates.Sort((left, right) => left.Id.CompareTo(right.Id)); }
        }

        internal static int DropCore(WorldState world, Vector2 position, int amount,
            int owner = -1, int source = 0, int target = 0, bool tower = false)
        {
            if (amount <= 0) return 0;
            double expires = world.Time + world.Rules.CoreLifetime;
            if (world.Cores.Count >= Net.GameplayCodec.MaxObjects)
            {
                // 핵이 가득 찬 경우에는 가까운 묶음에 합쳐 보상과 저장 용량을 보존한다.
                CoreState nearest = null;
                float distance = float.PositiveInfinity;
                for (int index = world.Cores.Count - 1; index >= 0; index--)
                {
                    CoreState core = world.Cores[index];
                    if (core.Expires <= world.Time)
                    {
                        world.Cores.RemoveAt(index);
                        break;
                    }
                    if (core.Amount > int.MaxValue - amount) continue;
                    float squared = Vector2.DistanceSquared(position, core.Position);
                    if (squared >= distance) continue;
                    distance = squared;
                    nearest = core;
                }
                if (world.Cores.Count >= Net.GameplayCodec.MaxObjects)
                {
                    if (nearest == null) return 0;
                    nearest.Amount += amount;
                    nearest.Expires = Math.Max(nearest.Expires, expires);
                    ReportCoreDrop(world, nearest.Id, amount, position, owner, source, target, tower);
                    return nearest.Id;
                }
            }
            int id = world.NextId++;
            world.Cores.Add(new CoreState { Id = id, Amount = amount, Position = position, Expires = expires });
            ReportCoreDrop(world, id, amount, position, owner, source, target, tower);
            return id;
        }

        private static void ReportCoreDrop(WorldState world, int id, int amount, Vector2 position,
            int owner, int source, int target, bool tower)
        {
            world.Metrics.Drops++;
            world.Events.Add(new SimEvent(SimEventKind.CoreDropped, owner, source, target, position, amount,
                entityId: id, entityKind: ObservedEntityKind.Core, sourceKind: tower ? ObservedEntityKind.Building : ObservedEntityKind.Unit));
        }

        internal static void BeginProduction(WorldState world, BuildingState building)
        {
            if (building.Kind == BuildingKind.Collector) building.NextProductionTick = world.Tick + world.Rules.CollectorIntervalTicks;
        }

        internal static void PauseProduction(WorldState world, PlayerState player)
        {
            foreach (BuildingState building in world.Buildings)
                if (building.OwnerId == player.Id && building.NextProductionTick != 0)
                    building.ProductionPausedAtTick = world.Tick;
        }

        internal static void ResumeProduction(WorldState world, PlayerState player)
        {
            foreach (BuildingState building in world.Buildings)
                if (building.OwnerId == player.Id && building.ProductionPausedAtTick >= 0)
                {
                    building.NextProductionTick += world.Tick - building.ProductionPausedAtTick;
                    building.ProductionPausedAtTick = -1;
                }
        }

        internal static void ProduceCores(WorldState world)
        {
            foreach (BuildingState building in world.Buildings)
            {
                if (building.NextProductionTick == 0 || building.NextProductionTick > world.Tick || building.ProductionPausedAtTick >= 0) continue;
                PlayerState owner = world.PlayerById[building.OwnerId];
                int cycleAmount = world.Rules.CollectorProductionAmount(building.UpgradeBranch, building.UpgradeRank);
                int produced = (int)Math.Min(cycleAmount, long.MaxValue - owner.Cores);
                building.NextProductionTick = world.Tick + world.Rules.CollectorProductionTicks(building.UpgradeBranch, building.UpgradeRank);
                if (produced == 0) continue;
                owner.GainCores(produced);
                world.Events.Add(new SimEvent(SimEventKind.CoresProduced, owner.Id, 0, 0, building.Position, produced,
                    entityId: building.Id, entityKind: ObservedEntityKind.Building));
            }
        }

        internal void TickCores(WorldState world)
        {
            if (world.Cores.Count == 0) return;
            Span<int> joined = stackalloc int[world.Rules.MaxPlayers * 4];
            SquadSystem.CountMembers(world, joined, Span<int>.Empty);
            int write = 0;
            for (int read = 0; read < world.Cores.Count; read++)
            {
                CoreState core = world.Cores[read];
                if (core.Expires <= world.Time) continue;
                PlayerState winner = null;
                float distance = float.PositiveInfinity, radiusSquared = world.Rules.CorePickupRadius * world.Rules.CorePickupRadius;
                foreach (PlayerState player in world.Players)
                    foreach (SquadState squad in player.Squads)
                        if (squad.Id < 4 && joined[player.Slot * 4 + squad.Id - 1] > 0)
                            Consider(player, Vector2.DistanceSquared(squad.Position, core.Position), core.Amount, radiusSquared,
                                ref winner, ref distance);
                world.Grid.Query(core.Position, world.Rules.CorePickupRadius, world.Query);
                foreach (UnitState unit in world.Query)
                    if (unit.OwnerId >= 0)
                        Consider(world.PlayerById[unit.OwnerId], Vector2.DistanceSquared(unit.Position, core.Position), core.Amount, radiusSquared,
                            ref winner, ref distance);
                if (winner == null) { world.Cores[write++] = core; continue; }
                winner.GainCores(core.Amount); world.Metrics.Pickups++;
                world.Events.Add(new SimEvent(SimEventKind.CorePickedUp, winner.Id, 0, core.Id, core.Position, core.Amount,
                    entityId: core.Id, entityKind: ObservedEntityKind.Core));
            }
            if (write < world.Cores.Count) world.Cores.RemoveRange(write, world.Cores.Count - write);
        }

        private static void Consider(PlayerState player, float candidate, int amount, float radiusSquared,
            ref PlayerState winner, ref float distance)
        {
            if (player.GhostEndTick != 0 || player.Cores > long.MaxValue - amount || candidate > radiusSquared) return;
            if (candidate < distance || candidate == distance && (winner == null || player.Id < winner.Id))
            {
                winner = player;
                distance = candidate;
            }
        }
        private static int CompareIds(UnitState left, UnitState right) => left.Id.CompareTo(right.Id);
    }
}
