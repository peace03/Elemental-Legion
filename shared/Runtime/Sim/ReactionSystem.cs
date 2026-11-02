using System;
using System.Collections.Generic;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>같은 군단의 상태 출처를 확인하고 반응과 고정 영역을 처리한다. 상태는 발동 후에도 유지한다.</summary>
    internal sealed class ReactionSystem
    {
        private readonly List<UnitState> candidates = new List<UnitState>();
        internal void Reserve(int count) { candidates.Capacity = count; }

        internal ulong Match(WorldState world, UnitState source, UnitState target, out float damageMultiplier, out float chainBonus)
        {
            ulong matches = 0;
            damageMultiplier = 1; chainBonus = 0;
            if (source.OwnerId < 0 || source.Garrison) return matches;
            PlayerState player = world.PlayerById[source.OwnerId];
            for (int i = 0; i < world.Rules.Reactions.Count; i++)
            {
                ReactionDefinition definition = world.Rules.Reactions[i];
                if (definition.Trigger != source.Element || (player.ElementMask & definition.RequiredMask) != definition.RequiredMask) continue;
                bool applies;
                if (definition.PreviousHitElement >= 0)
                    applies = target.LastHitOwners[definition.PreviousHitElement] == source.OwnerId && world.Time - target.LastHitTimes[definition.PreviousHitElement] <= definition.Window;
                else if (definition.Status == StatusKind.Smoke) applies = (target.SmokeOwners & (1UL << player.Slot)) != 0;
                else
                {
                    TimedStatus status = target.Statuses[(int)definition.Status];
                    applies = status.Expires > world.Time && status.LegionOwner == source.OwnerId;
                }
                if (!applies) continue;
                matches |= 1UL << i;
                if (definition.Kind == ReactionKind.Electrocute) { damageMultiplier *= definition.DamageMultiplier; chainBonus += definition.ChainBonus; }
            }
            return matches;
        }

        internal void Trigger(WorldState world, UnitState source, UnitState target, ulong matches)
        {
            for (int i = 0; i < world.Rules.Reactions.Count; i++)
            {
                if ((matches & (1UL << i)) == 0) continue;
                ReactionDefinition definition = world.Rules.Reactions[i];
                world.Metrics.ReactionBuffer[(int)definition.Kind]++;
                world.Events.Add(new SimEvent(SimEventKind.Reaction, source.OwnerId, source.Id, target.Id, target.Position, detail: (int)definition.Kind));
                switch (definition.Kind)
                {
                    case ReactionKind.Freeze:
                        CombatSystem.ApplyStatus(world, target, StatusKind.Freeze, source, definition.Duration, definition.SpeedDebuff);
                        CombatSystem.RefreshMultipliers(target, world.Time);
                        break;
                    case ReactionKind.Steam:
                    case ReactionKind.Sandstorm:
                        CreateArea(world, source, target, definition);
                        break;
                    case ReactionKind.BurnSpread:
                        TimedStatus burn = target.Statuses[(int)StatusKind.Burn];
                        world.Grid.Query(target.Position, definition.Radius, candidates);
                        foreach (UnitState extra in candidates)
                        {
                            if (extra == target || extra.OwnerId == source.OwnerId) continue;
                            ref TimedStatus current = ref extra.Statuses[(int)StatusKind.Burn];
                            if (current.Expires <= world.Time) current = burn;
                            else
                            {
                                double expiry = Math.Max(current.Expires, burn.Expires);
                                if (burn.Dps >= current.Dps) current = burn;
                                current.Expires = expiry;
                            }
                        }
                        break;
                    case ReactionKind.PlasmaBurst:
                        world.Grid.Query(target.Position, definition.Radius, candidates);
                        foreach (UnitState extra in candidates)
                            if (extra.OwnerId != source.OwnerId) CombatSystem.Damage(world, extra, definition.Damage * CombatSystem.AttackMultiplier(world, source), source.OwnerId, source.Id, source.Element);
                        break;
                }
            }
        }

        internal void PrepareAreas(WorldState world)
        {
            foreach (UnitState unit in world.Units) unit.AreaDamage = 0;
            for (int i = world.Areas.Count - 1; i >= 0; i--)
                if (world.Areas[i].Expires <= world.Time - world.Rules.DeltaTime) world.Areas.RemoveAt(i);
            foreach (AreaState area in world.Areas) ApplyArea(world, area, true);
            foreach (UnitState unit in world.Units) CombatSystem.RefreshMultipliers(unit, world.Time);
        }

        internal static void TickAreaDamage(WorldState world)
        {
            foreach (UnitState unit in world.Units)
                if (unit.AreaDamage > 0 && unit.Health > 0) CombatSystem.Damage(world, unit, unit.AreaDamage, unit.AreaOwner, unit.AreaSource, unit.AreaElement);
        }

        private void CreateArea(WorldState world, UnitState source, UnitState target, ReactionDefinition definition)
        {
            AreaState area = null;
            foreach (AreaState existing in world.Areas)
                if (existing.Owner == source.OwnerId && existing.Target == target.Id && existing.Definition == definition) { area = existing; break; }
            if (area == null) { area = new AreaState(); world.Areas.Add(area); }
            area.Owner = source.OwnerId; area.Source = source.Id; area.Element = source.Element; area.Target = target.Id;
            area.Position = target.Position; area.Expires = world.Time + definition.Duration; area.Definition = definition;
            ApplyArea(world, area, false);
        }

        private void ApplyArea(WorldState world, AreaState area, bool includeDamage)
        {
            ReactionDefinition definition = area.Definition;
            world.Grid.Query(area.Position, definition.Radius, candidates);
            float damage = (float)(definition.DamagePerSecond * Math.Max(0, Math.Min(world.Time, area.Expires) - (world.Time - world.Rules.DeltaTime)));
            foreach (UnitState unit in candidates)
            {
                if (unit.OwnerId == area.Owner) continue;
                if (area.Expires > world.Time)
                {
                    if (definition.Kind == ReactionKind.Steam) unit.SteamDebuff = Math.Min(unit.SteamDebuff, definition.RangeDebuff);
                    else unit.SandDebuff = Math.Min(unit.SandDebuff, definition.RangeDebuff);
                    CombatSystem.RefreshMultipliers(unit, world.Time);
                }
                // 같은 종류의 장판은 가장 큰 한 번의 피해만 적용한다.
                if (includeDamage && damage >= unit.AreaDamage)
                { unit.AreaDamage = damage; unit.AreaOwner = area.Owner; unit.AreaSource = area.Source; unit.AreaElement = area.Element; }
            }
        }
    }
}
