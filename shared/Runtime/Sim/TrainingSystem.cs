using System;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>양성 접수 때 자원과 인구 슬롯을 예약하고 완료·합류를 틱 경계에서 확정한다.</summary>
    internal static class TrainingSystem
    {
        internal static CommandFailure Start(WorldState world, PlayerState player, int element)
        {
            if (player.NextTrainingOrderId == ulong.MaxValue) return CommandFailure.Busy;
            if (player.TrainingQueue.Count >= player.PopulationCap - player.Population) return CommandFailure.PopulationCap;
            bool nature = world.Rules.Elements[element].Group == ElementGroup.Nature;
            if (!(nature ? player.NatureForge : player.EnergyForge) || !world.Rules.IsUnlocked(player.BaseLevel, nature ? "forge_nature" : "forge_energy")) return CommandFailure.NotUnlocked;
            int cost = world.Rules.TrainingCost(element, player.MainElement);
            if (player.Cores < cost) return CommandFailure.InsufficientCores;
            player.Cores -= cost;
            player.TrainingQueue.Add((player.NextTrainingOrderId++, element, cost));
            if (player.TrainingQueue.Count == 1) BeginNext(world, player);
            return CommandFailure.None;
        }

        internal static CommandFailure CancelWaiting(PlayerState player, ulong orderId)
        {
            // 첫 항목은 양성 중이므로 예약 번호가 일치해도 취소할 수 없다.
            for (int index = 1; index < player.TrainingQueue.Count; index++)
            {
                var order = player.TrainingQueue[index];
                if (order.Id != orderId) continue;
                player.TrainingQueue.RemoveAt(index);
                Refund(player, order.Cost);
                return CommandFailure.None;
            }
            return CommandFailure.TrainingNotWaiting;
        }

        internal static void Tick(WorldState world)
        {
            foreach (PlayerState player in world.Players)
            {
                if (player.GhostEndTick != 0 || player.TrainingElement < 0) continue;
                if (player.TrainingEndTick == 0) BeginNext(world, player);
                if (world.Tick < player.TrainingEndTick) continue;
                int element = player.TrainingElement;
                player.TrainingQueue.RemoveAt(0);
                BeginNext(world, player);
                Vector2 position = ReservePosition(world, player);
                UnitState unit = world.AddUnit(player.Id, element, position, garrison: true);
                world.Metrics.Trained++;
                player.TrainedCount++;
                world.Events.Add(new SimEvent(SimEventKind.Trained, player.Id, 0, unit.Id, position,element:element,entityId:unit.Id));
            }
            float arrivalRadiusSquared = world.Rules.SlotSpacing * world.Rules.SlotSpacing;
            foreach (UnitState unit in world.Units)
            {
                if (!unit.Garrison || unit.OwnerId < 0) continue;
                PlayerState player = world.PlayerById[unit.OwnerId];
                if (player.Rallies(unit) && Vector2.DistanceSquared(player.LegionPosition, unit.Position) <= arrivalRadiusSquared)
                {
                    unit.Garrison = false;
                    player.FormationDirty = true;
                    player.GarrisonArrivals++;
                }
            }
        }

        internal static void Cancel(PlayerState player)
        {
            foreach (var order in player.TrainingQueue) Refund(player, order.Cost);
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
