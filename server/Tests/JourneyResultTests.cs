using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>확정 결과, 확인 명령과 저장 경계를 실제 월드 틱으로 확인한다.</summary>
public sealed class JourneyResultTests
{
    [Fact]
    public void CandidateCreationIsNotConversionAndCompletionCountsOnce()
    {
        var world = ConversionRecoveryTests.Create();
        world.Step();
        Assert.Equal(1, world.GetView(1).Journey.Id);
        Assert.Equal(0, world.GetView(1).Journey.Total.Converted);
        Assert.Equal(1, world.GetView(2).Journey.Total.Lost);
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.Equal(1, world.GetView(1).Journey.Total.Converted);
        Assert.Equal(0, world.GetView(1).Journey.Total.Rescued);
        world.Step();
        Assert.Equal(1, world.GetView(1).Journey.Total.Converted);
    }

    [Fact]
    public void NeutralConversionIsImmediateAndOwnerOnly()
    {
        var world = ConversionRecoveryTests.Create(neutral: true); world.Step();
        Assert.Equal(1, world.GetView(1).Journey.Total.Converted);
        Assert.Equal(0, world.GetView(2).Journey.Total.Converted);
        Assert.Equal(0, world.GetView(1).Journey.Total.Lost);
        Assert.Equal(0, world.GetView(2).Journey.Total.Lost);
    }

    [Fact]
    public void RescueRecordsTheOriginalDeathAndSeparateGainOnlyOnce()
    {
        var rules = ReactionEconomyTests.CertainConversion();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(300), new Vector2(100), 4));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720), 4));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(101, 100), health: 1));
        setup.Units.Add(new UnitSpawn(2, "life", new Vector2(720)));
        var world = new SimulationWorld(rules, setup, 1); world.Step();
        ConversionRecoveryTests.Move(world, 1, new Vector2(200));
        ConversionRecoveryTests.Move(world, 3, new Vector2(102, 100));
        world = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.False(world.HasConversionCandidates);
        Assert.Equal(1, world.GetView(2).Journey.Total.Lost);
        Assert.Equal(1, world.GetView(2).Journey.Total.Rescued);
        Assert.Equal(0, world.GetView(1).Journey.Total.Converted);
        world.Step();
        Assert.Equal(1, world.GetView(2).Journey.Total.Rescued);
        Assert.Equal(2, world.GetView(2).Population);
    }

    [Fact]
    public void CandidateExpiryDoesNotAddAnotherLossOrGain()
    {
        var world = ConversionRecoveryTests.Create(); world.Step();
        ConversionRecoveryTests.Move(world, 1, new Vector2(200));
        for (int tick = 0; tick < 401; tick++) world.Step();
        Assert.False(world.HasConversionCandidates);
        Assert.Equal(1, world.GetView(2).Journey.Total.Lost);
        Assert.Equal(0, world.GetView(1).Journey.Total.Converted);
        Assert.Equal(0, world.GetView(2).Journey.Total.Rescued);
    }

    [Fact]
    public void StartingCombatDeathRecordsLossAndCoreDrop()
    {
        var data = GameRulesTests.Data().Root;
        data["economy"]!["conversionChance"] = 0;
        data["elements"]!.Single(element => (string?)element["id"] == "water")["hp"] = 1;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(2, "fire", new Vector2(80, 102), new Vector2(80, 82)));
        setup.Units.Add(new UnitSpawn(2, "light", new Vector2(80, 82), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(80))));
        world.Step();
        Assert.True(world.GetView(1).Journey.Total.Lost > 0);
        Assert.Equal(0, world.GetView(2).Journey.Total.Converted);
        Assert.Equal(0, world.GetView(2).Journey.Total.CoresIn);
        Assert.True(world.Metrics.Drops > 0);
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(true, 0)]
    public void SiegeVictoryKeepsExistingGhostPolicyAndDestroyedFacilitiesCountOnce(bool ghost, int wins)
    {
        var data = GameRulesTests.Data().Root;
        data["buildings"]!["base"]!["hp"] = 2;
        data["buildings"]!["dome"]!["hp"] = 3;
        data["buildings"]!["dome"]!["buildSeconds"] = 0;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        var edge = new Vector2(207, 200);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(400), edge));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200), new Vector2(200, 230), 4, 100));
        setup.Units.Add(new UnitSpawn(1, "earth", edge));
        setup.Units.Add(new UnitSpawn(1, "earth", edge));
        setup.Units.Add(new UnitSpawn(2, "water", new Vector2(200, 230), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        var final = world.GetView(2);
        if (ghost) Assert.True(world.BeginGhost(2));
        else Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.Build(1, BuildingKind.Dome, 0, 0)));
        world.Step();
        Assert.False(world.TryGetView(2, out _));
        Assert.Equal(1, final.Journey.Total.Lost);
        Assert.Equal(wins, world.GetView(1).Journey.Total.SiegeWins);
        Assert.Equal(ghost ? 0 : 1, world.GetView(1).Journey.Total.BuildingsDestroyed);
        world.Step();
        Assert.Equal(wins, world.GetView(1).Journey.Total.SiegeWins);
        Assert.Equal(ghost ? 0 : 1, world.GetView(1).Journey.Total.BuildingsDestroyed);
    }

    [Fact]
    public void GhostExpiryKeepsFinalLiveLossWithoutAwardingSiegeVictory()
    {
        var data = GameRulesTests.Data().Root;
        data["ghost"]!["decaySeconds"] = 0.05;
        data["ghost"]!["reconnectWindowSeconds"] = 0.05;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220))));
        var final = world.GetView(1);
        Assert.True(world.BeginGhost(1)); world.Step();
        Assert.False(world.TryGetView(1, out _));
        Assert.Equal(12, final.Journey.Total.Lost);
        Assert.Equal(0, final.Journey.Total.SiegeWins);
        world.Step(); Assert.Equal(12, final.Journey.Total.Lost);
    }

    [Fact]
    public void AcknowledgementUsesExecutionTimeAndKeepsLaterResults()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(50)));
        setup.Cores.Add(new CoreSpawn(new Vector2(50), 5));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.AcknowledgeJourney(1)));
        Assert.Equal(CommandAdmission.StaleSequence, world.TryEnqueue(1, SimCommand.AcknowledgeJourney(1)));
        world.Step();
        Assert.Equal(105, world.GetView(1).Journey.Total.CoresIn);
        Assert.Equal(5, world.GetView(1).Journey.SinceLast.CoresIn);
        Assert.Contains(world.GetView(1).Events, item => item.Kind == SimEventKind.CommandApplied && item.Detail == 10 && item.Sequence == 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.AcknowledgeJourney(2)));
        world.Step();
        Assert.Equal(0, world.GetView(1).Journey.SinceLast.CoresIn);
        Assert.Equal(105, world.GetView(1).Journey.Total.CoresIn);
    }

    [Fact]
    public void PendingAcknowledgementSurvivesMigrationWithSequenceAndRateLimits()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        Assert.Equal(CommandAdmission.UnknownPlayer, world.TryEnqueue(99, SimCommand.AcknowledgeJourney(1)));
        Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.AcknowledgeJourney(0)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.AcknowledgeJourney(1)));
        var restored = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        Assert.Equal(CommandAdmission.StaleSequence, restored.TryEnqueue(1, SimCommand.AcknowledgeJourney(1)));
        world.Step(); restored.Step();
        Assert.Equal(0, restored.GetView(1).Journey.SinceLast.CoresIn);
        Assert.True(JToken.DeepEquals(JObject.FromObject(world.CaptureMigrationState()), JObject.FromObject(restored.CaptureMigrationState())));
        for (ulong sequence = 2; sequence <= (ulong)rules.CommandRateLimit; sequence++)
            Assert.Equal(CommandAdmission.Accepted, restored.TryEnqueue(1, SimCommand.AcknowledgeJourney(sequence)));
        Assert.Equal(CommandAdmission.RateLimited, restored.TryEnqueue(1, SimCommand.AcknowledgeJourney((ulong)rules.CommandRateLimit + 1)));
    }

    [Fact]
    public void ActualTrainingPaymentsRefundsAndRejectedCommandsAreCounted()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 2, 4)); world.Step();
        Assert.Equal(16, world.GetView(1).Journey.Total.CoresSpent);
        world.TryEnqueue(1, SimCommand.CancelTraining(2, 2));
        world.TryEnqueue(1, SimCommand.CancelTraining(3, 2)); world.Step();
        Assert.Equal(108, world.GetView(1).Journey.Total.CoresIn);
        Assert.Equal(16, world.GetView(1).Journey.Total.CoresSpent);
        Assert.True(world.BeginGhost(1));
        Assert.Equal(116, world.GetView(1).Journey.Total.CoresIn);
        Assert.Equal(100, world.GetView(1).CoreBalance);
    }

    [Fact]
    public void ConstructionAndUpgradePaymentsAndDiskRefundAreCounted()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 225), cores: 300));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 215, 200));
        world.TryEnqueue(1, SimCommand.UpgradeBase(2)); world.Step();
        long spent = 20 + rules.UpgradeCost(2);
        Assert.Equal(spent, world.GetView(1).Journey.Total.CoresSpent);
        var restored = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        Assert.Equal(320, restored.GetView(1).Journey.Total.CoresIn);
        Assert.Equal(spent, restored.GetView(1).Journey.Total.CoresSpent);
        Assert.True(restored.TryResumeGhost(1));
        restored = SimulationWorld.RestoreCheckpoint(rules, restored.CaptureCheckpoint());
        Assert.Equal(320, restored.GetView(1).Journey.Total.CoresIn);
    }

    [Fact]
    public void ResumeKeepsJourneyAndNewAdmissionStartsAnother()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.AcknowledgeJourney(1)); world.Step();
        Assert.True(world.BeginGhost(1)); Assert.True(world.TryResumeGhost(1));
        Assert.Equal(1, world.GetView(1).Journey.Id);
        Assert.Equal(100, world.GetView(1).Journey.Total.CoresIn);
        Assert.Equal(0, world.GetView(1).Journey.SinceLast.CoresIn);
        var restored = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        Assert.True(restored.TryResumeGhost(1));
        Assert.Equal(world.GetView(1).Journey, restored.GetView(1).Journey);
        Assert.True(restored.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(620))));
        Assert.Equal(2, restored.GetView(2).Journey.Id);
        Assert.Equal(default, restored.GetView(2).Journey.Total);
    }

    [Fact]
    public void RemovedOwnerViewKeepsFinalLossesAndRefund()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(), 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(620), cores: 100, natureForge: true)));
        var final = world.GetView(2);
        world.TryEnqueue(2, SimCommand.Train(1, "fire")); world.Step();
        Set(Player(world, 2), "BaseDestroyed", true); world.Step();
        Assert.False(world.TryGetView(2, out _));
        Assert.Equal(12, final.Journey.Total.Lost);
        Assert.Equal(108, final.Journey.Total.CoresIn);
        Assert.Equal(8, final.Journey.Total.CoresSpent);
        Assert.Equal(2, final.Journey.Id);
        Assert.Equal(0, world.GetView(1).Journey.Total.Lost);
    }

    [Theory]
    [InlineData("Converted")] [InlineData("Rescued")] [InlineData("Lost")] [InlineData("CoresIn")]
    [InlineData("CoresSpent")] [InlineData("BuildingsDestroyed")] [InlineData("SiegeWins")]
    public void CheckpointRequiresAllTotalsAndRejectsNegativeOrExcessAcknowledgement(string field)
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var json = JObject.FromObject(world.CaptureCheckpoint());
        foreach (string name in new[] { "JourneyTotal", "JourneyAcknowledged" })
        {
            Assert.NotNull(json["Players"]![0]![name]);
            var corrupt = (JObject)json.DeepClone();
            ((JObject)corrupt["Players"]![0]![name]!).Remove(field);
            Assert.Throws<JsonSerializationException>(() => corrupt.ToObject<WorldCheckpoint>());
            corrupt = (JObject)json.DeepClone(); corrupt["Players"]![0]![name]![field] = -1;
            Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, corrupt.ToObject<WorldCheckpoint>()!));
        }
        json["Players"]![0]!["JourneyAcknowledged"]![field] = long.MaxValue;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, json.ToObject<WorldCheckpoint>()!));
    }

    [Theory]
    [InlineData("JourneyTotal")] [InlineData("JourneyAcknowledged")]
    public void CheckpointRejectsMissingOrNullAggregate(string field)
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(), 1);
        var json = JObject.FromObject(world.CaptureCheckpoint());
        ((JObject)json["Players"]![0]!).Remove(field);
        Assert.Throws<JsonSerializationException>(() => json.ToObject<WorldCheckpoint>());
        json["Players"]![0]![field] = JValue.CreateNull();
        Assert.Throws<JsonSerializationException>(() => json.ToObject<WorldCheckpoint>());
    }

    [Theory]
    [InlineData(100L, 103L)] [InlineData(long.MaxValue - 1, long.MaxValue)]
    public void CountersSaturateAndRefundOnlyCountsBalanceIncrease(long initialIncome, long expectedIncome)
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 2, 4)); world.Step();
        var json = JObject.FromObject(world.CaptureMigrationState());
        Assert.NotNull(json["World"]!["Players"]![0]!["JourneyTotal"]);
        json["World"]!["Players"]![0]!["Cores"] = long.MaxValue - 3;
        json["World"]!["Players"]![0]!["JourneyTotal"]!["CoresIn"] = initialIncome;
        json["World"]!["Players"]![0]!["JourneyTotal"]!["CoresSpent"] = long.MaxValue - 1;
        world = SimulationWorld.RestoreMigrationState(rules, json.ToObject<MigrationWorldState>()!);
        world.TryEnqueue(1, SimCommand.CancelTraining(2, 2)); world.Step();
        Assert.Equal(expectedIncome, world.GetView(1).Journey.Total.CoresIn);
        Assert.Equal(long.MaxValue, world.GetView(1).CoreBalance);
        world.TryEnqueue(1, SimCommand.Train(3, "water")); world.Step();
        Assert.Equal(long.MaxValue, world.GetView(1).Journey.Total.CoresSpent);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void LegacyAcquisitionTotalSaturatesWithoutBlockingNeutralOrCandidateConversion(bool neutral)
    {
        var world = ConversionRecoveryTests.Create(neutral);
        Set(Player(world, 1), "ConvertedCount", long.MaxValue - 5);
        Set(Player(world, 1), "TrainedCount", 5L);
        Set(Player(world, 1), "JourneyTotal", new JourneyTotals(long.MaxValue, 0, 0, 100, 0, 0, 0));
        world.Step();
        if (!neutral) for (int tick = 0; tick < 60; tick++) world.Step();
        var view = world.GetView(1);
        Assert.Equal(2, view.Population);
        Assert.Equal(long.MaxValue - 5, view.ConvertedCount);
        Assert.Equal(5, view.TrainedCount);
        Assert.Equal(long.MaxValue, view.Journey.Total.Converted);
        AssertValidSaveAndGameplay(world, ReactionEconomyTests.CertainConversion());
    }

    [Fact]
    public void LegacyAcquisitionTotalSaturatesWithoutBlockingPaidTraining()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        Set(Player(world, 1), "ConvertedCount", long.MaxValue);
        world.TryEnqueue(1, SimCommand.Train(1, "water")); world.Step();
        while (world.GetView(1).IsTraining) world.Step();
        Assert.Equal(1, world.GetView(1).Population);
        Assert.Equal(0, world.GetView(1).TrainedCount);
        Assert.Equal(long.MaxValue, world.GetView(1).ConvertedCount);
        AssertValidSaveAndGameplay(world, rules);
    }

    [Fact]
    public void CheckpointRejectsOverflowingLegacyAcquisitionTotal()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var json = JObject.FromObject(world.CaptureCheckpoint());
        json["Players"]![0]!["Converted"] = long.MaxValue;
        json["Players"]![0]!["Trained"] = 1;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, json.ToObject<WorldCheckpoint>()!));
    }

    [Fact]
    public void StandingReflectsCurrentForceOnly()
    {
        var world = ConversionRecoveryTests.Create(); world.Step();
        Set(Player(world, 1), "BattleWins", 50);
        world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 300, 315)); world.Step();
        Assert.Equal(112, world.GetView(1).Standings.Single(item => item.PlayerId == 1).Score);
        long due = world.GetView(1).Buildings.Single(item => item.Kind == BuildingKind.Collector).ConstructionEndTick;
        while (world.GetView(1).Tick < due) world.Step();
        Assert.Equal(124, world.GetView(1).Standings.Single(item => item.PlayerId == 1).Score);
    }

    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Player(SimulationWorld world, int id)
    {
        object state = typeof(SimulationWorld).GetField("state", Internal)!.GetValue(world)!;
        return ((System.Collections.IEnumerable)state.GetType().GetField("Players", Internal)!.GetValue(state)!).Cast<object>().Single(player => (int)player.GetType().GetField("Id", Internal)!.GetValue(player)! == id);
    }
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Internal)!.SetValue(target, value);
    private static void AssertValidSaveAndGameplay(SimulationWorld world, GameRules rules)
    {
        var restored = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        var disk = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        Assert.Equal(world.GetView(1).Journey, restored.GetView(1).Journey);
        Assert.Equal(world.GetView(1).Journey, disk.GetView(1).Journey);
        var writer = new NetDataWriter();
        GameplayCodec.WriteBody(writer, restored.GetView(1), Array.Empty<NetworkEvent>(), 0);
        Assert.True(GameplayCodec.ReadBody(new NetDataReader(writer.CopyData()), rules, new GameplayFrame(rules), world.GetView(1).Tick, 1, 1));
    }
}
