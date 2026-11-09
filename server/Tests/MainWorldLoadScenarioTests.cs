using System.Numerics;
using System.Reflection;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ElementalLegion.Tests;

public sealed class MainWorldLoadScenarioTests
{
    private readonly ITestOutputHelper output;
    public MainWorldLoadScenarioTests(ITestOutputHelper output) { this.output = output; }
    [Theory]
    [InlineData("main-world-defense", "MainWorldDefense")]
    [InlineData("main-world-recovery", "MainWorldRecovery")]
    [InlineData("main-world-dense", "MainWorldDense")]
    public void MainWorldScenarioCliRequiresBotsAndTickReport(string name, string kind)
    {
        var options = ServerOptions.Parse(["--bots", "15", "--report-tick", "--scenario", name], 15);
        Assert.Equal(name, options.Scenario);
        Assert.True(Enum.TryParse<ScenarioKind>(kind, out _));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--listen", "--scenario", name], 15));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--bots", "15", "--scenario", name], 15));
    }

    [Fact]
    public void MainWorldBotsUseOnlyObservedState()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 250)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 250)));
        for (int i = 0; i < 4; i++) setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(200 + i * 2, 210), garrison: true));
        setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(1500)));
        var world = new SimulationWorld(rules, setup, 1);
        var brain = new BotBrain(rules, 1, playMatch: true);
        Assert.DoesNotContain(world.GetView(1).Units, unit => unit.OwnerId < 0);
        SimCommand order = brain.Decide(world.GetView(1))!.Value;
        Assert.Equal(CommandKind.Rally, order.Kind);
        Assert.Equal((byte)1, order.TargetSquad);
        Assert.Equal(2, order.Count);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, order));
        world.Step();
        Assert.Equal(2, world.GetView(1).RallyingCount);
        Assert.Equal(2, world.GetView(1).GarrisonCount - world.GetView(1).RallyingCount);
        var restored = BotBrain.RestoreState(rules, JsonConvert.DeserializeObject<BotBrainState>(JsonConvert.SerializeObject(brain.CaptureState()))!);
        for (int tick = 0; tick < 40; tick++)
        {
            world.Step();
            Assert.Equal(brain.Decide(world.GetView(1)), restored.Decide(world.GetView(1)));
        }
    }

    [Fact]
    public void DenseScenarioAccountsForLiveAndReservedUnits()
    {
        var rules = GameRulesTests.Rules();
        var setup = ScenarioFactory.Create(rules, 15, Enum.Parse<ScenarioKind>("MainWorldDense"));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(1500, world.Metrics.PlayerUnits);
        Assert.Equal(2036, world.Metrics.NeutralUnits);
        Assert.Equal(15, setup.Players.Count);
        Assert.All(setup.Players, player => Assert.Equal(100, world.GetView(player.Id).Population));
        object state = typeof(SimulationWorld).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        MethodInfo reserve = state.GetType().GetMethod("TryReserveUnitSlots", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)reserve.Invoke(state, [560])!);
        Assert.False((bool)reserve.Invoke(state, [1])!);
        Assert.Equal(4096, setup.Units.Count + (int)state.GetType().GetProperty("ReservedUnitSlots", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!);
    }

    [Fact]
    public void ContinuousDoesNotPassAnOverBudgetMeasurement()
    {
        var result = new BenchmarkResult();
        typeof(BenchmarkResult).GetProperty(nameof(BenchmarkResult.Scenario))!.SetValue(result, ScenarioKind.Continuous);
        typeof(BenchmarkResult).GetProperty(nameof(BenchmarkResult.Statistics))!.SetValue(result, TickStatistics.Calculate([51], 1000));
        Assert.False(result.Passed);
    }

    [Fact]
    public void ActualOwnedAlarmRecallsAndRestoredBrainDoesNotInterruptTheCast()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 400)));
        setup.Players.Add(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(207, 200)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 400)));
        setup.Units.Add(new UnitSpawn(2, "fire", new Vector2(207, 200)));
        var world = new SimulationWorld(rules, setup, 1);
        world.Step();
        Assert.Contains(world.GetView(1).Events, item => item.Kind == SimEventKind.BaseUnderAttack && item.OwnerId == 1);
        var brain = new BotBrain(rules, 1, true);
        SimCommand recall = brain.Decide(world.GetView(1))!.Value;
        Assert.Equal(CommandKind.Recall, recall.Kind);
        Assert.Equal((byte)1, recall.TargetSquad);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, recall));
        var restored = BotBrain.RestoreState(rules, JsonConvert.DeserializeObject<BotBrainState>(JsonConvert.SerializeObject(brain.CaptureState()))!);
        for (int tick = 0; tick < 61; tick++)
        {
            world.Step();
            var command = brain.Decide(world.GetView(1));
            Assert.Equal(command, restored.Decide(world.GetView(1)));
            if (command.HasValue) Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, command.Value));
        }
        Assert.True(world.GetView(1).RecallReadyTick > world.GetView(1).Tick);
        Assert.InRange(Vector2.Distance(world.GetView(1).LegionPosition, new Vector2(200)), 0, rules.GarrisonRadius);
    }

    [Fact]
    public void ObservedRecoveryCandidateTakesPriorityOverUnrelatedVisibleCores()
    {
        var json = GameRulesTests.Data().Root;
        json["economy"]!["conversionChance"] = 1;
        var rules = new ElementalLegion.Shared.Data.GameRules(new ElementalLegion.Shared.Data.GameDataDocument(json.ToString()));
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(200)));
        setup.Players.Add(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(201, 200)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(201, 200), health: 1));
        setup.Cores.Add(new CoreSpawn(new Vector2(195, 200), 1));
        var world = new SimulationWorld(rules, setup, 1);
        world.Step();
        Assert.Equal(RecoveryRole.Secure, Assert.Single(world.GetView(1).Candidates).Role);
        SimCommand command = new BotBrain(rules, 1, true).Decide(world.GetView(1))!.Value;
        Assert.Equal(CommandKind.MoveDir, command.Kind);
        Assert.Equal(Vector2.Zero, command.Direction);
        Assert.Equal((byte)1, command.SquadMask);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, command));
    }

    [Theory]
    [InlineData("MainWorldDefense")]
    [InlineData("MainWorldRecovery")]
    public void SelectedRunnerActuallyRunsItsEcologyAndReportsSnapshotPreparation(string name)
    {
        using var output = new StringWriter();
        var kind = Enum.Parse<ScenarioKind>(name);
        var result = Assert.Single(BenchmarkRunner.Run(GameRulesTests.Rules(), 3, kind, output, CancellationToken.None, 0, 4));
        Assert.Equal(kind, result.Scenario);
        Assert.Equal(4, result.EcologyTicks);
        Assert.Equal(2, result.SnapshotSamples);
        Assert.True(result.SnapshotAllocatedBytes > 0);
        Assert.Contains("replication/send-queue=not measured", output.ToString());
        if (name == "MainWorldRecovery") Assert.Equal(4, result.GhostTicks);
    }

    [Fact]
    public void SelectedDenseRunnerMeasuresAllThreeLoadsAndKeepsFixedSampleStartsFull()
    {
        using var output = new StringWriter();
        var results = BenchmarkRunner.Run(GameRulesTests.Rules(), 15, Enum.Parse<ScenarioKind>("MainWorldDense"), output, CancellationToken.None, 0, 4);
        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { ScenarioKind.Distributed, ScenarioKind.Clustered, ScenarioKind.Continuous }, results.Select(result => result.Layout));
        Assert.All(results, result => Assert.Equal(3536, result.InitialPlayerUnits + result.InitialNeutralUnits));
        Assert.All(results.Take(2), result => Assert.True(result.RequiredLoadMaintained));
        Assert.All(results, result => Assert.InRange(result.MaxLiveAndReserved, 0, 4096));
        Assert.All(results, result => Assert.Equal(3536, result.MaxLiveAndReserved));
        Assert.All(results, result => Assert.Equal(0, result.EcologyTicks));
    }

    [Fact]
    public void RecoveryComparisonContainsFortyAgainstEightyAndActuallyCreatesCandidates()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, ScenarioFactory.CreateRecoveryBattle(rules), 1);
        Assert.Equal(40, world.GetView(1).Population);
        Assert.Equal(80, world.GetView(2).Population);
        Assert.Equal(120, world.Metrics.PlayerUnits);
        bool candidate = false;
        for (int tick = 0; tick < 600; tick++) { world.Step(); candidate |= world.HasConversionCandidates; }
        Assert.True(candidate);
        Assert.True(world.Metrics.Attacks > 0);
    }

    [Theory]
    [InlineData("DefenseUntilTick", -1)]
    [InlineData("DestinationSquad", 0)]
    [InlineData("DestinationSquad", 4)]
    [InlineData("CandidateId", -1)]
    public void MalformedNewBrainStateIsRejectedBeforeRestore(string field, int value)
    {
        var rules = GameRulesTests.Rules();
        var snapshot = JObject.FromObject(new BotBrain(rules, 1, true).CaptureState());
        snapshot[field] = value;
        Assert.Throws<ArgumentException>(() => BotBrain.RestoreState(rules, snapshot.ToObject<BotBrainState>()!));
    }

    [Fact]
    public void OwnedAlarmChoosesAnOccupiedFieldSquadAfterTheFirstSquadWasTransferred()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 220)));
        setup.Players.Add(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(207, 200)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 220)));
        setup.Units.Add(new UnitSpawn(2, "fire", new Vector2(207, 200)));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Transfer(1, 1, 2, 1)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.MoveTo(2, 2, new Vector2(200, 400))));
        for (int tick = 0; tick < 100; tick++) world.Step();
        Assert.Equal(0, world.GetView(1).Squads[0].JoinedCount);
        Assert.Equal(1, world.GetView(1).Squads[1].JoinedCount);
        // 억제 주기가 끝난 실제 다음 피격 경보를 기다린다.
        for (int tick = 0; tick < 100 && !world.GetView(1).Events.Any(item => item.Kind == SimEventKind.BaseUnderAttack); tick++) world.Step();
        var takeover = JObject.FromObject(new BotBrain(rules, 1, true).CaptureState());
        takeover["Sequence"] = 2UL;
        var brain = BotBrain.RestoreState(rules, takeover.ToObject<BotBrainState>()!);
        SimCommand command = brain.Decide(world.GetView(1))!.Value;
        Assert.Equal(CommandKind.Recall, command.Kind);
        Assert.Equal((byte)2, command.TargetSquad);
        var restored = BotBrain.RestoreState(rules, JObject.FromObject(brain.CaptureState()).ToObject<BotBrainState>()!);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, command));
        for (int tick = 0; tick < 60; tick++)
        {
            world.Step();
            Assert.Equal(brain.Decide(world.GetView(1)), restored.Decide(world.GetView(1)));
        }
    }

    [Fact]
    public void FortyAgainstEightySeparatesPursuitFromSecuringWithTheSameSeed()
    {
        var rules = GameRulesTests.Rules();
        var common = new SimulationWorld(rules, ScenarioFactory.CreateRecoveryBattle(rules), 1);
        for (int tick = 0; tick < 600 && !common.HasConversionCandidates; tick++) common.Step();
        Assert.True(common.HasConversionCandidates);
        int securingOwner = common.GetView(1).Candidates.Any(candidate => candidate.Role == RecoveryRole.Secure) ? 1 : 2;
        var snapshot = common.CaptureMigrationState();
        var secure = SimulationWorld.RestoreMigrationState(rules, snapshot);
        var pursue = SimulationWorld.RestoreMigrationState(rules, snapshot);
        Assert.Equal(secure.Metrics.PlayerUnits, pursue.Metrics.PlayerUnits);
        Assert.Equal(CommandAdmission.Accepted, secure.TryEnqueue(securingOwner, SimCommand.Move(1, Vector2.Zero)));
        Vector2 destination = common.GetView(securingOwner == 1 ? 2 : 1).BasePosition;
        Assert.Equal(CommandAdmission.Accepted, pursue.TryEnqueue(securingOwner, SimCommand.MoveTo(1, 1, destination)));
        int secureCandidates = 0, pursuitCandidates = 0;
        for (int tick = 0; tick < 400; tick++)
        {
            secure.Step(); pursue.Step();
            secureCandidates = Math.Max(secureCandidates, secure.Metrics.Candidates);
            pursuitCandidates = Math.Max(pursuitCandidates, pursue.Metrics.Candidates);
        }
        output.WriteLine($"seed=1; initial=40+80=120; forkTick={common.Metrics.Tick}; securingOwner={securingOwner}; afterForkTicks=400");
        output.WriteLine($"secure: attacks={secure.Metrics.Attacks-common.Metrics.Attacks}; candidatesPeak={secureCandidates}; conversions={secure.Metrics.Conversions-common.Metrics.Conversions}; remaining={secure.GetView(1).Population}+{secure.GetView(2).Population}");
        output.WriteLine($"pursue: attacks={pursue.Metrics.Attacks-common.Metrics.Attacks}; candidatesPeak={pursuitCandidates}; conversions={pursue.Metrics.Conversions-common.Metrics.Conversions}; remaining={pursue.GetView(1).Population}+{pursue.GetView(2).Population}");
        Assert.True(secureCandidates > 0 && pursuitCandidates > 0);
        Assert.True(secure.Metrics.Attacks > common.Metrics.Attacks);
        Assert.True(pursue.Metrics.Attacks > common.Metrics.Attacks);
        Assert.NotEqual(secure.GetView(securingOwner).LegionPosition, pursue.GetView(securingOwner).LegionPosition);
    }

    [Fact]
    public void EcologyActivityCountsActualRespawnsAndCoreSupplyAcrossMigration()
    {
        var json = GameRulesTests.Data().Root;
        json["neutral"]!["respawnSeconds"] = 0.15;
        var rules = new ElementalLegion.Shared.Data.GameRules(new ElementalLegion.Shared.Data.GameDataDocument(json.ToString()));
        var setup = ScenarioFactory.CreateMainWorld(rules, 1, ScenarioKind.MainWorldDefense, ScenarioKind.Continuous);
        var world = new SimulationWorld(rules, setup, 1);
        world.EnableEcology(rules.RoomMinBaseDistance);
        Assert.Equal(0, world.Metrics.EcologyRespawns);
        object state = typeof(SimulationWorld).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        var units = (System.Collections.IEnumerable)state.GetType().GetField("Units", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!;
        object neutral = units.Cast<object>().First(unit => (bool)unit.GetType().GetField("EcologyNeutral", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(unit)!);
        neutral.GetType().GetField("Dead", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(neutral, true);
        neutral.GetType().GetField("Health", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(neutral, 0f);
        for (int tick = 0; tick < 10 && world.Metrics.EcologyRespawns == 0; tick++) world.Step();
        Assert.Equal(1, world.Metrics.EcologyRespawns);
        Assert.True(world.Metrics.EcologyCoreSpawns > 0);
        var restored = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        Assert.Equal(world.Metrics.EcologyRespawns, restored.Metrics.EcologyRespawns);
        Assert.Equal(world.Metrics.EcologyCoreSpawns, restored.Metrics.EcologyCoreSpawns);
        var legacy = JObject.FromObject(world.CaptureMigrationState());
        ((JObject)legacy["Metrics"]!).Remove("EcologyRespawns");
        ((JObject)legacy["Metrics"]!).Remove("EcologyCoreSpawns");
        var legacyRestored = SimulationWorld.RestoreMigrationState(rules, legacy.ToObject<MigrationWorldState>()!);
        Assert.Equal(0, legacyRestored.Metrics.EcologyRespawns);
        Assert.Equal(0, legacyRestored.Metrics.EcologyCoreSpawns);
        var corrupted = JObject.FromObject(world.CaptureMigrationState());
        corrupted["Metrics"]!["EcologyRespawns"] = -1;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(rules, corrupted.ToObject<MigrationWorldState>()!));
    }

    [Fact]
    public void RestoredBrainKeepsItsObservedCandidateUntilItDisappears()
    {
        var json = GameRulesTests.Data().Root;
        json["economy"]!["conversionChance"] = 1;
        var rules = new ElementalLegion.Shared.Data.GameRules(new ElementalLegion.Shared.Data.GameDataDocument(json.ToString()));
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(200)));
        setup.Players.Add(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(203, 200)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(204, 200)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(201, 200), health: 1));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(205, 200), health: 1));
        var world = new SimulationWorld(rules, setup, 1);
        for (int tick = 0; tick < 30 && world.GetView(1).Candidates.Count < 2; tick++) world.Step();
        Assert.Equal(2, world.GetView(1).Candidates.Count);
        var brain = new BotBrain(rules, 1, true);
        Assert.Equal(CommandKind.MoveDir, brain.Decide(world.GetView(1))!.Value.Kind);
        var restored = BotBrain.RestoreState(rules, JObject.FromObject(brain.CaptureState()).ToObject<BotBrainState>()!);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 1, new Vector2(205, 200)));
        for (int tick = 0; tick < 12; tick++) world.Step();
        Assert.Equal(2, world.GetView(1).Candidates.Count);
        SimCommand retained = restored.Decide(world.GetView(1))!.Value;
        SimCommand nearest = new BotBrain(rules, 1, true).Decide(world.GetView(1))!.Value;
        Assert.True(retained.Direction.X < 0);
        Assert.Equal(Vector2.Zero, nearest.Direction);
        Assert.Equal(brain.Decide(world.GetView(1)), retained);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, retained));
    }
}
