using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>표적·오라·시간 기반 상태와 원소 공격을 처리한다. 추가 피해는 기본 피해 경로만 호출해 재귀 발동을 막는다.</summary>
    internal sealed class CombatSystem
    {
        private readonly List<UnitState> secondary = new List<UnitState>();
        private readonly ReactionSystem reactions = new ReactionSystem();
        internal void Reserve(int count) { secondary.Capacity = count; reactions.Reserve(count); }

        internal void Prepare(WorldState world)
        {
            foreach (PlayerState player in world.Players) player.ElementMask = 0;
            foreach (UnitState unit in world.Units)
            {
                unit.Haste = 0; unit.SmokeDebuff = 0; unit.SteamDebuff = 0; unit.SandDebuff = 0; unit.SmokeOwners = 0;
                if (unit.OwnerId >= 0 && !unit.Garrison && unit.Health > 0) world.PlayerById[unit.OwnerId].ElementMask |= 1UL << unit.Element;
            }
            foreach (UnitState source in world.Units)
            {
                if (source.Health <= 0) continue;
                ElementDefinition definition = world.Rules.Elements[source.Element];
                bool shieldPulse = false;
                if (definition.Effect == EffectKind.ShieldAura)
                {
                    source.ShieldRemaining -= world.Rules.DeltaTime;
                    if (source.ShieldRemaining <= 0)
                    {
                        shieldPulse = true;
                        source.ShieldRemaining += definition.Interval * (Math.Floor(-source.ShieldRemaining / definition.Interval) + 1);
                    }
                }
                if (definition.Effect != EffectKind.SmokeAura && definition.Effect != EffectKind.HasteAura && !shieldPulse) continue;
                world.Grid.Query(source.Position, definition.Radius, world.Query);
                foreach (UnitState target in world.Query)
                {
                    if (definition.Effect == EffectKind.SmokeAura && target.OwnerId != source.OwnerId)
                    {
                        target.SmokeDebuff = Math.Min(target.SmokeDebuff, definition.Amount);
                        if (source.OwnerId >= 0 && !source.Garrison) target.SmokeOwners |= 1UL << world.PlayerById[source.OwnerId].Slot;
                    }
                    else if (target.OwnerId == source.OwnerId)
                    {
                        if (definition.Effect == EffectKind.HasteAura) target.Haste = Math.Max(target.Haste, definition.Amount);
                        else if (shieldPulse) target.Shield = Math.Max(target.Shield, definition.Absorb);
                    }
                }
            }
            foreach (UnitState unit in world.Units) RefreshMultipliers(unit, world.Time);
            reactions.PrepareAreas(world);
        }

        internal void SelectTargets(WorldState world)
        {
            foreach (UnitState unit in world.Units)
            {
                unit.Target = null;
                if (unit.Health <= 0) continue;
                ElementDefinition definition = world.Rules.Elements[unit.Element];
                float range = definition.Range * unit.RangeMultiplier;
                float leash = !unit.Garrison && unit.OwnerId >= 0 && definition.Effect != EffectKind.Heal ? world.Rules.LeashDistance : 0;
                unit.Target = world.Grid.FindNearest(unit, range, leash, definition.Effect == EffectKind.Heal, world.Rules);
            }
        }

        internal void Run(WorldState world)
        {
            ReactionSystem.TickAreaDamage(world);
            foreach (UnitState unit in world.Units)
            {
                if (unit.Health <= 0) continue;
                TickDot(world, unit, StatusKind.Burn);
                TickDot(world, unit, StatusKind.Radiation);
            }
            foreach (UnitState source in world.Units)
            {
                if (source.Health <= 0) continue;
                ElementDefinition definition = world.Rules.Elements[source.Element];
                source.AttackRemaining -= world.Rules.DeltaTime * source.SpeedMultiplier;
                UnitState target = source.Target;
                float range = definition.Range * source.RangeMultiplier;
                bool valid = target != null && target.Health > 0 && Vector2.DistanceSquared(source.Position, target.Position) <= range * range;
                if (definition.Effect == EffectKind.Heal) valid &= target != null && target.OwnerId == source.OwnerId && target.Health < world.Rules.Elements[target.Element].Hp;
                else valid &= target != null && target.OwnerId != source.OwnerId;
                if (!valid) { source.AttackRemaining = Math.Max(0, source.AttackRemaining); continue; }
                if (source.AttackRemaining > 0) continue;
                if (definition.Effect == EffectKind.Heal) target.Health = Math.Min(world.Rules.Elements[target.Element].Hp, target.Health + definition.Heal);
                else Attack(world, source, target, definition);
                source.AttackRemaining += 1.0 / definition.AttacksPerSecond;
            }
        }

        private void Attack(WorldState world, UnitState source, UnitState target, ElementDefinition definition)
        {
            float attackMultiplier = AttackMultiplier(world, source);
            ulong matches = reactions.Match(world, source, target, out float multiplier, out float chainBonus);
            float primary = definition.Attack * attackMultiplier;
            float actual = Damage(world, target, primary * multiplier, source.OwnerId, source.Id, source.Element);
            world.Metrics.Attacks++;
            world.Events.Add(new SimEvent(SimEventKind.Attack, source.OwnerId, source.Id, target.Id, target.Position, actual));
            switch (definition.Effect)
            {
                case EffectKind.Chill: ApplyStatus(world, target, StatusKind.Chill, source, definition.Duration, definition.Amount); RefreshMultipliers(target, world.Time); break;
                case EffectKind.Wet: ApplyStatus(world, target, StatusKind.Wet, source, definition.Duration); break;
                case EffectKind.Burn: ApplyStatus(world, target, StatusKind.Burn, source, definition.Duration, dps: definition.DamagePerSecond * attackMultiplier); break;
                case EffectKind.Radiation: ApplyStatus(world, target, StatusKind.Radiation, source, definition.Duration, dps: definition.DamagePerSecond * attackMultiplier); break;
                case EffectKind.Lifesteal: source.Health = Math.Min(definition.Hp, source.Health + actual * definition.Ratio); break;
                case EffectKind.Chain: Chain(world, source, target, definition, definition.Radius + chainBonus); break;
                case EffectKind.Splash:
                    world.Grid.Query(target.Position, definition.Radius, secondary);
                    foreach (UnitState extra in secondary)
                        if (extra != target && extra.OwnerId != source.OwnerId) Damage(world, extra, primary, source.OwnerId, source.Id, source.Element);
                    break;
                case EffectKind.Pierce: Pierce(world, source, target, definition, primary); break;
            }
            reactions.Trigger(world, source, target, matches);
            if (source.OwnerId >= 0 && !source.Garrison)
            {
                target.LastHitTimes[source.Element] = world.Time;
                target.LastHitOwners[source.Element] = source.OwnerId;
            }
        }

        private void Chain(WorldState world, UnitState source, UnitState target, ElementDefinition definition, float radius)
        {
            world.Grid.Query(target.Position, radius, secondary);
            for (int count = 0; count < definition.ExtraTargets; count++)
            {
                int selected = -1;
                float nearest = float.PositiveInfinity;
                for (int i = 0; i < secondary.Count; i++)
                {
                    UnitState extra = secondary[i];
                    if (extra == target || extra.OwnerId == source.OwnerId || extra.Health <= 0) continue;
                    float distance = Vector2.DistanceSquared(target.Position, extra.Position);
                    if (distance < nearest || distance == nearest && (selected < 0 || extra.Id < secondary[selected].Id)) { nearest = distance; selected = i; }
                }
                if (selected < 0) break;
                UnitState chosen = secondary[selected]; secondary.RemoveAt(selected);
                Damage(world, chosen, definition.Attack * AttackMultiplier(world, source) * definition.Ratio, source.OwnerId, source.Id, source.Element);
            }
        }

        private void Pierce(WorldState world, UnitState source, UnitState target, ElementDefinition definition, float damage)
        {
            Vector2 direction = target.Position - source.Position;
            if (direction.LengthSquared() == 0) direction = Vector2.UnitY;
            else direction = Vector2.Normalize(direction);
            float range = definition.Range * source.RangeMultiplier;
            world.Grid.Query(source.Position, range + definition.Width, secondary);
            for (int count = 1; count < definition.MaxTargets; count++)
            {
                int selected = -1; float nearest = float.PositiveInfinity;
                for (int i = 0; i < secondary.Count; i++)
                {
                    UnitState extra = secondary[i];
                    if (extra == target || extra.OwnerId == source.OwnerId || extra.Health <= 0) continue;
                    Vector2 delta = extra.Position - source.Position;
                    float projection = Vector2.Dot(delta, direction);
                    if (projection < 0 || projection > range || delta.LengthSquared() - projection * projection > definition.Width * definition.Width * 0.25f) continue;
                    if (projection < nearest || projection == nearest && (selected < 0 || extra.Id < secondary[selected].Id)) { nearest = projection; selected = i; }
                }
                if (selected < 0) break;
                UnitState chosen = secondary[selected]; secondary.RemoveAt(selected);
                Damage(world, chosen, damage, source.OwnerId, source.Id, source.Element);
            }
        }

        internal static float AttackMultiplier(WorldState world, UnitState source) => !source.WasNeutral ? 1 : source.OwnerId < 0 ? world.Rules.NeutralAttackMultiplier : world.Rules.ConvertedNeutralAttackMultiplier;

        internal static float Damage(WorldState world, UnitState target, float amount, int owner, int source, int element, bool tower = false)
        {
            if (target.Health <= 0 || amount <= 0) return 0;
            amount *= world.Rules.Matchup(element, target.Element);
            float absorbed = Math.Min(target.Shield, amount);
            target.Shield -= absorbed; amount -= absorbed;
            float actual = Math.Min(target.Health, amount);
            target.Health = Math.Max(0, target.Health - amount);
            if (target.Health <= 0)
            {
                target.Dead = true; target.KillerOwner = owner; target.KillerSource = source; target.KillerElement = element; target.KillerTower = tower;
                if (target.OwnerId >= 0 && !target.Garrison)
                {
                    PlayerState player = world.PlayerById[target.OwnerId]; player.ElementMask = 0;
                    foreach (UnitState member in player.Legion)
                        if (member.Health > 0) player.ElementMask |= 1UL << member.Element;
                }
            }
            return actual;
        }

        internal static void ApplyStatus(WorldState world, UnitState target, StatusKind kind, UnitState source, double duration, float amount = 0, float dps = 0)
        {
            if (target.Health <= 0) return;
            ref TimedStatus status = ref target.Statuses[(int)kind];
            bool active = status.Expires > world.Time;
            bool replaceSource = !active || dps >= status.Dps;
            status.Amount = active ? Math.Min(status.Amount, amount) : amount;
            status.Dps = active ? Math.Max(status.Dps, dps) : dps;
            status.Expires = Math.Max(status.Expires, world.Time + duration);
            if (replaceSource)
            {
                status.Owner = source.OwnerId; status.Source = source.Id; status.Element = source.Element;
                // 피해 귀속과 별개로 부여 당시의 군단 소속을 보존해 합류 후 소급 발동을 막는다.
                status.LegionOwner = source.OwnerId >= 0 && !source.Garrison ? source.OwnerId : -1;
            }
        }

        internal static void RefreshMultipliers(UnitState unit, double time)
        {
            TimedStatus chill = unit.Statuses[(int)StatusKind.Chill], freeze = unit.Statuses[(int)StatusKind.Freeze];
            unit.SpeedMultiplier = (1 + unit.Haste) * (chill.Expires > time ? 1 + chill.Amount : 1) * (freeze.Expires > time ? 1 + freeze.Amount : 1);
            unit.RangeMultiplier = (1 + unit.SmokeDebuff) * (1 + unit.SteamDebuff) * (1 + unit.SandDebuff);
        }

        private static void TickDot(WorldState world, UnitState target, StatusKind kind)
        {
            TimedStatus status = target.Statuses[(int)kind];
            double start = world.Time - world.Rules.DeltaTime;
            if (status.Expires <= start || status.Dps <= 0) return;
            double active = Math.Min(world.Time, status.Expires) - start;
            Damage(world, target, (float)(status.Dps * active), status.Owner, status.Source, status.Element);
        }
    }
}
