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
        public WorldMetrics Metrics => state.Metrics;
        public SimulationWorld(GameRules rules, WorldSetup setup, int seed)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (setup.Players.Count > rules.MaxPlayers) throw new ArgumentException("플레이어 수가 상한을 넘었습니다.", nameof(setup));
            state = new WorldState(rules, seed, setup.CollectTimings);
            foreach (PlayerSpawn spawn in setup.Players)
            {
                if (spawn == null || spawn.Id < 0 || state.PlayerById.ContainsKey(spawn.Id) || spawn.Cores < 0) throw new ArgumentException("플레이어 ID 또는 자원이 잘못됐습니다.", nameof(setup));
                ValidatePosition(rules, spawn.BasePosition); ValidatePosition(rules, spawn.LegionPosition);
                int element = rules.ElementIndex(spawn.MainElement);
                if (!rules.StartingElements.Contains(element)) throw new ArgumentException("시작 원소는 허용된 자연 원소여야 합니다.", nameof(setup));
                var player = new PlayerState
                {
                    Id = spawn.Id, Slot = state.Players.Count, MainElement = element, BaseLevel = spawn.BaseLevel,
                    Vision = new VisionGrid(rules.MapSize, rules.CellSize),
                    BasePosition = spawn.BasePosition, LegionPosition = spawn.LegionPosition, Cores = spawn.Cores,
                    PopulationCap = rules.PopulationCap(spawn.BaseLevel), NatureForge = spawn.NatureForge, EnergyForge = spawn.EnergyForge
                };
                state.Players.Add(player); state.PlayerById.Add(player.Id, player);
                state.Buildings.Add(new BuildingState(state.NextBuildingId++, player.Id, BuildingKind.Base, player.BasePosition, rules.BaseRadius));
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
            if (!state.PlayerById.TryGetValue(playerId, out PlayerState player)) return CommandAdmission.UnknownPlayer;
            if (command.Sequence == 0 || command.Kind < CommandKind.MoveDir || command.Kind > CommandKind.Rally ||
                command.Kind == CommandKind.MoveDir && (!Finite(command.Direction.X) || !Finite(command.Direction.Y)) ||
                command.Kind == CommandKind.Train && !state.Rules.TryElementIndex(command.Element, out _)) return CommandAdmission.InvalidInput;
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
            ApplyCommands(); MarkStage(0, ref start);
            MovementSystem.MoveLegions(state); MarkStage(1, ref start);
            MovementSystem.MoveUnits(state); MarkStage(2, ref start);
            state.Grid.Rebuild(state.Units); combat.Prepare(state); combat.SelectTargets(state); MarkStage(3, ref start);
            combat.Run(state); MarkStage(4, ref start);
            economy.ResolveDeaths(state); MarkStage(5, ref start);
            TrainingSystem.Tick(state); state.Grid.Rebuild(state.Units); economy.TickCores(state); MarkStage(6, ref start);
            VisibilitySystem.Refresh(state); MarkStage(7, ref start);
        }

        public WorldView GetView(int playerId) => state.PlayerById.TryGetValue(playerId, out PlayerState player) ? player.View : throw new ArgumentException("없는 플레이어입니다.", nameof(playerId));

        /// <summary>서버가 검증한 신규 세션을 현재 월드에 추가한다. 기존 틱·유닛·시야 슬롯은 유지한다.</summary>
        public bool TryAddPlayer(PlayerSpawn spawn)
        {
            if (spawn == null) throw new ArgumentNullException(nameof(spawn));
            if (state.Players.Count >= state.Rules.MaxPlayers || spawn.Id < 0 || state.PlayerById.ContainsKey(spawn.Id)) return false;
            ValidatePosition(state.Rules, spawn.BasePosition); ValidatePosition(state.Rules, spawn.LegionPosition);
            int element = state.Rules.ElementIndex(spawn.MainElement);
            int cap = state.Rules.PopulationCap(spawn.BaseLevel);
            if (!state.Rules.StartingElements.Contains(element) || spawn.Cores < 0 || cap < state.Rules.StartUnitCount) return false;
            var player = new PlayerState
            {
                Id = spawn.Id, Slot = state.Players.Count, MainElement = element, BaseLevel = spawn.BaseLevel,
                Vision = new VisionGrid(state.Rules.MapSize, state.Rules.CellSize),
                BasePosition = spawn.BasePosition, LegionPosition = spawn.LegionPosition, Cores = spawn.Cores,
                PopulationCap = cap, NatureForge = spawn.NatureForge, EnergyForge = spawn.EnergyForge
            };
            state.Players.Add(player); state.PlayerById.Add(player.Id, player);
            state.Buildings.Add(new BuildingState(state.NextBuildingId++, player.Id, BuildingKind.Base, player.BasePosition, state.Rules.BaseRadius));
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

        /// <summary>접속 종료 시 대기·유지 중인 이동을 끊는다. 군단·본진·자원은 월드에 보존한다.</summary>
        public void StopPlayer(int playerId)
        {
            if (!state.PlayerById.TryGetValue(playerId, out PlayerState player)) return;
            state.Commands.RemoveAll(command => command.Player == playerId);
            player.Direction = Vector2.Zero; player.Rallying = false;
            VisibilitySystem.Refresh(state);
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
                else if (command.Kind == CommandKind.Rally) player.Rallying = true;
                else
                {
                    CommandFailure failure = TrainingSystem.Start(state, player, state.Rules.ElementIndex(command.Element));
                    if (failure != CommandFailure.None) Reject(player, command, failure);
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
                player.VisionSources.Capacity = Math.Max(player.VisionSources.Capacity, player.PopulationCap + state.Buildings.Count);
                player.View.UnitBuffer.Capacity = Math.Max(player.View.UnitBuffer.Capacity, count);
                player.View.CoreBuffer.Capacity = Math.Max(player.View.CoreBuffer.Capacity, count);
                player.View.EventBuffer.Capacity = Math.Max(player.View.EventBuffer.Capacity, events);
                player.View.BuildingBuffer.Capacity = Math.Max(player.View.BuildingBuffer.Capacity, state.Buildings.Count);
                player.PreviousObservedUnits.EnsureCapacity(count);
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
            world.Buildings.Add(new BuildingState(world.NextBuildingId++, player.Id, side>0?BuildingKind.NatureForge:BuildingKind.EnergyForge, position, radius));
        }
    }
}
