using System;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>양성 접수 때 자원과 인구 슬롯을 예약하고 완료·합류를 틱 경계에서 확정한다.</summary>
    internal static class TrainingSystem
    {
        internal static CommandFailure Start(WorldState world, PlayerState player, int element, int count, byte targetSquad)
        {
            if (player.NextTrainingOrderId > ulong.MaxValue - (ulong)count) return CommandFailure.Busy;
            if (count > player.PopulationCap - player.Population - player.ReservedPopulation) return CommandFailure.PopulationCap;
            if (targetSquad == 4 && count > world.Rules.GarrisonCap(player.BaseLevel) - WaitingGarrison(world, player) - ReservedFor(player, 4))
                return CommandFailure.PopulationCap;
            bool nature = world.Rules.Elements[element].Group == ElementGroup.Nature;
            if (!(nature ? player.NatureForge : player.EnergyForge) || !world.Rules.IsUnlocked(player.BaseLevel, nature ? "forge_nature" : "forge_energy")) return CommandFailure.NotUnlocked;
            int cost = world.Rules.TrainingCost(element, player.MainElement);
            long totalCost = (long)cost * count;
            if (player.Cores < totalCost) return CommandFailure.InsufficientCores;
            if (!world.TryReserveUnitSlots(count)) return CommandFailure.PopulationCap;
            player.Cores -= totalCost;
            bool idle = player.TrainingQueue.Count == 0;
            for (int index = 0; index < count; index++)
                player.TrainingQueue.Add((player.NextTrainingOrderId++, element, cost, targetSquad));
            if (idle) BeginNext(world, player);
            return CommandFailure.None;
        }

        internal static CommandFailure CancelWaiting(WorldState world, PlayerState player, ulong orderId)
        {
            // 첫 항목은 양성 중이므로 예약 번호가 일치해도 취소할 수 없다.
            for (int index = 1; index < player.TrainingQueue.Count; index++)
            {
                var order = player.TrainingQueue[index];
                if (order.Id != orderId) continue;
                player.TrainingQueue.RemoveAt(index);
                world.ReleaseUnitSlots(1);
                Refund(player, order.Cost);
                return CommandFailure.None;
            }
            return CommandFailure.TrainingNotWaiting;
        }

        internal static void Tick(WorldState world)
        {
            JoinArrivals(world);
            foreach (PlayerState player in world.Players)
            {
                if (player.GhostEndTick != 0 || player.TrainingElement < 0) continue;
                if (player.TrainingEndTick == 0) BeginNext(world, player);
                if (world.Tick < player.TrainingEndTick) continue;
                var order = player.TrainingQueue[0];
                int element = order.Element;
                player.TrainingQueue.RemoveAt(0);
                BeginNext(world, player);
                Vector2 position = ReservePosition(world, player);
                UnitState unit = world.AddUnit(player.Id, element, position, garrison: order.TargetSquad == 4, consumeReservation: true);
                if (order.TargetSquad != 4)
                {
                    if (SquadSystem.JoinedCount(world, player, order.TargetSquad) == 0)
                        SquadSystem.ResetEmpty(world, player, player.Squads[order.TargetSquad - 1]);
                    unit.SquadId = 0; unit.DestinationSquadId = order.TargetSquad; unit.IsReinforcing = true;
                }
                world.Metrics.Trained++;
                player.TrainedCount++;
                world.Events.Add(new SimEvent(SimEventKind.Trained, player.Id, 0, unit.Id, position,element:element,entityId:unit.Id));
            }
        }

        private static void JoinArrivals(WorldState world)
        {
            float arrivalRadiusSquared = world.Rules.SlotSpacing * world.Rules.SlotSpacing;
            foreach (UnitState unit in world.Units)
            {
                if (!unit.IsReinforcing || unit.OwnerId < 0 || unit.Health <= 0) continue;
                PlayerState player = world.PlayerById[unit.OwnerId];
                if (Vector2.DistanceSquared(player.Squads[unit.DestinationSquadId - 1].Position, unit.Position) <= arrivalRadiusSquared)
                {
                    unit.SquadId = unit.DestinationSquadId; unit.DestinationSquadId = 0; unit.IsReinforcing = false; unit.Garrison = unit.SquadId == 4;
                    player.FormationDirty = true;
                    player.GarrisonArrivals++;
                }
            }
        }

        internal static void Cancel(WorldState world, PlayerState player)
        {
            foreach (var order in player.TrainingQueue) Refund(player, order.Cost);
            if (player.TrainingQueue.Count > 0) world.ReleaseUnitSlots(player.TrainingQueue.Count);
            player.TrainingQueue.Clear();
            player.TrainingStartTick = player.TrainingEndTick = 0;
            player.TrainingPausedAtTick = -1;
        }

        internal static void Pause(WorldState world, PlayerState player)
        {
            if (player.TrainingQueue.Count > 0 && player.TrainingPausedAtTick < 0)
                player.TrainingPausedAtTick = world.Tick;
        }

        internal static void Resume(WorldState world, PlayerState player)
        {
            if (player.TrainingPausedAtTick < 0) return;
            long elapsed = world.Tick - player.TrainingPausedAtTick;
            player.TrainingStartTick += elapsed;
            player.TrainingEndTick += elapsed;
            player.TrainingPausedAtTick = -1;
        }

        internal static void LoseForge(WorldState world, PlayerState player, ElementGroup group)
        {
            for (int index = player.TrainingQueue.Count - 1; index >= 0; index--)
            {
                var order = player.TrainingQueue[index];
                if (world.Rules.Elements[order.Element].Group != group) continue;
                // 착수한 원소는 파괴로 소실되고 아직 대기 중인 원소의 비용만 반환한다.
                if (index != 0 || player.TrainingEndTick == 0) Refund(player, order.Cost);
                player.TrainingQueue.RemoveAt(index);
                world.ReleaseUnitSlots(1);
                if (index == 0) player.TrainingStartTick = player.TrainingEndTick = 0;
            }
            if (player.TrainingEndTick == 0)
            {
                player.TrainingPausedAtTick = -1;
                BeginNext(world, player);
                if (player.GhostEndTick != 0) Pause(world, player);
            }
        }

        private static void BeginNext(WorldState world, PlayerState player)
        {
            if (player.TrainingElement < 0)
            {
                player.TrainingStartTick = player.TrainingEndTick = 0;
                player.TrainingPausedAtTick = -1;
                return;
            }
            player.TrainingStartTick = world.Tick;
            player.TrainingEndTick = world.Tick +
                (long)Math.Ceiling(world.Rules.TrainingSeconds(player.TrainingElement, player.MainElement) * world.Rules.TickRate);
        }

        private static void Refund(PlayerState player, int cost) => player.Cores += Math.Min(long.MaxValue - player.Cores, cost);

        internal static int ReservedFor(PlayerState player, byte squad)
        {
            int count = player.CandidateSquadReservations[squad - 1];
            foreach (var order in player.TrainingQueue) if (order.TargetSquad == squad) count++;
            return count;
        }

        internal static int WaitingGarrison(WorldState world, PlayerState player)
        {
            int count = 0;
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId == player.Id && unit.Health > 0 && unit.SquadId == 4 && !unit.IsReinforcing) count++;
            return count;
        }

        private static Vector2 ReservePosition(WorldState world, PlayerState player)
        {
            float spacing = world.Rules.SlotSpacing;
            float distance = world.Rules.BaseRadius + spacing;
            float forward = player.BasePosition.Y + distance <= world.Rules.MapSize ? 1 : -1;
            Vector2 limit = new Vector2(world.Rules.MapSize);
            Vector2 origin = Vector2.Clamp(player.BasePosition + new Vector2(0, forward * distance), Vector2.Zero, limit);
            int columns = (int)Math.Ceiling(Math.Sqrt(player.PopulationCap));
            for (int index = 0; index < player.PopulationCap; index++)
            {
                // 기존 출생점을 먼저 쓰고, 차 있으면 같은 줄의 좌우와 뒤이어 다음 줄을 확인한다.
                int column = index % columns;
                int side = column % 2 == 0 ? -1 : 1;
                Vector2 candidate = origin + new Vector2(side * ((column + 1) / 2), forward * (index / columns)) * spacing;
                if (Vector2.Clamp(candidate, Vector2.Zero, limit) == candidate && IsReserveSpaceFree(world, candidate, player.Id))
                    return candidate;
            }
            return origin;
        }

        private static bool IsReserveSpaceFree(WorldState world, Vector2 position, int ownerId)
        {
            foreach (BuildingState building in world.Buildings)
                if (building.BlocksMovement && Vector2.DistanceSquared(building.Position, position) < building.Radius * building.Radius) return false;
            float distance = world.Rules.MinimumSeparation;
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId == ownerId && unit.Health > 0 &&
                    Vector2.DistanceSquared(unit.Position, position) < distance * distance) return false;
            return true;
        }
    }
}
