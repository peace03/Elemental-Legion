using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

/// <summary>한 시나리오의 시간·실제 부하·규칙 활동·준비 비용을 보관한다.</summary>
public sealed class BenchmarkResult
{
    public ScenarioKind Scenario { get; internal set; }
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
    public long Attacks { get; internal set; }
    public long Deaths { get; internal set; }
    public long Conversions { get; internal set; }
    public long Drops { get; internal set; }
    public long Trained { get; internal set; }
    public long Pickups { get; internal set; }
    public long[] Reactions { get; } = new long[6];
    public long[] StageTicks { get; } = new long[8];
    public long BotTicks { get; internal set; }
    public TickStatistics Statistics { get; internal set; } = null!;
    public bool Passed => Scenario == ScenarioKind.Continuous || Statistics.P99Ms <= BenchmarkRunner.TargetP99Ms;
}

/// <summary>봇 판단부터 시야 계산까지 측정한다. 부하 복구와 출력은 시간·할당 표본에서 분리한다.</summary>
public static class BenchmarkRunner
{
    public const int Seed = 1;
    public const double TargetP99Ms = 10;
    public static IReadOnlyList<BenchmarkResult> Run(GameRules rules, int bots, TextWriter output, CancellationToken cancellation, int warmupTicks = 1000, int measuredTicks = 10000)
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
        foreach (ScenarioKind kind in Enum.GetValues<ScenarioKind>())
        {
            WorldSetup setup = ScenarioFactory.Create(rules, bots, kind);
            SimulationWorld world = new(rules, setup, Seed);
            BotBrain[] brains = CreateBots(rules, setup);
            int expectedPlayers = world.Metrics.PlayerUnits, expectedNeutrals = world.Metrics.NeutralUnits;
            bool fixedLoad = kind != ScenarioKind.Continuous;
            for (int tick = 0; tick < warmupTicks; tick++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (fixedLoad && NeedsReset(world, expectedPlayers, expectedNeutrals)) { world = new SimulationWorld(rules, setup, Seed); brains = CreateBots(rules, setup); }
                StepBots(world, setup, brains); world.Step();
            }
            // 연속 진행의 측정 구간도 명시한 최대 인구에서 시작한다. 워밍업으로 줄어든 인구를 그대로 쓰지 않는다.
            world = new SimulationWorld(rules, setup, Seed); brains = CreateBots(rules, setup);
            var result = new BenchmarkResult { Scenario = kind, Samples = measuredTicks, InitialPlayerUnits = expectedPlayers, InitialNeutralUnits = expectedNeutrals };
            var samples = new long[measuredTicks];
            var previousReactions = new long[6];
            for (int generation = 0; generation < result.Collections.Length; generation++) result.Collections[generation] = GC.CollectionCount(generation);
            for (int tick = 0; tick < measuredTicks; tick++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (fixedLoad && NeedsReset(world, expectedPlayers, expectedNeutrals))
                {
                    long prepAllocated = GC.GetAllocatedBytesForCurrentThread(), prepStart = Stopwatch.GetTimestamp();
                    world = new SimulationWorld(rules, setup, Seed); brains = CreateBots(rules, setup);
                    result.PreparationTicks += Stopwatch.GetTimestamp() - prepStart;
                    result.PreparationAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - prepAllocated;
                    result.Resets++;
                }
                WorldMetrics metrics = world.Metrics;
                result.MinStartPlayerUnits = Math.Min(result.MinStartPlayerUnits, metrics.PlayerUnits);
                result.MinStartNeutralUnits = Math.Min(result.MinStartNeutralUnits, metrics.NeutralUnits);
                long attacks = metrics.Attacks, deaths = metrics.Deaths, conversions = metrics.Conversions, drops = metrics.Drops, trained = metrics.Trained, pickups = metrics.Pickups;
                for (int i = 0; i < previousReactions.Length; i++) previousReactions[i] = metrics.Reactions[i];
                long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                StepBots(world, setup, brains);
                long botEnd = Stopwatch.GetTimestamp();
                world.Step();
                samples[tick] = Stopwatch.GetTimestamp() - start;
                result.AllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                result.BotTicks += botEnd - start;
                result.Attacks += metrics.Attacks - attacks; result.Deaths += metrics.Deaths - deaths; result.Conversions += metrics.Conversions - conversions;
                result.Drops += metrics.Drops - drops; result.Trained += metrics.Trained - trained; result.Pickups += metrics.Pickups - pickups;
                for (int i = 0; i < result.Reactions.Length; i++) result.Reactions[i] += metrics.Reactions[i] - previousReactions[i];
                for (int i = 0; i < result.StageTicks.Length; i++) result.StageTicks[i] += metrics.StageTicks[i];
                result.MinPlayerUnits = Math.Min(result.MinPlayerUnits, metrics.PlayerUnits); result.MinNeutralUnits = Math.Min(result.MinNeutralUnits, metrics.NeutralUnits);
            }
            result.FinalPlayerUnits = world.Metrics.PlayerUnits; result.FinalNeutralUnits = world.Metrics.NeutralUnits;
            for (int generation = 0; generation < result.Collections.Length; generation++) result.Collections[generation] = GC.CollectionCount(generation) - result.Collections[generation];
            result.Statistics = TickStatistics.Calculate(samples, Stopwatch.Frequency, rules.DeltaTime * 1000);
            results.Add(result); Print(output, result, rules);
        }
        return results.AsReadOnly();
    }

    internal static BotBrain[] CreateBots(GameRules rules, WorldSetup setup)
    {
        var brains = new BotBrain[setup.Players.Count];
        for (int i = 0; i < brains.Length; i++) brains[i] = new BotBrain(rules, setup.Players[i].Id);
        return brains;
    }
    internal static void StepBots(SimulationWorld world, WorldSetup setup, BotBrain[] brains)
    {
        for (int i = 0; i < brains.Length; i++)
        {
            SimCommand? command = brains[i].Decide(world.GetView(setup.Players[i].Id));
            if (!command.HasValue) continue;
            CommandAdmission admission = world.TryEnqueue(setup.Players[i].Id, command.Value);
            if (admission != CommandAdmission.Accepted) throw new InvalidOperationException("봇 명령 접수 실패: " + admission);
        }
    }
    private static bool NeedsReset(SimulationWorld world, int players, int neutrals) => world.Metrics.PlayerUnits != players || world.Metrics.NeutralUnits != neutrals;
    private static void Print(TextWriter output, BenchmarkResult result, GameRules rules)
    {
        double milliseconds = 1000.0 / Stopwatch.Frequency;
        output.WriteLine(FormattableString.Invariant($"{result.Scenario}: mean={result.Statistics.MeanMs:F3}ms p99={result.Statistics.P99Ms:F3}ms max={result.Statistics.MaxMs:F3}ms over{rules.DeltaTime * 1000:F0}ms={result.Statistics.OverBudgetCount}; {(result.Scenario == ScenarioKind.Continuous ? "CONTINUOUS" : result.Passed ? "PASS" : "FAIL")}"));
        output.WriteLine($"  load initial={result.InitialPlayerUnits}+{result.InitialNeutralUnits}; sampleStartMin={result.MinStartPlayerUnits}+{result.MinStartNeutralUnits}; postTickMin={result.MinPlayerUnits}+{result.MinNeutralUnits}; final={result.FinalPlayerUnits}+{result.FinalNeutralUnits}; resets={result.Resets}");
        output.WriteLine(FormattableString.Invariant($"  tick allocated={result.AllocatedBytes / (double)result.Samples:F1}B/tick; preparation={result.PreparationTicks * milliseconds:F2}ms/{result.PreparationAllocatedBytes}B; GC(including preparation)={string.Join("/", result.Collections)}"));
        output.WriteLine($"  attacks={result.Attacks}; deaths={result.Deaths}; conversions={result.Conversions}; drops={result.Drops}; trained={result.Trained}; pickups={result.Pickups}; reactions[{string.Join(",", Enum.GetNames<ReactionKind>())}]={string.Join(",", result.Reactions)}");
        output.WriteLine(FormattableString.Invariant($"  stage mean ms: bots={result.BotTicks * milliseconds / result.Samples:F3}; command/move/formation/search+aura/combat/death/economy/vision={string.Join("/", result.StageTicks.Select(value => (value * milliseconds / result.Samples).ToString("F3", CultureInfo.InvariantCulture)))}"));
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
