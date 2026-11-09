using System;
using System.Diagnostics;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>단일 스레드 월드의 수정 진입점이다. I/O 없이 고정 틱과 명령·관측 경계만 제공한다.</summary>
    public sealed class SimulationWorld
    {
        private readonly WorldState state;
        private readonly CombatSystem combat = new CombatSystem();
        private readonly EconomySystem economy = new EconomySystem();
        private WorldEcology ecology;
        public WorldMetrics Metrics => state.Metrics;
        public bool HasGhosts => state.GhostCount > 0;
        public int PlayerCount => state.Players.Count;

        /// <summary>적용이 끝난 틱의 서버 상태를 독립된 복사본으로 보관한다.</summary>
        public WorldCheckpoint CaptureCheckpoint() => WorldCheckpoint.Capture(state, ecology);

        /// <summary>접수 명령과 진행 중인 동작을 포함한 독립된 승계 상태를 만든다.</summary>
        public MigrationWorldState CaptureMigrationState() => MigrationWorldState.Capture(state, ecology);

        /// <summary>로그아웃·환불을 적용하지 않고 저장한 틱의 다음 틱부터 이어 간다.</summary>
        public static SimulationWorld RestoreMigrationState(GameRules rules, MigrationWorldState snapshot,
            bool collectTimings = false, bool cameraVisibility = false)
            => new SimulationWorld(rules, snapshot, collectTimings, cameraVisibility);

        private SimulationWorld(GameRules rules, MigrationWorldState snapshot, bool collectTimings, bool cameraVisibility)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            state = snapshot.Restore(rules, collectTimings);
            state.CameraVisibility |= cameraVisibility;
            var saved = snapshot.World.Ecology;
            ecology = saved == null ? null : new WorldEcology(state, saved.MinBaseDistance, saved);
            ReserveBuffers();
            VisibilitySystem.Refresh(state);
        }

        public static SimulationWorld RestoreCheckpoint(GameRules rules, WorldCheckpoint checkpoint, bool collectTimings = false,
            bool cameraVisibility = false) => new SimulationWorld(rules, checkpoint, collectTimings, cameraVisibility);

        private SimulationWorld(GameRules rules, WorldCheckpoint checkpoint, bool collectTimings, bool cameraVisibility)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            state = checkpoint.Restore(rules.ForRoom(checkpoint.RoomPlayers), collectTimings);
            state.CameraVisibility = cameraVisibility;
            ecology = checkpoint.Ecology == null ? null : new WorldEcology(state, checkpoint.Ecology.MinBaseDistance, checkpoint.Ecology);
            ReserveBuffers();
            foreach (PlayerState player in state.Players) EnterGhost(player, preserveTraining: true);
            VisibilitySystem.Refresh(state);
        }

        public SimulationWorld(GameRules rules, WorldSetup setup, int seed)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (setup.Players.Count > rules.MaxPlayers) throw new ArgumentException("플레이어 수가 상한을 넘었습니다.", nameof(setup));
            state = new WorldState(rules, seed, setup.CollectTimings) { CameraVisibility = setup.CameraVisibility };
            foreach (PlayerSpawn spawn in setup.Players)
            {
                if (spawn == null || spawn.Id < 0 || state.PlayerById.ContainsKey(spawn.Id) || spawn.Cores < 0) throw new ArgumentException("플레이어 ID 또는 자원이 잘못됐습니다.", nameof(setup));
                ValidatePosition(rules, spawn.BasePosition); ValidatePosition(rules, spawn.LegionPosition);
                int element = rules.ElementIndex(spawn.MainElement);
                if (!rules.StartingElements.Contains(element)) throw new ArgumentException("시작 원소는 허용된 자연 원소여야 합니다.", nameof(setup));
                var player = new PlayerState
                {
                    Id = spawn.Id, Nickname = spawn.Nickname, ColorId = ChooseColor(spawn.ColorId), Slot = state.Players.Count, MainElement = element, BaseLevel = spawn.BaseLevel,
                    Vision = new VisionGrid(rules.MapSize, rules.CellSize),
                    BasePosition = spawn.BasePosition, LegionPosition = spawn.LegionPosition, Cores = spawn.Cores,
                    PopulationCap = rules.PopulationCap(spawn.BaseLevel), NatureForge = spawn.NatureForge, EnergyForge = spawn.EnergyForge
                };
                if (player.ColorId < 0) throw new ArgumentException("소유 색이 중복되거나 부족합니다.", nameof(setup));
                state.Players.Add(player); state.PlayerById.Add(player.Id, player);
                state.AddBuilding(player, BuildingKind.Base, player.BasePosition);
                if (spawn.NatureForge) AddForge(state, player, rules.NatureForgeRadius, 1);
                if (spawn.EnergyForge) AddForge(state, player, rules.EnergyForgeRadius, -1);
            }
            state.Players.Sort((left, right) => left.Id.CompareTo(right.Id));
            foreach (UnitSpawn spawn in setup.Units)
            {
                if (spawn == null || spawn.OwnerId < -1 || spawn.OwnerId >= 0 && !state.PlayerById.ContainsKey(spawn.OwnerId) || spawn.OwnerId < 0 && spawn.Garrison) throw new ArgumentException("유닛 소유자가 잘못됐습니다.", nameof(setup));
                ValidatePosition(rules, spawn.Position);
                int element = rules.ElementIndex(spawn.Element);
                if (spawn.Health.HasValue && (!Finite(spawn.Health.Value) || spawn.Health.Value <= 0 || spawn.Health.Value > rules.Elements[element].Hp)) throw new ArgumentException("초기 체력이 잘못됐습니다.", nameof(setup));
                state.AddUnit(spawn.OwnerId, element, spawn.Position, spawn.Converted, spawn.Garrison, spawn.Health);
            }
            foreach (PlayerState player in state.Players)
                if (player.Population > player.PopulationCap) throw new ArgumentException("초기 인구가 상한을 넘었습니다.", nameof(setup));
            foreach (CoreSpawn core in setup.Cores)
            {
                ValidatePosition(rules, core.Position);
                if (core.Amount <= 0) throw new ArgumentException("핵 수량은 양수여야 합니다.", nameof(setup));
                state.Cores.Add(new CoreState { Id = state.NextId++, Position = core.Position, Amount = core.Amount, Expires = rules.CoreLifetime });
            }
            ReserveBuffers();
            VisibilitySystem.Refresh(state);
        }

        /// <summary>수량·위치 결과가 없는 명령을 접수한다. 자원 검증과 실행 결과는 다음 틱의 이벤트로 확인한다.</summary>
        public CommandAdmission TryEnqueue(int playerId, SimCommand command)
        {
            if (!state.PlayerById.TryGetValue(playerId, out PlayerState player) || player.GhostEndTick != 0) return CommandAdmission.UnknownPlayer;
            if (command.Sequence == 0 || command.Kind < CommandKind.MoveDir || command.Kind > CommandKind.CancelTraining ||
                command.Kind == CommandKind.MoveDir && (!Finite(command.Direction.X) || !Finite(command.Direction.Y)) ||
                command.Kind == CommandKind.Build && !ConstructionSystem.ValidIntent(state.Rules, command) ||
                command.Kind == CommandKind.Train && !state.Rules.TryElementIndex(command.Element, out _) ||
                command.Kind == CommandKind.CancelTraining && command.TrainingOrderId == 0)
                return CommandAdmission.InvalidInput;
            if (command.Sequence <= player.LastSequence) return CommandAdmission.StaleSequence;
            long window = state.Tick / state.Rules.TickRate;
            if (player.RateWindow != window) { player.RateWindow = window; player.CommandsInWindow = 0; }
            if (player.CommandsInWindow >= state.Rules.CommandRateLimit) return CommandAdmission.RateLimited;
            if (state.Commands.Count >= (long)state.Rules.MaxPlayers * state.Rules.CommandRateLimit) return CommandAdmission.QueueFull;
            player.CommandsInWindow++; player.LastSequence = command.Sequence;
            state.Commands.Add(new QueuedCommand(playerId, command));
            return CommandAdmission.Accepted;
        }

        /// <summary>JSON의 tickRate에 따른 한 틱이다. 호출자는 같은 월드를 여러 스레드에서 동시에 사용하지 않는다.</summary>
        public void Step()
        {
            state.Events.Clear(); state.Tick++;
            long start = Timestamp();
            ConstructionSystem.CompleteDueBuildings(state);
            ApplyCommands(); MarkStage(0, ref start);
            MovementSystem.MoveLegions(state); MarkStage(1, ref start);
            MovementSystem.MoveUnits(state); MarkStage(2, ref start);
            state.Grid.Rebuild(state.Units); combat.Prepare(state); combat.SelectTargets(state); MarkStage(3, ref start);
            combat.Run(state); MarkStage(4, ref start);
            economy.ResolveDeaths(state); PlayerLifecycle.Resolve(state, economy); MarkStage(5, ref start);
            TrainingSystem.Tick(state); ecology?.Tick(state);
            state.Grid.Rebuild(state.Units); economy.TickCores(state); MarkStage(6, ref start);
            VisibilitySystem.Refresh(state); MarkStage(7, ref start);
        }

        public WorldView GetView(int playerId) => TryGetView(playerId, out WorldView view) ? view : throw new ArgumentException("없는 플레이어입니다.", nameof(playerId));

        public bool TryGetView(int playerId, out WorldView view)
        {
            bool found = state.PlayerById.TryGetValue(playerId, out PlayerState player);
            view = found ? player.View : null;
            return found;
        }

        /// <summary>네트워크 월드의 중립·자원 배치를 한 번만 초기화한다.</summary>
        public void EnableEcology(float minBaseDistance)
        {
            if (ecology != null) throw new InvalidOperationException("월드 생태가 이미 초기화됐습니다.");
            ecology = new WorldEcology(state, minBaseDistance);
            state.ProtectionEnabled = true;
            ReserveBuffers();
            VisibilitySystem.Refresh(state);
        }

        public bool TryChooseSpawn(out Vector2 basePosition, out Vector2 legionPosition)
        {
            basePosition = legionPosition = default;
            return ecology != null && ecology.TryChooseSpawn(out basePosition, out legionPosition);
        }

        /// <summary>고정 시작 지점에서 기존 플레이어와 적 유닛이 충분히 떨어져 있는지 확인한다.</summary>
        public bool IsSafePlayerSpawn(Vector2 basePosition, Vector2 legionPosition, float minBaseDistance) =>
            WorldEcology.IsSafePlayerSpawn(state, basePosition, legionPosition, minBaseDistance);

        /// <summary>서버가 검증한 신규 세션을 현재 월드에 추가한다. 기존 틱·유닛·시야 슬롯은 유지한다.</summary>
        public bool TryAddPlayer(PlayerSpawn spawn)
        {
            if (spawn == null) throw new ArgumentNullException(nameof(spawn));
            if (state.Players.Count >= state.Rules.MaxPlayers || spawn.Id < 0 || state.PlayerById.ContainsKey(spawn.Id)) return false;
            ValidatePosition(state.Rules, spawn.BasePosition); ValidatePosition(state.Rules, spawn.LegionPosition);
            int element = state.Rules.ElementIndex(spawn.MainElement);
            int cap = state.Rules.PopulationCap(spawn.BaseLevel);
            if (!state.Rules.StartingElements.Contains(element) || spawn.Cores < 0 || cap < state.Rules.StartUnitCount) return false;
            int colorId = ChooseColor(spawn.ColorId);
            if (colorId < 0) return false;
            ulong occupied = 0;
            foreach (PlayerState existing in state.Players) occupied |= 1UL << existing.Slot;
            int slot = 0;
            while ((occupied & (1UL << slot)) != 0) slot++;
            var player = new PlayerState
            {
                Id = spawn.Id, Nickname = spawn.Nickname, ColorId = colorId, Slot = slot, MainElement = element, BaseLevel = spawn.BaseLevel,
                Vision = new VisionGrid(state.Rules.MapSize, state.Rules.CellSize),
                BasePosition = spawn.BasePosition, LegionPosition = spawn.LegionPosition, Cores = spawn.Cores,
                PopulationCap = cap, NatureForge = spawn.NatureForge, EnergyForge = spawn.EnergyForge,
                ProtectionEndTick = state.ProtectionEnabled ? state.Tick + (long)Math.Ceiling(state.Rules.SpawnProtectionSeconds * state.Rules.TickRate) : 0
            };
            state.Players.Add(player); state.Players.Sort((left, right) => left.Id.CompareTo(right.Id));
            state.PlayerById.Add(player.Id, player);
            state.AddBuilding(player, BuildingKind.Base, player.BasePosition);
            if (spawn.NatureForge) AddForge(state, player, state.Rules.NatureForgeRadius, 1);
            if (spawn.EnergyForge) AddForge(state, player, state.Rules.EnergyForgeRadius, -1);
            for (int index = 0; index < state.Rules.StartUnitCount; index++)
            {
                var offset = new Vector2((index - (state.Rules.StartUnitCount - 1) * 0.5f) * state.Rules.SlotSpacing, 0);
                state.AddUnit(player.Id, element, Vector2.Clamp(player.LegionPosition + offset, Vector2.Zero, new Vector2(state.Rules.MapSize)));
            }
            ReserveBuffers(); VisibilitySystem.Refresh(state);
            return true;
        }

        /// <summary>유령이 쓰는 색도 유지하며 새 플레이어에게 배정할 수 있는지 확인한다.</summary>
        public bool IsColorAvailable(int colorId) => PlayerPalette.IsValid(colorId) && ChooseColor(colorId) == colorId;

        private int ChooseColor(int requested)
        {
            int used = 0;
            foreach (PlayerState player in state.Players) used |= 1 << player.ColorId;
            if (requested != -1) return PlayerPalette.IsValid(requested) && (used & (1 << requested)) == 0 ? requested : -1;
            for (int color = 0; color < PlayerPalette.Count; color++)
                if ((used & (1 << color)) == 0) return color;
            return -1;
        }

        /// <summary>접속 종료 시 대기·유지 중인 이동을 끊는다. 군단·본진·자원은 월드에 보존한다.</summary>
        public void StopPlayer(int playerId)
        {
            if (!state.PlayerById.TryGetValue(playerId, out PlayerState player)) return;
            state.Commands.RemoveAll(command => command.Player == playerId);
            player.Direction = Vector2.Zero; player.RallyThroughUnitId = 0;
            VisibilitySystem.Refresh(state);
        }

        /// <summary>유령은 기존 자리에서 방어하고 설정된 월드 틱까지 자리를 점유한다.</summary>
        public bool BeginGhost(int playerId)
        {
            if (!state.PlayerById.TryGetValue(playerId, out PlayerState player) || player.GhostEndTick != 0) return false;
            EnterGhost(player);
            VisibilitySystem.Refresh(state);
            return true;
        }

        private void EnterGhost(PlayerState player, bool preserveTraining = false)
        {
            if (player.GhostEndTick != 0) return;
            player.GhostEndTick = Math.Max(1L, state.Tick + state.Rules.GhostDecayTicks);
            player.ProtectionEndTick = 0;
            state.GhostCount++;
            if (preserveTraining) TrainingSystem.Pause(state, player);
            else TrainingSystem.Cancel(player);
            ConstructionSystem.CancelPendingBuildings(state, player);
            foreach (UnitState unit in state.Units)
                if (unit.OwnerId == player.Id)
                {
                    unit.Ghost = true;
                    unit.Home = unit.Position;
                }
            state.Commands.RemoveAll(command => command.Player == player.Id);
            player.Direction = Vector2.Zero;
            player.RallyThroughUnitId = 0;
        }

        /// <summary>계정 확인을 마친 서버가 만료 전 유령의 새 명령 세션을 시작한다.</summary>
        public bool TryResumeGhost(int playerId)
        {
            if (!state.PlayerById.TryGetValue(playerId, out PlayerState player) || player.BaseDestroyed ||
                player.GhostEndTick <= state.Tick) return false;
            player.GhostEndTick = 0;
            TrainingSystem.Resume(state, player);
            player.LastSequence = player.AppliedSequence = 0;
            player.RateWindow = -1;
            player.CommandsInWindow = 0;
            player.FormationDirty = true;
            state.GhostCount--;
            foreach (UnitState unit in state.Units)
                if (unit.OwnerId == playerId) unit.Ghost = false;
            VisibilitySystem.Refresh(state);
            return true;
        }

        private void ApplyCommands()
        {
            foreach (QueuedCommand queued in state.Commands)
            {
                PlayerState player = state.PlayerById[queued.Player];
                SimCommand command = queued.Command;
                player.AppliedSequence = command.Sequence;
                if (command.Kind == CommandKind.MoveDir)
                {
                    Vector2 direction = command.Direction;
                    float scale = Math.Max(Math.Abs(direction.X), Math.Abs(direction.Y));
                    // 작은 입력도 방향만 뜻한다. 먼저 성분을 스케일링해 정규화의 언더플로·오버플로를 막는다.
                    if (scale > 0) direction = Vector2.Normalize(direction / scale);
                    player.Direction = direction;
                }
                else if (command.Kind == CommandKind.Rally)
                {
                    // 이후 양성된 수비대는 다음 집결 명령을 기다린다.
                    player.RallyThroughUnitId = state.NextId - 1;
                }
                else
                {
                    CommandFailure failure = command.Kind switch
                    {
                        CommandKind.Build => ConstructionSystem.Build(state, player, command),
                        CommandKind.UpgradeBase => ConstructionSystem.Upgrade(state, player),
                        CommandKind.CancelTraining => TrainingSystem.CancelWaiting(player, command.TrainingOrderId),
                        _ => TrainingSystem.Start(state, player, state.Rules.ElementIndex(command.Element))
                    };
                    if (failure != CommandFailure.None) Reject(player, command, failure);
                    else if (command.Kind != CommandKind.Train)
                        state.Events.Add(new SimEvent(SimEventKind.CommandApplied, player.Id, 0, 0, player.BasePosition,
                            detail: (int)command.Kind, sequence: command.Sequence));
                }
            }
            state.Commands.Clear();
        }
        private void Reject(PlayerState player, SimCommand command, CommandFailure failure) => state.Events.Add(new SimEvent(SimEventKind.CommandRejected, player.Id, 0, 0, player.LegionPosition, detail: (int)failure, sequence: command.Sequence));
        private void ReserveBuffers()
        {
            int count = state.Units.Count;
            int commands = checked(state.Players.Count * state.Rules.CommandRateLimit);
            int events = checked(count * 4 + commands);
            // 동적 접속에서는 이전 관측·전투 임시 목록이 남아 있으므로 예약 용량을 축소하지 않는다.
            state.Query.Capacity = Math.Max(state.Query.Capacity, count);
            state.Events.Capacity = Math.Max(state.Events.Capacity, events);
            state.Commands.Capacity = Math.Max(state.Commands.Capacity, commands);
            state.Cores.Capacity = Math.Max(state.Cores.Capacity, count);
            combat.Reserve(count); economy.Reserve(count);
            foreach (PlayerState player in state.Players)
            {
                player.Legion.Capacity = Math.Max(player.Legion.Capacity, player.PopulationCap);
                player.OwnedUnits.Capacity = Math.Max(player.OwnedUnits.Capacity, player.PopulationCap);
                player.VisionSources.Capacity = Math.Max(player.VisionSources.Capacity, player.PopulationCap + state.Buildings.Count);
                player.View.OwnerColorBuffer.Capacity = Math.Max(player.View.OwnerColorBuffer.Capacity, state.Rules.MaxPlayers);
                player.View.UnitBuffer.Capacity = Math.Max(player.View.UnitBuffer.Capacity, count);
                player.View.CoreBuffer.Capacity = Math.Max(player.View.CoreBuffer.Capacity, count);
                player.View.EventBuffer.Capacity = Math.Max(player.View.EventBuffer.Capacity, events);
                player.View.BuildingBuffer.Capacity = Math.Max(player.View.BuildingBuffer.Capacity, state.Buildings.Count);
                player.PreviousObservedUnits.EnsureCapacity(count);
                player.PreviousObservedBuildings.EnsureCapacity(state.Buildings.Count);
                player.View.AreaBuffer.Capacity = Math.Max(player.View.AreaBuffer.Capacity, count);
            }
        }
        private long Timestamp() => state.CollectTimings ? Stopwatch.GetTimestamp() : 0;
        private void MarkStage(int stage, ref long start)
        {
            if (!state.CollectTimings) return;
            long now = Stopwatch.GetTimestamp(); state.Metrics.StageBuffer[stage] = now - start; start = now;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void ValidatePosition(GameRules rules, Vector2 position)
        {
            if (!Finite(position.X) || !Finite(position.Y) || position.X < 0 || position.Y < 0 || position.X > rules.MapSize || position.Y > rules.MapSize) throw new ArgumentException("좌표는 월드 안의 유한한 값이어야 합니다.");
        }
        private static void AddForge(WorldState world, PlayerState player, float radius, int side)
        {
            Vector2 position = Vector2.Clamp(player.BasePosition + new Vector2(side * (world.Rules.BaseRadius + radius + world.Rules.SlotSpacing), 0), Vector2.Zero, new Vector2(world.Rules.MapSize));
            world.AddBuilding(player, side > 0 ? BuildingKind.NatureForge : BuildingKind.EnergyForge, position);
        }
    }
}
