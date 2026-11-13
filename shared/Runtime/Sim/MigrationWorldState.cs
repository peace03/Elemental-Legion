using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;
using Newtonsoft.Json;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>디스크 복원 정책을 적용하지 않고 실행 중인 월드와 접수 명령을 이어 가는 승계 상태다.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class MigrationWorldState
    {
        [JsonProperty(Required = Required.Always)] public WorldCheckpoint World { get; private set; }
        [JsonProperty(Required = Required.Always)] private PlayerRuntime[] Players;
        [JsonProperty(Required = Required.Always)] private UnitRuntime[] Units;
        [JsonProperty(Required = Required.Always)] private PendingCommand[] Commands;
        [JsonProperty(Required = Required.Always)] private StatefulRandomState Random;
        [JsonProperty(Required = Required.Always)] private MetricState Metrics;
        [JsonProperty(Required = Required.Always)] private bool ProtectionEnabled;
        [JsonProperty(Required = Required.Always)] private bool CameraVisibility;
        public long Tick => World.Tick;
        public string DataHash => World.DataHash;
        public int RoomPlayers => World.RoomPlayers;
        public int MaxPlayerId => World.MaxPlayerId;

        [JsonConstructor]
        private MigrationWorldState() { }

        internal static MigrationWorldState Capture(WorldState state, WorldEcology ecology)
        {
            var result = new MigrationWorldState
            {
                World = WorldCheckpoint.Capture(state, ecology), Random = state.Random.CaptureState(),
                ProtectionEnabled = state.ProtectionEnabled, CameraVisibility = state.CameraVisibility,
                Players = new PlayerRuntime[state.Players.Count],
                Units = new UnitRuntime[state.Units.Count], Commands = new PendingCommand[state.Commands.Count],
                Metrics = MetricState.Capture(state.Metrics)
            };
            for (int index = 0; index < result.Players.Length; index++)
            {
                PlayerState player = state.Players[index];
                result.Players[index] = new PlayerRuntime
                {
                    Id = player.Id, Direction = player.Direction, LastSequence = player.LastSequence,
                    AppliedSequence = player.AppliedSequence, RateWindow = player.RateWindow,
                    CommandsInWindow = player.CommandsInWindow, MovementCommandsInWindow = player.MovementCommandsInWindow,
                    ProtectionEndTick = player.ProtectionEndTick,
                    TrainingStartTick = player.TrainingStartTick, TrainingEndTick = player.TrainingEndTick,
                    TrainingPausedAtTick = player.TrainingPausedAtTick, NextTrainingOrderId = player.NextTrainingOrderId,
                    RallyThroughUnitId = player.RallyThroughUnitId, FormationDirty = player.FormationDirty,
                    ElementMask = player.ElementMask, BaseDestroyed = player.BaseDestroyed
                };
            }
            for (int index = 0; index < result.Units.Length; index++)
            {
                UnitState unit = state.Units[index];
                result.Units[index] = new UnitRuntime
                {
                    Id = unit.Id, Slot = unit.Slot, MoveStart = unit.MoveStart, Separation = unit.Separation,
                    MoveStep = unit.MoveStep, AreaDamage = unit.AreaDamage, AreaOwner = unit.AreaOwner,
                    AreaSource = unit.AreaSource, AreaElement = unit.AreaElement, AreaSquad = unit.AreaSquad, SmokeOwners = unit.SmokeOwners,
                    SmokeOwners2 = unit.SmokeOwners2, SmokeOwners3 = unit.SmokeOwners3,
                    Ghost = unit.Ghost, Protected = unit.Protected, Dead = unit.Dead,
                    ImpactTick = unit.ImpactTick, ImpactIndex = unit.ImpactIndex
                };
            }
            for (int index = 0; index < result.Commands.Length; index++)
            {
                QueuedCommand pending = state.Commands[index];
                SimCommand command = pending.Command;
                result.Commands[index] = new PendingCommand
                {
                    Player = pending.Player, Kind = command.Kind, Sequence = command.Sequence, Direction = command.Direction,
                    Element = command.Element, BuildingKind = command.BuildingKind, CellX = command.CellX,
                    CellZ = command.CellZ, TowerElement = command.TowerElement, TrainingOrderId = command.TrainingOrderId,
                    SquadMask = command.SquadMask, FromSquad = command.FromSquad, TargetSquad = command.TargetSquad, Count = command.Count,
                    BuildingId = command.BuildingId, UpgradeBranch = command.UpgradeBranch, TargetRank = command.TargetRank
                };
            }
            return result;
        }

        internal WorldState Restore(GameRules rules, bool collectTimings)
        {
            WorldCheckpoint.Check(World != null, "승계 월드");
            WorldState state = World.Restore(rules, collectTimings);
            WorldCheckpoint.Check(Players != null && Players.Length == state.Players.Count &&
                Units != null && Units.Length == state.Units.Count && Commands != null &&
                Commands.Length <= (long)state.Rules.MaxPlayers * state.Rules.CommandRateLimit, "승계 배열");
            state.Random.RestoreState(Random);
            state.ProtectionEnabled = ProtectionEnabled;
            state.CameraVisibility = CameraVisibility;
            RestorePlayers(state);
            RestoreUnits(state);
            RestoreCommands(state);
            WorldCheckpoint.Check(Metrics != null, "승계 통계");
            Metrics.Restore(state);
            return state;
        }

        private void RestorePlayers(WorldState state)
        {
            GameRules rules = state.Rules;
            for (int index = 0; index < Players.Length; index++)
            {
                PlayerRuntime saved = Players[index];
                PlayerState player = state.Players[index];
                WorldCheckpoint.Check(saved != null && saved.Id == player.Id && Finite(saved.Direction) &&
                    saved.Direction.LengthSquared() <= 1.001f && saved.AppliedSequence <= saved.LastSequence &&
                    saved.RateWindow >= -1 && saved.RateWindow <= state.Tick / rules.TickRate &&
                    saved.CommandsInWindow >= 0 && saved.CommandsInWindow <= rules.CommandRateLimit &&
                    saved.MovementCommandsInWindow >= 0 && saved.MovementCommandsInWindow <= rules.CommandRateLimit &&
                    player.RecallSequence <= saved.LastSequence &&
                    saved.ProtectionEndTick >= 0 &&
                    saved.ProtectionEndTick <= state.Tick + (long)Math.Ceiling(rules.SpawnProtectionSeconds * rules.TickRate) &&
                    saved.RallyThroughUnitId >= 0 && saved.RallyThroughUnitId < state.NextId &&
                    (saved.ElementMask >> rules.Elements.Count) == 0 && !saved.BaseDestroyed, "승계 플레이어");
                WorldCheckpoint.Check(saved.TrainingStartTick == player.TrainingStartTick && saved.TrainingEndTick == player.TrainingEndTick &&
                    saved.TrainingPausedAtTick == player.TrainingPausedAtTick && saved.NextTrainingOrderId == player.NextTrainingOrderId,
                    "승계 양성 타이머와 체크포인트 일치");
                WorldCheckpoint.Check(player.GhostEndTick == 0 || saved.Direction == Vector2.Zero, "유령 이동");
                foreach (SquadState squad in player.Squads)
                    WorldCheckpoint.Check(squad.AppliedSequence <= saved.LastSequence &&
                        (player.GhostEndTick == 0 || squad.Direction == Vector2.Zero && !squad.FollowFormation), "승계 분대 순번과 유령 이동");
                WorldCheckpoint.Check(saved.Direction == player.Direction && saved.AppliedSequence == player.AppliedSequence && saved.ElementMask == player.ElementMask, "승계 1번 분대 일치");
                player.Direction = saved.Direction;
                player.LastSequence = saved.LastSequence;
                player.AppliedSequence = saved.AppliedSequence;
                player.RateWindow = saved.RateWindow;
                player.CommandsInWindow = saved.CommandsInWindow;
                player.MovementCommandsInWindow = saved.MovementCommandsInWindow;
                player.ProtectionEndTick = saved.ProtectionEndTick;
                player.TrainingStartTick = saved.TrainingStartTick;
                player.TrainingEndTick = saved.TrainingEndTick;
                player.RallyThroughUnitId = saved.RallyThroughUnitId;
                WorldCheckpoint.Check(player.FormationDirty == saved.FormationDirty, "승계 진형 상태 일치");
                player.ElementMask = saved.ElementMask;
                player.BaseDestroyed = saved.BaseDestroyed;
            }
        }

        private void RestoreUnits(WorldState state)
        {
            for (int index = 0; index < Units.Length; index++)
            {
                UnitRuntime saved = Units[index];
                UnitState unit = state.Units[index];
                WorldCheckpoint.Check(saved != null && saved.Id == unit.Id && Finite(saved.Slot) &&
                    Finite(saved.MoveStart) && Finite(saved.Separation) && WorldCheckpoint.Finite(saved.MoveStep) && saved.MoveStep >= 0 &&
                    WorldCheckpoint.Finite(saved.AreaDamage) && saved.AreaDamage >= 0 && saved.AreaOwner >= -1 &&
                    saved.AreaSource >= 0 && saved.AreaSource < state.NextId &&
                    saved.AreaElement >= 0 && saved.AreaElement < state.Rules.Elements.Count &&
                    (saved.SmokeOwners >> state.Rules.MaxPlayers) == 0 && (saved.SmokeOwners2 >> state.Rules.MaxPlayers) == 0 &&
                    (saved.SmokeOwners3 >> state.Rules.MaxPlayers) == 0 && saved.AreaSquad <= 3 && !saved.Dead &&
                    saved.ImpactTick >= -1 && saved.ImpactTick <= state.Tick && saved.ImpactIndex >= -1 &&
                    saved.Ghost == (unit.OwnerId >= 0 && state.PlayerById[unit.OwnerId].GhostEndTick != 0), "승계 유닛");
                unit.Slot = saved.Slot;
                unit.MoveStart = saved.MoveStart;
                unit.Separation = saved.Separation;
                unit.MoveStep = saved.MoveStep;
                unit.AreaDamage = saved.AreaDamage;
                unit.AreaOwner = saved.AreaOwner;
                unit.AreaSource = saved.AreaSource;
                unit.AreaElement = saved.AreaElement;
                unit.SmokeOwners = saved.SmokeOwners; unit.SmokeOwners2 = saved.SmokeOwners2; unit.SmokeOwners3 = saved.SmokeOwners3; unit.AreaSquad = saved.AreaSquad;
                unit.Ghost = saved.Ghost;
                unit.Protected = saved.Protected;
                unit.Dead = saved.Dead;
                unit.ImpactTick = saved.ImpactTick;
                unit.ImpactIndex = saved.ImpactIndex;
            }
        }

        private void RestoreCommands(WorldState state)
        {
            var previous = new Dictionary<int, ulong>();
            foreach (PendingCommand saved in Commands)
            {
                WorldCheckpoint.Check(saved != null && state.PlayerById.TryGetValue(saved.Player, out _), "승계 명령 소유자");
                PlayerState player = state.PlayerById[saved.Player];
                previous.TryGetValue(saved.Player, out ulong last);
                WorldCheckpoint.Check(player.GhostEndTick == 0 && saved.Sequence > last && saved.Sequence <= player.LastSequence &&
                    saved.Kind >= CommandKind.MoveDir && saved.Kind <= CommandKind.UpgradeFacility, "승계 명령 순번");
                SimCommand command = saved.Command();
                WorldCheckpoint.Check(SquadSystem.ValidIntent(state, command), "승계 분대 명령");
                WorldCheckpoint.Check(saved.Kind != CommandKind.MoveDir || Finite(saved.Direction), "승계 이동 명령");
                WorldCheckpoint.Check(saved.Kind != CommandKind.Train || state.Rules.TryElementIndex(saved.Element, out _), "승계 양성 명령");
                WorldCheckpoint.Check(saved.Kind != CommandKind.Build || ConstructionSystem.ValidIntent(state.Rules, command), "승계 건설 명령");
                WorldCheckpoint.Check(saved.Kind == CommandKind.UpgradeFacility ? ConstructionSystem.ValidUpgradeIntent(command) :
                    saved.BuildingId == 0 && saved.UpgradeBranch == FacilityUpgradeBranch.None && saved.TargetRank == 0, "승계 시설 업그레이드 명령");
                WorldCheckpoint.Check(saved.Kind != CommandKind.CancelTraining ||
                    saved.TrainingOrderId > 0, "승계 취소 명령");
                WorldCheckpoint.Check(saved.Kind != CommandKind.QuickTransfer || saved.Count == 0, "승계 빠른 전송 수량");
                state.Commands.Add(new QueuedCommand(saved.Player, command));
                previous[saved.Player] = saved.Sequence;
            }
        }

        private static bool Finite(Vector2 point) => WorldCheckpoint.Finite(point.X) && WorldCheckpoint.Finite(point.Y);

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class PlayerRuntime
        {
            public int Id, CommandsInWindow, RallyThroughUnitId;
            [JsonProperty(Required = Required.DisallowNull)] public int MovementCommandsInWindow;
            public Vector2 Direction;
            public ulong LastSequence, AppliedSequence, ElementMask, NextTrainingOrderId;
            public long RateWindow, ProtectionEndTick, TrainingStartTick, TrainingEndTick, TrainingPausedAtTick;
            public bool FormationDirty, BaseDestroyed;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class UnitRuntime
        {
            public int Id, AreaOwner, AreaSource, AreaElement, ImpactIndex;
            public long ImpactTick;
            public Vector2 Slot, MoveStart, Separation;
            public float MoveStep, AreaDamage;
            public ulong SmokeOwners, SmokeOwners2, SmokeOwners3;
            public byte AreaSquad;
            public bool Ghost, Protected, Dead;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class PendingCommand
        {
            public int Player, CellX, CellZ, TowerElement, Count, BuildingId;
            public byte SquadMask, FromSquad, TargetSquad, TargetRank;
            public CommandKind Kind;
            public BuildingKind BuildingKind;
            public FacilityUpgradeBranch UpgradeBranch;
            public ulong Sequence, TrainingOrderId;
            public Vector2 Direction;
            [JsonProperty(Required = Required.AllowNull)] public string Element;

            internal SimCommand Command()
            {
                switch (Kind)
                {
                    case CommandKind.MoveDir: return SimCommand.Move(Sequence, SquadMask, Direction);
                    case CommandKind.MoveTo: return SimCommand.MoveTo(Sequence, SquadMask, Direction);
                    case CommandKind.Transfer: return SimCommand.Transfer(Sequence, FromSquad, TargetSquad, Count);
                    case CommandKind.QuickTransfer: return SimCommand.QuickTransfer(Sequence, FromSquad, TargetSquad);
                    case CommandKind.Focus: return SimCommand.Focus(Sequence, TargetSquad);
                    case CommandKind.Recall: return SimCommand.Recall(Sequence, TargetSquad);
                    case CommandKind.AcknowledgeJourney: return SimCommand.AcknowledgeJourney(Sequence);
                    case CommandKind.Train: return SimCommand.Train(Sequence, Element, Count, TargetSquad);
                    case CommandKind.Rally: return SimCommand.Rally(Sequence, TargetSquad, Count);
                    case CommandKind.Build: return SimCommand.Build(Sequence, BuildingKind, CellX, CellZ, TowerElement);
                    case CommandKind.UpgradeBase: return SimCommand.UpgradeBase(Sequence);
                    case CommandKind.UpgradeFacility: return SimCommand.UpgradeFacility(Sequence, BuildingId, UpgradeBranch, TargetRank);
                    case CommandKind.CancelTraining: return SimCommand.CancelTraining(Sequence, TrainingOrderId);
                    default: throw new ArgumentException("승계 명령 종류가 잘못됐습니다.");
                }
            }
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class MetricState
        {
            public long Attacks, Deaths, Conversions, Drops, Pickups, Trained;
            [JsonProperty(Required = Required.Default)] public long EcologyRespawns, EcologyCoreSpawns;
            public long[] Stages, Reactions;

            internal static MetricState Capture(WorldMetrics metrics) => new MetricState
            {
                Attacks = metrics.Attacks, Deaths = metrics.Deaths, Conversions = metrics.Conversions,
                Drops = metrics.Drops, Pickups = metrics.Pickups, Trained = metrics.Trained,
                EcologyRespawns = metrics.EcologyRespawns, EcologyCoreSpawns = metrics.EcologyCoreSpawns,
                Stages = (long[])metrics.StageBuffer.Clone(), Reactions = (long[])metrics.ReactionBuffer.Clone()
            };

            internal void Restore(WorldState state)
            {
                WorldMetrics metrics = state.Metrics;
                WorldCheckpoint.Check(Attacks >= 0 && Deaths >= 0 && Conversions >= 0 && Drops >= 0 && Pickups >= 0 && Trained >= 0 &&
                    EcologyRespawns >= 0 && EcologyCoreSpawns >= 0 &&
                    Stages != null && Stages.Length == metrics.StageBuffer.Length &&
                    Reactions != null && Reactions.Length == metrics.ReactionBuffer.Length, "승계 통계");
                foreach (long value in Stages) WorldCheckpoint.Check(value >= 0, "승계 단계 시간");
                foreach (long value in Reactions) WorldCheckpoint.Check(value >= 0, "승계 반응 통계");
                metrics.Attacks = Attacks;
                metrics.Deaths = Deaths;
                metrics.Conversions = Conversions;
                metrics.Drops = Drops;
                metrics.Pickups = Pickups;
                metrics.Trained = Trained;
                metrics.EcologyRespawns = EcologyRespawns;
                metrics.EcologyCoreSpawns = EcologyCoreSpawns;
                Array.Copy(Stages, metrics.StageBuffer, Stages.Length);
                Array.Copy(Reactions, metrics.ReactionBuffer, Reactions.Length);
            }
        }
    }
}
