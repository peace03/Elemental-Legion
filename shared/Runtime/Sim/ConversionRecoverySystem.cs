using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>현재 생존 원소로 확보·구출을 판정하고 후보가 소유한 예약을 한 번만 정산한다.</summary>
    internal static class ConversionRecoverySystem
    {
        internal static bool CanAccept(WorldState world, PlayerState player, byte squad, bool reserved = false)
        {
            int ownReservation = reserved ? 1 : 0;
            return player.Population + player.ReservedPopulation - ownReservation < player.PopulationCap &&
                (squad != 4 || TrainingSystem.GarrisonSlotsUsed(world, player) - ownReservation < world.Rules.GarrisonCap(player.BaseLevel));
        }

        internal static void Add(WorldState world, ConversionCandidateState candidate)
        {
            if (!world.TryReserveUnitSlots(1)) throw new InvalidOperationException("후보 전역 예약 공간이 없습니다.");
            world.Candidates.Add(candidate);
            if (world.PlayerById.TryGetValue(candidate.SecureOwner, out PlayerState player))
            {
                player.CandidateReservations++;
                if (candidate.SecureSquad >= 1 && candidate.SecureSquad <= 4) player.CandidateSquadReservations[candidate.SecureSquad - 1]++;
            }
        }

        internal static void Tick(WorldState world)
        {
            if (world.Candidates.Count == 0) return;
            world.Grid.Rebuild(world.Units);
            Span<int> joined = stackalloc int[world.Rules.MaxPlayers * 4];
            joined.Clear();
            foreach (UnitState unit in world.Units)
                if (unit.Health > 0 && !unit.Dead && SquadSystem.FormalMember(unit))
                    joined[world.PlayerById[unit.OwnerId].Slot * 4 + unit.SquadId - 1]++;
            int write = 0;
            float radiusSquared = world.Rules.PvpRecoveryRadius * world.Rules.PvpRecoveryRadius;
            for (int read = 0; read < world.Candidates.Count; read++)
            {
                ConversionCandidateState candidate = world.Candidates[read];
                if (candidate.SecureSquad >= 1 && candidate.SecureSquad <= 4 &&
                    (!world.PlayerById.TryGetValue(candidate.SecureOwner, out PlayerState secureOwner) ||
                        joined[secureOwner.Slot * 4 + candidate.SecureSquad - 1] == 0) &&
                    !OriginalSourceIsReinforcing(world, candidate))
                    candidate.SecureAuthorityLost = true;
                if (world.Tick >= candidate.EndTick)
                {
                    Release(world, candidate);
                    int amount = world.Rules.DropAmount(candidate.Element);
                    if (amount > 0)
                    {
                        int id = EconomySystem.DropCore(world, candidate.Position, amount);
                        world.Metrics.Drops++;
                        world.Events.Add(new SimEvent(SimEventKind.CoreDropped, -1, 0, 0, candidate.Position, amount, entityId: id, entityKind: ObservedEntityKind.Core));
                    }
                    continue;
                }
                bool secure = false, rescue = false, secureBlocked = false, rescueBlocked = false;
                byte rescueSquad = 0;
                float closest = float.PositiveInfinity;
                bool secureActive = !candidate.SecureAuthorityLost && Active(world, candidate.SecureOwner), rescueActive = Active(world, candidate.PreviousOwner) && !candidate.RescuedOnce;
                world.Grid.Query(candidate.Position, world.Rules.PvpRecoveryRadius, world.Query);
                foreach (UnitState unit in world.Query)
                {
                    if (unit.OwnerId < 0 || unit.Health <= 0 || unit.Dead) continue;
                    float distance = Vector2.DistanceSquared(unit.Position, candidate.Position);
                    if (distance > radiusSquared) continue;
                    if (unit.OwnerId != candidate.SecureOwner) secureBlocked = true;
                    if (unit.OwnerId != candidate.PreviousOwner) rescueBlocked = true;
                    if (secureActive && unit.OwnerId == candidate.SecureOwner && SquadSystem.FormalMember(unit) && unit.SquadId == candidate.SecureSquad) secure = true;
                    if (rescueActive && unit.OwnerId == candidate.PreviousOwner && SquadSystem.FormalMember(unit) &&
                        (distance < closest || distance == closest && unit.SquadId < rescueSquad))
                    { rescue = true; closest = distance; rescueSquad = unit.SquadId; }
                }
                if (!secure) candidate.SecureProgress = 0;
                if (!rescue) candidate.RescueProgress = 0;
                if (world.Tick != candidate.CreatedTick)
                {
                    if (secure && !secureBlocked) candidate.SecureProgress = Math.Min(candidate.SecureProgress + 1, (int)world.Rules.PvpRecoverySecureTicks);
                    if (rescue && !rescueBlocked) candidate.RescueProgress = Math.Min(candidate.RescueProgress + 1, (int)world.Rules.PvpRecoverySecureTicks);
                }
                bool secured = secure && !secureBlocked && candidate.SecureProgress >= world.Rules.PvpRecoverySecureTicks;
                bool rescued = rescue && !rescueBlocked && candidate.RescueProgress >= world.Rules.PvpRecoverySecureTicks;
                int owner = secured ? candidate.SecureOwner : candidate.PreviousOwner;
                byte squad = secured ? candidate.SecureSquad : rescueSquad;
                if ((secured || rescued) && world.PlayerById.TryGetValue(owner, out PlayerState winner) && CanAccept(world, winner, squad, secured))
                {
                    Release(world, candidate);
                    var unit = new UnitState(candidate.Id, owner, candidate.Element, candidate.Position,
                        world.Rules.Elements[candidate.Element].Hp * (secured ? world.Rules.PvpRecoveryConvertedHealthRatio : world.Rules.PvpRecoveryRescuedHealthRatio),
                        candidate.Converted || secured, squad == 4, candidate.WasNeutral, world.Rules.Elements.Count)
                    {
                        SquadId = squad, RescuedOnce = candidate.RescuedOnce || rescued, Home = candidate.Position,
                        Role = world.Rules.Elements[candidate.Element].Role, MovementRevision = unchecked(candidate.MovementRevision + 1)
                    };
                    world.InsertUnit(unit);
                    if (secured)
                    {
                        if (winner.ConvertedCount < long.MaxValue - winner.TrainedCount) winner.ConvertedCount++;
                        world.Metrics.Conversions++;
                    }
                    winner.JourneyTotal = winner.JourneyTotal.Add(converted: secured ? 1 : 0, rescued: rescued ? 1 : 0);
                    world.Events.Add(new SimEvent(secured ? SimEventKind.Converted : SimEventKind.Rescued, owner,
                        secured ? candidate.Source : 0, unit.Id, unit.Position, unit.Health, element: unit.Element, entityId: unit.Id));
                    continue;
                }
                world.Candidates[write++] = candidate;
            }
            if (write < world.Candidates.Count) world.Candidates.RemoveRange(write, world.Candidates.Count - write);
            world.Units.Sort((left, right) => left.Id.CompareTo(right.Id));
        }

        private static bool Active(WorldState world, int owner) => world.PlayerById.TryGetValue(owner, out PlayerState player) && player.GhostEndTick == 0 && !player.BaseDestroyed;

        private static bool OriginalSourceIsReinforcing(WorldState world, ConversionCandidateState candidate) =>
            world.UnitById.TryGetValue(candidate.Source, out UnitState source) && source.Health > 0 && !source.Dead &&
            source.OwnerId == candidate.SecureOwner && source.IsReinforcing && source.DestinationSquadId == candidate.SecureSquad;

        private static void Release(WorldState world, ConversionCandidateState candidate)
        {
            world.ReleaseUnitSlots(1);
            if (!world.PlayerById.TryGetValue(candidate.SecureOwner, out PlayerState player)) return;
            player.CandidateReservations--;
            if (candidate.SecureSquad >= 1 && candidate.SecureSquad <= 4) player.CandidateSquadReservations[candidate.SecureSquad - 1]--;
        }
    }
}
