using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

/// <summary>한 시나리오의 시간·실제 부하·규칙 활동·준비 비용을 보관한다.</summary>
public sealed class BenchmarkResult
{
    public ScenarioKind Scenario { get; internal set; }
    public ScenarioKind Layout { get; internal set; }
    public bool FixedLoad { get; internal set; }
    public int InitialReserved { get; internal set; }
    public int MinReserved { get; internal set; } = int.MaxValue;
    public int MaxReserved { get; internal set; }
    public int FinalReserved { get; internal set; }
    public int MaxLiveAndReserved { get; internal set; }
    public int MaxCandidates { get; internal set; }
    public long CandidateTicks { get; internal set; }
    public long GhostTicks { get; internal set; }
    public long EcologyTicks { get; internal set; }
    public int InitialEcologyUnits { get; internal set; }
    public long EcologyRespawns { get; internal set; }
    public long EcologyCoreSpawns { get; internal set; }
    public long Alarms { get; internal set; }
    public long RecallOrders { get; internal set; }
    public long CheckpointPreparationTicks { get; internal set; }
    public long BackupPreparationTicks { get; internal set; }
    public long SnapshotAllocatedBytes { get; internal set; }
    public int SnapshotSamples { get; internal set; }
    public double ServerTickBudgetMs { get; internal set; } = 50;
    public int SimulationOverBudgetCount { get; internal set; }
    public MainWorldPipelineResult? Pipeline { get; internal set; }
    public int Samples { get; internal set; }
    public int InitialPlayerUnits { get; internal set; }
    public int InitialNeutralUnits { get; internal set; }
    public int MinStartPlayerUnits { get; internal set; } = int.MaxValue;
    public int MinStartNeutralUnits { get; internal set; } = int.MaxValue;
    public int MinPlayerUnits { get; internal set; } = int.MaxValue;
    public int MinNeutralUnits { get; internal set; } = int.MaxValue;
    public int FinalPlayerUnits { get; internal set; }
    public int FinalNeutralUnits { get; internal set; }
    public long AllocatedBytes { get; internal set; }
    public long PreparationAllocatedBytes { get; internal set; }
    public long PreparationTicks { get; internal set; }
    public int Resets { get; internal set; }
    public int[] Collections { get; } = new int[3];
    public int[] SimulationCollections { get; } = new int[3];
    public int[] ReplicationCollections { get; } = new int[3];
    public int[] PreparationCollections { get; } = new int[3];
    public long Attacks { get; internal set; }
    public long Deaths { get; internal set; }
    public long Conversions { get; internal set; }
    public long Drops { get; internal set; }
    public long Trained { get; internal set; }
    public long Pickups { get; internal set; }
    public long[] Reactions { get; } = new long[6];
    public long[] StageTicks { get; } = new long[8];
    public long BotTicks { get; internal set; }
    public long PrepareAuraTicks { get; internal set; }
    public long SelectTargetTicks { get; internal set; }
    public TickStatistics Statistics { get; internal set; } = null!;
    public bool RequiredLoadMaintained => FixedLoad
        ? MinStartPlayerUnits == InitialPlayerUnits && MinStartNeutralUnits == InitialNeutralUnits
        : MinPlayerUnits >= InitialPlayerUnits && MinNeutralUnits >= InitialNeutralUnits;
    public bool SimulationBudgetPassed => Statistics.P99Ms <= BenchmarkRunner.TargetP99Ms;
    public bool WithinServerTickBudget => Statistics.P99Ms <= ServerTickBudgetMs;
    public bool Passed => SimulationBudgetPassed && RequiredLoadMaintained && (Pipeline?.Passed ?? true);
}

/// <summary>봇 판단부터 시야 계산까지 측정한다. 부하 복구와 출력은 시간·할당 표본에서 분리한다.</summary>
public static class BenchmarkRunner
{
    public const int Seed = 1;
    public const double TargetP99Ms = 10;
    public static IReadOnlyList<BenchmarkResult> Run(GameRules rules, int bots, TextWriter output, CancellationToken cancellation, int warmupTicks = 1000, int measuredTicks = 10000)
        => RunCases(rules, bots, new[] { (ScenarioKind.Distributed, ScenarioKind.Distributed), (ScenarioKind.Clustered, ScenarioKind.Clustered), (ScenarioKind.Continuous, ScenarioKind.Continuous) }, output, cancellation, warmupTicks, measuredTicks);

    public static IReadOnlyList<BenchmarkResult> Run(GameRules rules, int bots, ScenarioKind scenario, TextWriter output, CancellationToken cancellation, int warmupTicks = 1000, int measuredTicks = 10000)
        => RunMainWorld(rules, null, bots, scenario, output, cancellation, warmupTicks, measuredTicks);

    public static IReadOnlyList<BenchmarkResult> Run(GameRules rules, NetworkSettings settings, int bots, ScenarioKind scenario, TextWriter output, CancellationToken cancellation, int warmupTicks = 1000, int measuredTicks = 10000)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return RunMainWorld(rules, settings, bots, scenario, output, cancellation, warmupTicks, measuredTicks);
    }

    private static IReadOnlyList<BenchmarkResult> RunMainWorld(GameRules rules, NetworkSettings? settings, int bots, ScenarioKind scenario, TextWriter output, CancellationToken cancellation, int warmupTicks, int measuredTicks)
    {
        if (!ScenarioFactory.IsMainWorld(scenario)) throw new ArgumentOutOfRangeException(nameof(scenario));
        var cases = scenario == ScenarioKind.MainWorldDense
            ? new[] { (scenario, ScenarioKind.Distributed), (scenario, ScenarioKind.Clustered), (scenario, ScenarioKind.Continuous) }
            : new[] { (scenario, ScenarioKind.Continuous) };
        return RunCases(rules, bots, cases, output, cancellation, warmupTicks, measuredTicks, settings);
    }

    private static IReadOnlyList<BenchmarkResult> RunCases(GameRules rules, int bots, (ScenarioKind scenario, ScenarioKind layout)[] cases, TextWriter output, CancellationToken cancellation, int warmupTicks, int measuredTicks, NetworkSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(rules); ArgumentNullException.ThrowIfNull(output);
        if (warmupTicks < 0) throw new ArgumentOutOfRangeException(nameof(warmupTicks));
        if (measuredTicks < 1) throw new ArgumentOutOfRangeException(nameof(measuredTicks));
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        output.WriteLine($"M0 {configuration}; seed={Seed}; warmup={warmupTicks}; samples={measuredTicks}; target p99<={TargetP99Ms}ms; tickRate={rules.TickRate}");
        output.WriteLine($"CPU={CpuName()}; logicalProcessors={Environment.ProcessorCount}; runtime={RuntimeInformation.FrameworkDescription}; architecture={RuntimeInformation.ProcessArchitecture}; OS={RuntimeInformation.OSDescription}");
        output.WriteLine("gamedata SHA-256: " + rules.DataHash);
        var results = new List<BenchmarkResult>();
        foreach ((ScenarioKind kind, ScenarioKind layout) in cases)
        {
            bool mainWorld = ScenarioFactory.IsMainWorld(kind);
            WorldSetup setup = mainWorld ? ScenarioFactory.CreateMainWorld(rules, bots, kind, layout) : ScenarioFactory.Create(rules, bots, kind);
            SimulationWorld world = CreateWorld(rules, setup, kind);
            BotBrain[] brains = CreateBots(rules, setup, mainWorld);
            var warmPipelineResult = settings == null ? null : new MainWorldPipelineResult { BudgetMs = rules.DeltaTime * 1000 };
            MainWorldPipeline? pipeline = settings == null ? null : new MainWorldPipeline(rules, settings, setup, warmPipelineResult!);
            try
            {
            int expectedPlayers = world.Metrics.PlayerUnits, expectedNeutrals = world.Metrics.NeutralUnits;
            bool fixedLoad = layout != ScenarioKind.Continuous;
            for (int tick = 0; tick < warmupTicks; tick++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (fixedLoad && NeedsReset(world, expectedPlayers, expectedNeutrals))
                {
                    pipeline?.Dispose();
                    world = CreateWorld(rules, setup, kind); brains = CreateBots(rules, setup, mainWorld);
                    pipeline = settings == null ? null : new MainWorldPipeline(rules, settings, setup, warmPipelineResult!);
                }
                StepBots(world, setup, brains); world.Step();
                pipeline?.Tick(world, brains, tick + 1);
            }
            // 연속 진행의 측정 구간도 명시한 최대 인구에서 시작한다. 워밍업으로 줄어든 인구를 그대로 쓰지 않는다.
            pipeline?.Dispose();
            world = CreateWorld(rules, setup, kind); brains = CreateBots(rules, setup, mainWorld);
            var result = new BenchmarkResult { Scenario = kind, Layout = layout, FixedLoad = fixedLoad, Samples = measuredTicks, InitialPlayerUnits = expectedPlayers,
                InitialNeutralUnits = expectedNeutrals, InitialReserved = world.Metrics.ReservedUnitSlots, ServerTickBudgetMs = rules.DeltaTime * 1000 };
            result.InitialEcologyUnits = world.Metrics.EcologyNeutralUnits;
            result.Pipeline = settings == null ? null : new MainWorldPipelineResult { BudgetMs = rules.DeltaTime * 1000 };
            pipeline = settings == null ? null : new MainWorldPipeline(rules, settings, setup, result.Pipeline!);
            var samples = new long[measuredTicks];
            var pipelineSamples = settings == null ? null : new long[measuredTicks];
            var previousReactions = new long[6];
            for (int generation = 0; generation < result.Collections.Length; generation++) result.Collections[generation] = GC.CollectionCount(generation);
            for (int tick = 0; tick < measuredTicks; tick++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (fixedLoad && NeedsReset(world, expectedPlayers, expectedNeutrals))
                {
                    var collections = mainWorld ? ReadCollections() : default;
                    long prepAllocated = GC.GetAllocatedBytesForCurrentThread(), prepStart = Stopwatch.GetTimestamp();
                    pipeline?.Dispose();
                    world = CreateWorld(rules, setup, kind); brains = CreateBots(rules, setup, mainWorld);
                    pipeline = settings == null ? null : new MainWorldPipeline(rules, settings, setup, result.Pipeline!);
                    result.PreparationTicks += Stopwatch.GetTimestamp() - prepStart;
                    result.PreparationAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - prepAllocated;
                    if (mainWorld) AddCollections(result.PreparationCollections, collections);
                    result.Resets++;
                }
                WorldMetrics metrics = world.Metrics;
                result.MinStartPlayerUnits = Math.Min(result.MinStartPlayerUnits, metrics.PlayerUnits);
                result.MinStartNeutralUnits = Math.Min(result.MinStartNeutralUnits, metrics.NeutralUnits);
                result.MaxLiveAndReserved = Math.Max(result.MaxLiveAndReserved, metrics.PlayerUnits + metrics.NeutralUnits + metrics.ReservedUnitSlots);
                long attacks = metrics.Attacks, deaths = metrics.Deaths, conversions = metrics.Conversions, drops = metrics.Drops, trained = metrics.Trained, pickups = metrics.Pickups;
                long respawns = metrics.EcologyRespawns, coreSpawns = metrics.EcologyCoreSpawns;
                for (int i = 0; i < previousReactions.Length; i++) previousReactions[i] = metrics.Reactions[i];
                var simulationCollections = mainWorld ? ReadCollections() : default;
                long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                StepBots(world, setup, brains, result);
                long botEnd = Stopwatch.GetTimestamp();
                world.Step();
                samples[tick] = Stopwatch.GetTimestamp() - start;
                result.AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (mainWorld) AddCollections(result.SimulationCollections, simulationCollections);
                if (pipeline != null)
                {
                    var replicationCollections = ReadCollections();
                    pipeline.Tick(world, brains, tick + 1);
                    pipelineSamples![tick] = Stopwatch.GetTimestamp() - start;
                    result.Pipeline!.AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                    AddCollections(result.ReplicationCollections, replicationCollections);
                }
                result.BotTicks += botEnd - start;
                result.Attacks += metrics.Attacks - attacks; result.Deaths += metrics.Deaths - deaths; result.Conversions += metrics.Conversions - conversions;
                result.Drops += metrics.Drops - drops; result.Trained += metrics.Trained - trained; result.Pickups += metrics.Pickups - pickups;
                result.EcologyRespawns += metrics.EcologyRespawns - respawns;
                result.EcologyCoreSpawns += metrics.EcologyCoreSpawns - coreSpawns;
                for (int i = 0; i < result.Reactions.Length; i++) result.Reactions[i] += metrics.Reactions[i] - previousReactions[i];
                for (int i = 0; i < result.StageTicks.Length; i++) result.StageTicks[i] += metrics.StageTicks[i];
                result.PrepareAuraTicks += metrics.PrepareAuraTicks;
                result.SelectTargetTicks += metrics.SelectTargetTicks;
                result.MinPlayerUnits = Math.Min(result.MinPlayerUnits, metrics.PlayerUnits); result.MinNeutralUnits = Math.Min(result.MinNeutralUnits, metrics.NeutralUnits);
                result.MinReserved = Math.Min(result.MinReserved, metrics.ReservedUnitSlots);
                result.MaxReserved = Math.Max(result.MaxReserved, metrics.ReservedUnitSlots);
                result.MaxLiveAndReserved = Math.Max(result.MaxLiveAndReserved, metrics.PlayerUnits + metrics.NeutralUnits + metrics.ReservedUnitSlots);
                result.MaxCandidates = Math.Max(result.MaxCandidates, metrics.Candidates);
                if (metrics.Candidates > 0) result.CandidateTicks++;
                if (metrics.Ghosts > 0) result.GhostTicks++;
                if (metrics.EcologyNeutralUnits > 0) result.EcologyTicks++;
                if (samples[tick] * 1000.0 / Stopwatch.Frequency > TargetP99Ms) result.SimulationOverBudgetCount++;
                foreach (PlayerSpawn player in setup.Players)
                    if (world.TryGetView(player.Id, out WorldView view))
                        foreach (SimEvent item in view.Events)
                            if (item.Kind == SimEventKind.BaseUnderAttack && item.OwnerId == player.Id) result.Alarms++;
                if (mainWorld && (tick == 0 || tick == measuredTicks - 1))
                {
                    var collections = ReadCollections();
                    long snapshotAllocated = GC.GetAllocatedBytesForCurrentThread(), captureStart = Stopwatch.GetTimestamp();
                    GC.KeepAlive(world.CaptureCheckpoint());
                    long checkpointEnd = Stopwatch.GetTimestamp();
                    GC.KeepAlive(world.CaptureMigrationState());
                    result.CheckpointPreparationTicks += checkpointEnd - captureStart;
                    result.BackupPreparationTicks += Stopwatch.GetTimestamp() - checkpointEnd;
                    result.SnapshotAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - snapshotAllocated;
                    result.SnapshotSamples++;
                    AddCollections(result.PreparationCollections, collections);
                }
                if (mainWorld && ((tick + 1) % 1000 == 0 || tick == measuredTicks - 1))
                    output.WriteLine($"  progress {kind}/{layout}: measured={tick + 1}/{measuredTicks}; resets={result.Resets}; live={metrics.PlayerUnits}+{metrics.NeutralUnits}; reserved={metrics.ReservedUnitSlots}");
            }
            result.FinalPlayerUnits = world.Metrics.PlayerUnits; result.FinalNeutralUnits = world.Metrics.NeutralUnits;
            result.FinalReserved = world.Metrics.ReservedUnitSlots;
            for (int generation = 0; generation < result.Collections.Length; generation++) result.Collections[generation] = GC.CollectionCount(generation) - result.Collections[generation];
            result.Statistics = TickStatistics.Calculate(samples, Stopwatch.Frequency, rules.DeltaTime * 1000);
            pipeline?.Drain();
            if (result.Pipeline != null) result.Pipeline.Statistics = TickStatistics.Calculate(pipelineSamples!, Stopwatch.Frequency, rules.DeltaTime * 1000);
            results.Add(result); Print(output, result, rules);
            }
            finally { pipeline?.Dispose(); }
        }
        return results.AsReadOnly();
    }

    private static SimulationWorld CreateWorld(GameRules rules, WorldSetup setup, ScenarioKind kind)
    {
        var world = new SimulationWorld(rules, setup, Seed);
        if (kind is ScenarioKind.MainWorldDefense or ScenarioKind.MainWorldRecovery)
        {
            world.EnableEcology(rules.RoomMinBaseDistance);
            if (kind == ScenarioKind.MainWorldRecovery && setup.Players.Count > 2) world.BeginGhost(setup.Players[^1].Id);
        }
        return world;
    }

    internal static BotBrain[] CreateBots(GameRules rules, WorldSetup setup, bool playMatch = false)
    {
        var brains = new BotBrain[setup.Players.Count];
        for (int i = 0; i < brains.Length; i++) brains[i] = new BotBrain(rules, setup.Players[i].Id, playMatch);
        return brains;
    }
    internal static void StepBots(SimulationWorld world, WorldSetup setup, BotBrain[] brains, BenchmarkResult? result = null)
    {
        for (int i = 0; i < brains.Length; i++)
        {
            int playerId = setup.Players[i].Id;
            if (!world.TryGetView(playerId, out WorldView view)) continue;
            bool ghost = false;
            foreach (BuildingView building in view.Buildings)
                if (building.OwnerId == playerId && building.Kind == BuildingKind.Base && building.GhostEndTick != 0) { ghost = true; break; }
            if (ghost) continue;
            SimCommand? command = brains[i].Decide(view);
            if (!command.HasValue) continue;
            CommandAdmission admission = world.TryEnqueue(playerId, command.Value);
            if (admission != CommandAdmission.Accepted) throw new InvalidOperationException("봇 명령 접수 실패: " + admission);
            if (result != null && command.Value.Kind == CommandKind.Recall) result.RecallOrders++;
        }
    }
    private static bool NeedsReset(SimulationWorld world, int players, int neutrals) => world.Metrics.PlayerUnits != players || world.Metrics.NeutralUnits != neutrals;
    private static (int Gen0, int Gen1, int Gen2) ReadCollections() => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    private static void AddCollections(int[] target, (int Gen0, int Gen1, int Gen2) before)
    {
        target[0] += GC.CollectionCount(0) - before.Gen0;
        target[1] += GC.CollectionCount(1) - before.Gen1;
        target[2] += GC.CollectionCount(2) - before.Gen2;
    }
    private static void Print(TextWriter output, BenchmarkResult result, GameRules rules)
    {
        double milliseconds = 1000.0 / Stopwatch.Frequency;
        output.WriteLine(FormattableString.Invariant($"{result.Scenario}/{result.Layout}: mean={result.Statistics.MeanMs:F3}ms p99={result.Statistics.P99Ms:F3}ms max={result.Statistics.MaxMs:F3}ms over{rules.DeltaTime * 1000:F0}ms={result.Statistics.OverBudgetCount}; {(result.Passed ? "PASS" : "FAIL")}"));
        output.WriteLine($"  simulation-only p99<=10ms={result.SimulationBudgetPassed}; over10ms={result.SimulationOverBudgetCount}; within server {result.ServerTickBudgetMs}ms envelope={result.WithinServerTickBudget}; required load maintained={result.RequiredLoadMaintained}; full-server replication/send-queue={(result.Pipeline == null ? "not measured" : "included below")}");
        output.WriteLine($"  load initial={result.InitialPlayerUnits}+{result.InitialNeutralUnits}; sampleStartMin={result.MinStartPlayerUnits}+{result.MinStartNeutralUnits}; postTickMin={result.MinPlayerUnits}+{result.MinNeutralUnits}; final={result.FinalPlayerUnits}+{result.FinalNeutralUnits}; resets={result.Resets}");
        output.WriteLine(FormattableString.Invariant($"  tick allocated={result.AllocatedBytes / (double)result.Samples:F1}B/tick; preparation={result.PreparationTicks * milliseconds:F2}ms/{result.PreparationAllocatedBytes}B; GC(including preparation)={string.Join("/", result.Collections)}"));
        if (ScenarioFactory.IsMainWorld(result.Scenario)) output.WriteLine($"  GC intervals 0/1/2: simulation={string.Join("/", result.SimulationCollections)}; replication+due capture+drain={string.Join("/", result.ReplicationCollections)}; resets+endpoint captures={string.Join("/", result.PreparationCollections)}; counts identify collection intervals, not isolated pause time");
        output.WriteLine($"  attacks={result.Attacks}; deaths={result.Deaths}; conversions={result.Conversions}; drops={result.Drops}; trained={result.Trained}; pickups={result.Pickups}; reactions[{string.Join(",", Enum.GetNames<ReactionKind>())}]={string.Join(",", result.Reactions)}");
        output.WriteLine($"  reserved initial/min/max/final={result.InitialReserved}/{result.MinReserved}/{result.MaxReserved}/{result.FinalReserved}; peak live+reserved={result.MaxLiveAndReserved}; candidates max/ticks={result.MaxCandidates}/{result.CandidateTicks}; ghostTicks={result.GhostTicks}; ecologyTicks={result.EcologyTicks}; alarms={result.Alarms}; recallOrders={result.RecallOrders}");
        output.WriteLine($"  ecology initial live={result.InitialEcologyUnits}; actual respawns={result.EcologyRespawns}; actual map core spawns={result.EcologyCoreSpawns}");
        output.WriteLine(FormattableString.Invariant($"  snapshot preparation samples={result.SnapshotSamples}; checkpoint={result.CheckpointPreparationTicks * milliseconds:F3}ms; migration={result.BackupPreparationTicks * milliseconds:F3}ms; allocated={result.SnapshotAllocatedBytes}B; encode/disk/transfer=not measured"));
        output.WriteLine(FormattableString.Invariant($"  stage mean ms: bots={result.BotTicks * milliseconds / result.Samples:F3}; command/move/formation/search+aura/combat/death/economy/vision={string.Join("/", result.StageTicks.Select(value => (value * milliseconds / result.Samples).ToString("F3", CultureInfo.InvariantCulture)))}"));
        output.WriteLine(FormattableString.Invariant($"  search+aura detail mean ms: prepareAura={result.PrepareAuraTicks * milliseconds / result.Samples:F3}; selectTargets={result.SelectTargetTicks * milliseconds / result.Samples:F3}"));
        if (result.Pipeline is MainWorldPipelineResult server)
        {
            var stats = server.Statistics!;
            output.WriteLine(FormattableString.Invariant($"  server tick-boundary pipeline: mean={stats.MeanMs:F3}ms p99={stats.P99Ms:F3}ms max={stats.MaxMs:F3}ms over{server.BudgetMs:F0}ms={stats.OverBudgetCount}; {(server.Passed ? "PASS" : "FAIL")}; replication mean={server.ReplicationTicks * milliseconds / result.Samples:F3}ms; total allocated={server.AllocatedBytes / (double)result.Samples:F1}B/tick"));
            output.WriteLine($"  real bounded queues: owners={server.OwnersMeasured}; minimum active={server.MinActiveOwners}; ownerFrames={server.OwnerFrames}; queued/drained={server.QueuedBytes}/{server.DrainedBytes}B; reliablePackets={server.ReliablePackets}; snapshotChunks={server.SnapshotChunks}; maxPacket={server.MaxPacketBytes}B; peakPendingPerOwner={server.MaxPendingBytes}B; failures={server.QueueFailures}");
            output.WriteLine(FormattableString.Invariant($"  replication detail mean ms: capture={server.EventCaptureTicks * milliseconds / result.Samples:F3}; prepare={server.PrepareTicks * milliseconds / result.Samples:F3}; write+queue={server.WriteQueueTicks * milliseconds / result.Samples:F3}; drain={server.DrainTicks * milliseconds / result.Samples:F3}"));
            output.WriteLine(FormattableString.Invariant($"  due boundary captures: migration={server.MigrationCaptures}/{server.MigrationCaptureTicks * milliseconds:F3}ms; checkpoint={server.CheckpointCaptures}/{server.CheckpointCaptureTicks * milliseconds:F3}ms; allocated={server.CaptureAllocatedBytes}B; JSON/compression/socket/disk I/O excluded; queue drain assumes healthy downstream, not network delivery proof"));
        }
    }
    private static string CpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name) return name.Trim();
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
    }
}
