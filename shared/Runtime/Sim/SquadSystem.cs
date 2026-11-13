using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>인증된 소유자의 분대 명령과 전송을 처리한다.</summary>
    internal static class SquadSystem
    {
        internal static bool ValidIntent(WorldState world, SimCommand command)
        {
            switch (command.Kind)
            {
                case CommandKind.MoveDir:
                case CommandKind.MoveTo:
                    return command.SquadMask > 0 && command.SquadMask <= 15 &&
                        WorldCheckpoint.Finite(command.Direction.X) && WorldCheckpoint.Finite(command.Direction.Y) &&
                        (command.Kind != CommandKind.MoveTo || command.Direction.X >= 0 && command.Direction.Y >= 0 &&
                            command.Direction.X <= world.Rules.MapSize && command.Direction.Y <= world.Rules.MapSize);
                case CommandKind.Transfer:
                    return ValidTransferPair(command.FromSquad, command.TargetSquad) &&
                        command.Count > 0 && command.Count <= world.Rules.PopulationCap(world.Rules.MaxBaseLevel);
                case CommandKind.QuickTransfer:
                    return ValidTransferPair(command.FromSquad, command.TargetSquad);
                case CommandKind.Rally:
                    return command.TargetSquad >= 1 && command.TargetSquad <= 3 && command.Count >= 0 && command.Count <= world.Rules.PopulationCap(world.Rules.MaxBaseLevel);
                case CommandKind.Focus: return command.TargetSquad >= 1 && command.TargetSquad <= 4;
                case CommandKind.Recall: return command.TargetSquad >= 1 && command.TargetSquad <= 3;
                case CommandKind.Train:
                    return command.Count >= 1 && command.Count <= world.Rules.MaxTrainingBatchCount && command.TargetSquad >= 1 && command.TargetSquad <= 4;
                default: return true;
            }
        }

        internal static bool ValidTransferPair(byte from, byte to) => from >= 1 && from <= 4 && to >= 1 && to <= 4 && from != to;

        internal static CommandFailure Apply(WorldState world, PlayerState player, SimCommand command, out int dispatched)
        {
            dispatched = 0;
            if (!ValidIntent(world, command)) return CommandFailure.InvalidPlacement;
            if (command.Kind == CommandKind.Focus) { player.FocusAnchor = command.TargetSquad; return CommandFailure.None; }
            bool quick = command.Kind == CommandKind.QuickTransfer;
            if (quick || command.Kind == CommandKind.Transfer || command.Kind == CommandKind.Rally)
            {
                byte from = command.Kind == CommandKind.Rally ? (byte)4 : command.FromSquad;
                byte to = command.TargetSquad;
                bool returning = command.Kind == CommandKind.Transfer && to == 4;
                bool canLeaveFromAnywhere = returning || quick;
                int joined = 0;
                int reinforcing = 0;
                float radiusSquared = world.Rules.GarrisonRadius * world.Rules.GarrisonRadius;
                foreach (UnitState unit in world.Units)
                {
                    if (unit.OwnerId != player.Id || unit.Health <= 0) continue;
                    if (unit.IsReinforcing)
                    {
                        if (quick && unit.DestinationSquadId == from) reinforcing++;
                    }
                    else if (unit.SquadId == from &&
                        (canLeaveFromAnywhere || Vector2.DistanceSquared(unit.Position, player.BasePosition) <= radiusSquared)) joined++;
                }
                int available = joined + reinforcing;
                int count = quick ? Math.Min(world.Rules.QuickTransferCount, available) :
                    command.Kind == CommandKind.Rally && command.Count == 0 ? available : command.Count;
                if (quick && count == 0 || count > available) return CommandFailure.Busy;
                if (to == 4 && count + TrainingSystem.GarrisonSlotsUsed(world, player) > world.Rules.GarrisonCap(player.BaseLevel)) return CommandFailure.PopulationCap;
                if (canLeaveFromAnywhere && count > reinforcing && player.RecallSquad == from) RecallSystem.Cancel(world, player);
                // 생성·복원·전향은 Units를 ID 오름차순으로 유지한다.
                if (reinforcing > 0)
                    foreach (UnitState unit in world.Units)
                    {
                        if (dispatched == count) break;
                        if (unit.OwnerId != player.Id || unit.Health <= 0 || !unit.IsReinforcing || unit.DestinationSquadId != from) continue;
                        SendToSquad(unit, from, to);
                        dispatched++;
                        if (--reinforcing == 0) break;
                    }
                foreach (UnitState unit in world.Units)
                {
                    if (dispatched == count) break;
                    if (unit.OwnerId != player.Id || unit.Health <= 0 || unit.IsReinforcing || unit.SquadId != from ||
                        (!canLeaveFromAnywhere && Vector2.DistanceSquared(unit.Position, player.BasePosition) > radiusSquared)) continue;
                    dispatched++;
                    if (quick || command.Kind == CommandKind.Rally || returning && Vector2.DistanceSquared(unit.Position, player.BasePosition) > radiusSquared)
                        SendToSquad(unit, from, to);
                    else
                    {
                        unit.SquadId = to; unit.DestinationSquadId = 0; unit.Garrison = to == 4;
                        unit.CatchingUp = false; unit.Target = null; unit.BuildingTarget = null;
                    }
                }
                if (quick) player.Squads[from - 1].FormationDirty = true;
                else player.FormationDirty = true;
                return CommandFailure.None;
            }
            Vector2 direction = command.Direction;
            if (player.RecallSquad != 0 && (command.SquadMask & (1 << (player.RecallSquad - 1))) != 0 &&
                (command.Kind == CommandKind.MoveTo || direction != Vector2.Zero)) RecallSystem.Cancel(world, player);
            if (command.Kind == CommandKind.MoveDir)
            {
                float scale = Math.Max(Math.Abs(direction.X), Math.Abs(direction.Y));
                if (scale > 0) direction = Vector2.Normalize(direction / scale);
            }
            foreach (SquadState squad in player.Squads)
            {
                if ((command.SquadMask & (1 << (squad.Id - 1))) == 0) continue;
                squad.AppliedSequence = command.Sequence;
                if (squad.Order == SquadOrderKind.Hold && (command.Kind == CommandKind.MoveTo || direction != Vector2.Zero))
                    squad.FormationDirty = true;
                squad.Direction = command.Kind == CommandKind.MoveDir ? direction : Vector2.Zero;
                squad.Destination = command.Kind == CommandKind.MoveTo ?
                    squad.Id == 4 ? ClampGuardPosition(world, player, command.Direction) : command.Direction : squad.Position;
                squad.Order = command.Kind == CommandKind.MoveTo ? SquadOrderKind.MoveTo : direction == Vector2.Zero ? SquadOrderKind.Hold : SquadOrderKind.MoveDir;
                squad.FollowFormation = squad.Id == 4 && squad.Order != SquadOrderKind.Hold;
            }
            return CommandFailure.None;
        }

        private static void SendToSquad(UnitState unit, byte from, byte to)
        {
            unit.SquadId = 0;
            unit.DestinationSquadId = to;
            unit.IsReinforcing = true;
            unit.IsSquadTransfer = true;
            unit.Garrison = from == 4 || to == 4;
            unit.CatchingUp = false;
            unit.Target = null;
            unit.BuildingTarget = null;
            unit.Detour = null;
            unit.DetourSide = 0;
        }

        internal static int JoinedCount(WorldState world, PlayerState player, byte id)
        {
            int count = 0;
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId == player.Id && unit.Health > 0 && !unit.IsReinforcing && unit.SquadId == id) count++;
            return count;
        }

        /// <summary>호출 단계의 생존 인원을 슬롯별로 센다. 증원 버퍼가 비었으면 합류 인원만 센다.</summary>
        internal static void CountMembers(WorldState world, Span<int> joined, Span<int> reinforcing)
        {
            joined.Clear(); reinforcing.Clear();
            foreach (UnitState unit in world.Units)
            {
                if (unit.OwnerId < 0 || !(unit.Health > 0)) continue;
                int offset = world.PlayerById[unit.OwnerId].Slot * 4;
                if (!unit.IsReinforcing)
                {
                    if (unit.SquadId >= 1 && unit.SquadId <= 4) joined[offset + unit.SquadId - 1]++;
                }
                else if (!reinforcing.IsEmpty && unit.DestinationSquadId >= 1 && unit.DestinationSquadId <= 4)
                    reinforcing[offset + unit.DestinationSquadId - 1]++;
            }
        }

        internal static bool FieldMember(UnitState unit) => unit.OwnerId >= 0 && !unit.IsReinforcing && unit.SquadId >= 1 && unit.SquadId <= 3;
        internal static bool FormalMember(UnitState unit) => unit.OwnerId >= 0 && !unit.IsReinforcing && unit.SquadId >= 1 && unit.SquadId <= 4;
        internal static byte DamageSquad(UnitState unit) => FormalMember(unit) ? unit.SquadId :
            unit.OwnerId >= 0 && unit.IsReinforcing && unit.DestinationSquadId >= 1 && unit.DestinationSquadId <= 4 ? unit.DestinationSquadId : (byte)0;

        internal static Vector2 ClampGuardPosition(WorldState world, PlayerState player, Vector2 point)
        {
            point = Vector2.Clamp(point, Vector2.Zero, new Vector2(world.Rules.MapSize));
            Vector2 offset = point - player.BasePosition;
            return offset.LengthSquared() > world.Rules.GarrisonRadius * world.Rules.GarrisonRadius
                ? player.BasePosition + Vector2.Normalize(offset) * world.Rules.GarrisonRadius : point;
        }

        /// <summary>새 플레이어의 빈 분대만 본진 옆에 배치한다. 재접속과 증원은 기존 중심을 유지한다.</summary>
        internal static void InitializeCenters(WorldState world, PlayerState player)
        {
            Vector2 departure = Departure(world, player);
            foreach (SquadState squad in player.Squads)
            {
                if (squad.Id == 1 && (JoinedCount(world, player, 1) > 0 || IsFreeCenter(world, squad.Position))) continue;
                squad.Position = squad.Destination = departure;
            }
        }

        private static bool DepartureIsValid(WorldState world, PlayerState player, Vector2 point)
            => Vector2.DistanceSquared(point, player.BasePosition) <= world.Rules.GarrisonRadius * world.Rules.GarrisonRadius && IsFreeCenter(world, point);

        private static bool IsFreeCenter(WorldState world, Vector2 point)
        {
            if (point.X < 0 || point.Y < 0 || point.X > world.Rules.MapSize || point.Y > world.Rules.MapSize) return false;
            foreach (BuildingState building in world.Buildings)
                if (building.BlocksMovement && Vector2.DistanceSquared(point, building.Position) <= building.Radius * building.Radius) return false;
            return true;
        }

        internal static Vector2 FocusPosition(WorldState world, PlayerState player)
            => FocusPosition(player, player.FocusAnchor == 4 ? 0 : JoinedCount(world, player, player.FocusAnchor));

        internal static Vector2 FocusPosition(PlayerState player, int joinedCount)
        {
            if (player.FocusAnchor == 4 || joinedCount == 0) return player.BasePosition;
            return player.Squads[player.FocusAnchor - 1].Position;
        }

        internal static Vector2 Departure(WorldState world, PlayerState player)
        {
            float distance = world.Rules.BaseRadius + world.Rules.SlotSpacing;
            int rings = Math.Max(1, (int)Math.Ceiling((world.Rules.GarrisonRadius - distance) / world.Rules.SlotSpacing));
            for (int ring = 0; ring <= rings; ring++)
                for (int index = 0; index < 16; index++)
                {
                    double angle = index * Math.PI / 8;
                    Vector2 point = player.BasePosition + new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle)) * (distance + ring * world.Rules.SlotSpacing);
                    if (point.X < 0 || point.Y < 0 || point.X > world.Rules.MapSize || point.Y > world.Rules.MapSize) continue;
                    if (DepartureIsValid(world, player, point)) return point;
                }
            // 기지 주위가 모두 막히면 기존 합법적인 군단 위치를 보존한다.
            return player.LegionPosition;
        }
    }
}
