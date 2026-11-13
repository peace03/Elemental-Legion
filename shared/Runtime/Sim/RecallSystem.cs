using System;
using System.Collections.Generic;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>본진의 고정 도착 구역을 예약하고 분대 전체의 귀환을 한 번에 적용한다.</summary>
    internal sealed class RecallSystem
    {
        private readonly List<UnitState> members = new List<UnitState>();
        private readonly List<Vector2> arrivals = new List<Vector2>();
        private readonly List<Vector2> extraSlots = new List<Vector2>();
        private readonly HashSet<Vector2> fixedCells = new HashSet<Vector2>();

        internal void Reserve(int capacity)
        {
            members.Capacity = Math.Max(members.Capacity, capacity);
            arrivals.Capacity = Math.Max(arrivals.Capacity, capacity);
        }

        internal static bool IsCasting(WorldState world, UnitState unit) => unit.OwnerId >= 0 && !unit.IsReinforcing &&
            unit.SquadId != 0 && world.PlayerById[unit.OwnerId].RecallSquad == unit.SquadId;

        internal static CommandFailure Start(WorldState world, PlayerState player, SimCommand command)
        {
            if (player.RecallSquad != 0 || player.RecallReadyTick > world.Tick) return CommandFailure.Busy;
            if (player.BaseDestroyed || player.GhostEndTick != 0 || SquadSystem.JoinedCount(world, player, command.TargetSquad) == 0)
                return CommandFailure.RecallInterrupted;
            player.RecallSquad = command.TargetSquad;
            player.RecallSequence = command.Sequence;
            player.RecallEndTick = world.Tick + world.Rules.RecallCastTicks;
            SquadState squad = player.Squads[command.TargetSquad - 1];
            squad.Order = SquadOrderKind.Hold; squad.Direction = Vector2.Zero; squad.Destination = squad.Position;
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId == player.Id && IsCasting(world, unit)) { unit.Target = null; unit.BuildingTarget = null; unit.CatchingUp = false; }
            return CommandFailure.None;
        }

        internal static void Cancel(WorldState world, PlayerState player, CommandFailure failure = CommandFailure.RecallInterrupted)
        {
            if (player.RecallSquad == 0) return;
            world.Events.Add(new SimEvent(SimEventKind.CommandRejected, player.Id, 0, 0,
                player.Squads[player.RecallSquad - 1].Position, detail: (int)failure, sequence: player.RecallSequence));
            ClearCast(player);
        }

        private static void ClearCast(PlayerState player)
        {
            player.RecallSquad = 0; player.RecallEndTick = 0; player.RecallSequence = 0;
        }

        internal void CompleteDue(WorldState world)
        {
            bool gridReady = false;
            foreach (PlayerState player in world.Players)
            {
                if (player.RecallSquad == 0 || player.RecallEndTick > world.Tick) continue;
                if (player.BaseDestroyed || player.GhostEndTick != 0) { Cancel(world, player); continue; }
                members.Clear(); arrivals.Clear();
                foreach (UnitState unit in world.Units)
                    if (unit.Health > 0 && IsCasting(world, unit) && unit.OwnerId == player.Id) members.Add(unit);
                if (members.Count == 0) { Cancel(world, player); continue; }
                // 앞선 귀환으로 옮겨진 병력도 다음 플레이어의 점유 검사에 포함한다.
                if (!gridReady) { world.Grid.Rebuild(world.Units); gridReady = true; }
                foreach (Vector2 slot in player.RecallArrivalSlots)
                {
                    if (!LegalSlot(world, slot) || IsOccupied(world, player, slot)) continue;
                    arrivals.Add(slot);
                    if (arrivals.Count == members.Count) break;
                }
                if (members.Count > player.RecallArrivalSlots.Count) FindExtraArrivals(world, player);
                if (arrivals.Count != members.Count) { Cancel(world, player, CommandFailure.InvalidPlacement); continue; }
                members.Sort((left, right) => left.Id.CompareTo(right.Id));
                SquadState squad = player.Squads[player.RecallSquad - 1];
                squad.Position = arrivals[0]; squad.Destination = squad.Position; squad.Heading = Vector2.UnitY;
                squad.Direction = Vector2.Zero; squad.Order = SquadOrderKind.Hold; squad.FormationDirty = false;
                squad.MovementRevision++;
                for (int i = 0; i < members.Count; i++)
                {
                    UnitState unit = members[i];
                    unit.Position = arrivals[i]; unit.Home = arrivals[i]; unit.MoveStart = arrivals[i];
                    unit.Slot = arrivals[i] - squad.Position; unit.Separation = Vector2.Zero; unit.MoveStep = 0;
                    unit.Detour = null; unit.DetourSide = 0; unit.CatchingUp = false;
                    unit.Target = null; unit.BuildingTarget = null; unit.MovementRevision++;
                }
                player.RecallReadyTick = world.Tick + world.Rules.RecallCooldownTicks;
                ClearCast(player);
                gridReady = false;
            }
        }

        private void FindExtraArrivals(WorldState world, PlayerState player)
        {
            Vector2 origin = player.BasePosition;
            float spacing = world.Rules.SlotSpacing;
            extraSlots.Clear();
            fixedCells.Clear();
            foreach (Vector2 slot in player.RecallArrivalSlots) fixedCells.Add(SlotCell(origin, slot, spacing));
            CollectLayoutCandidates(world, origin, extraSlots, null);
            foreach (Vector2 slot in extraSlots)
            {
                if (fixedCells.Contains(SlotCell(origin, slot, spacing)) || IsOccupied(world, player, slot)) continue;
                arrivals.Add(slot);
                if (arrivals.Count == members.Count) break;
            }
        }

        private static Vector2 SlotCell(Vector2 origin, Vector2 slot, float spacing)
        {
            Vector2 offset = (slot - origin) / spacing;
            return new Vector2(MathF.Round(offset.X), MathF.Round(offset.Y));
        }

        private static bool IsOccupied(WorldState world, PlayerState player, Vector2 slot)
        {
            float clearance = world.Rules.MinimumSeparation;
            world.Grid.Query(slot, clearance, world.Query);
            foreach (UnitState occupant in world.Query)
                if (!(occupant.OwnerId == player.Id && IsCasting(world, occupant)) &&
                    Vector2.DistanceSquared(occupant.Position, slot) < clearance * clearance)
                    return true;
            return false;
        }

        internal static bool TryCreateLayout(WorldState world, Vector2 origin, out Vector2[] slots, PlayerSpawn prospective = null)
        {
            var candidates = new List<Vector2>();
            CollectLayoutCandidates(world, origin, candidates, prospective);
            int reserved = world.Rules.RecallReservedSlotCount;
            if (candidates.Count < reserved) { slots = null; return false; }
            slots = candidates.GetRange(0, reserved).ToArray();
            return true;
        }

        private static void CollectLayoutCandidates(WorldState world, Vector2 origin, List<Vector2> candidates, PlayerSpawn prospective)
        {
            float spacing = world.Rules.SlotSpacing;
            float unitRadius = world.Rules.MinimumSeparation * 0.5f;
            float reach = world.Rules.BuildRadius - unitRadius;
            int steps = (int)Math.Floor(reach / spacing);
            for (int x = -steps; x <= steps; x++)
                for (int z = -steps; z <= steps; z++)
                {
                    Vector2 offset = new Vector2(x * spacing, z * spacing);
                    Vector2 point = origin + offset;
                    if (offset.LengthSquared() > reach * reach || Overlaps(point, origin, world.Rules.BaseRadius + unitRadius) || !LegalSlot(world, point)) continue;
                    if (prospective != null && OverlapsSpawn(world, prospective, point, unitRadius)) continue;
                    candidates.Add(point);
                }
            candidates.Sort((left, right) =>
            {
                int distance = Vector2.DistanceSquared(left, origin).CompareTo(Vector2.DistanceSquared(right, origin));
                if (distance != 0) return distance;
                int x = left.X.CompareTo(right.X);
                return x != 0 ? x : left.Y.CompareTo(right.Y);
            });
        }

        internal static bool LegalSlot(WorldState world, Vector2 point)
        {
            float radius = world.Rules.MinimumSeparation * 0.5f;
            if (point.X < radius || point.Y < radius || point.X > world.Rules.MapSize - radius || point.Y > world.Rules.MapSize - radius) return false;
            foreach (BuildingState building in world.Buildings)
                if (building.BlocksMovement && Overlaps(point, building.Position, building.Radius + radius)) return false;
            return true;
        }

        internal static bool ValidLayout(WorldState world, Vector2 origin, Vector2[] slots)
        {
            if (slots == null || slots.Length != world.Rules.RecallReservedSlotCount) return false;
            float reach = world.Rules.BuildRadius - world.Rules.MinimumSeparation * 0.5f;
            float spacing = world.Rules.SlotSpacing;
            for (int i = 0; i < slots.Length; i++)
            {
                Vector2 offset = slots[i] - origin;
                if (!WorldCheckpoint.Finite(offset.X) || !WorldCheckpoint.Finite(offset.Y) ||
                    offset.LengthSquared() > reach * reach || !LegalSlot(world, slots[i]) ||
                    Math.Abs(offset.X / spacing - Math.Round(offset.X / spacing)) > 0.001 ||
                    Math.Abs(offset.Y / spacing - Math.Round(offset.Y / spacing)) > 0.001) return false;
                for (int j = 0; j < i; j++)
                    if (Vector2.DistanceSquared(slots[i], slots[j]) < spacing * spacing - 0.001f) return false;
                if (i == 0) continue;
                float previous = Vector2.DistanceSquared(slots[i - 1], origin), current = offset.LengthSquared();
                if (previous > current || previous == current && (slots[i - 1].X > slots[i].X ||
                    slots[i - 1].X == slots[i].X && slots[i - 1].Y > slots[i].Y)) return false;
            }
            return true;
        }

        internal static bool Reserved(WorldState world, Vector2 position, float buildingRadius)
        {
            float reach = buildingRadius + world.Rules.MinimumSeparation * 0.5f;
            foreach (PlayerState player in world.Players)
            {
                if (player.RecallArrivalSlots == null) continue;
                foreach (Vector2 slot in player.RecallArrivalSlots)
                    if (Overlaps(slot, position, reach)) return true;
            }
            return false;
        }

        internal static Vector2 ForgePosition(WorldState world, PlayerSpawn spawn, float radius, int side)
            => Vector2.Clamp(spawn.BasePosition + new Vector2(side * (world.Rules.BaseRadius + radius + world.Rules.SlotSpacing), 0),
                Vector2.Zero, new Vector2(world.Rules.MapSize));

        private static bool OverlapsSpawn(WorldState world, PlayerSpawn spawn, Vector2 point, float radius)
            => Overlaps(point, spawn.BasePosition, world.Rules.BaseRadius + radius) ||
                spawn.NatureForge && Overlaps(point, ForgePosition(world, spawn, world.Rules.NatureForgeRadius, 1), world.Rules.NatureForgeRadius + radius) ||
                spawn.EnergyForge && Overlaps(point, ForgePosition(world, spawn, world.Rules.EnergyForgeRadius, -1), world.Rules.EnergyForgeRadius + radius);

        private static bool Overlaps(Vector2 left, Vector2 right, float distance) => Vector2.DistanceSquared(left, right) < distance * distance;
    }
}
