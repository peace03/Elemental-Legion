using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>승계가 명령·진행 타이머와 난수 결과를 그대로 이어 가는지 확인한다.</summary>
public sealed class MigrationStateTests
{
    [Fact]
    public void MigrationKeepsTrainingMovementAndQueuedCommandsWithoutRefundsOrDuplicates()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup(cores: 1000);
        setup.Units.Add(new UnitSpawn(1, "water", setup.Players[0].LegionPosition));
        var original = new SimulationWorld(rules, setup, 17);
        original.TryEnqueue(1, SimCommand.Train(1, "earth"));
        original.TryEnqueue(1, SimCommand.Move(2, Vector2.UnitX));
        original.Step();
        original.TryEnqueue(1, SimCommand.Train(3, "water"));
        original.TryEnqueue(1, SimCommand.Rally(4));
        original.TryEnqueue(1, SimCommand.Build(5, BuildingKind.Collector, 200, 216));
        var resumed = RoundTrip(rules, original);

        Assert.Equal(original.GetView(1).CoreBalance, resumed.GetView(1).CoreBalance);
        Assert.Equal(original.GetView(1).TrainingEndTick, resumed.GetView(1).TrainingEndTick);
        Assert.Equal(CommandAdmission.StaleSequence, resumed.TryEnqueue(1, SimCommand.Train(3, "water")));
        for (int tick = 0; tick < rules.TickRate * 18; tick++)
        {
            original.Step();
            resumed.Step();
            AssertSameState(original, resumed);
            Assert.Equal(JsonConvert.SerializeObject(original.GetView(1).Events), JsonConvert.SerializeObject(resumed.GetView(1).Events));
        }
        Assert.Equal(2, resumed.Metrics.Trained);
    }

    [Fact]
    public void MigrationPreservesCombatEffectsConversionRandomnessAndEcology()
    {
        var rules = GameRulesTests.Rules().ForRoom(2);
        var world = new SimulationWorld(rules, new WorldSetup(), 41);
        world.EnableEcology(rules.RoomMinBaseDistance);
        Assert.True(world.TryChooseSpawn(out var home, out var center));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "fire", home, center, 4, 1000, true, true)));
        Assert.True(world.TryChooseSpawn(out home, out center));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "earth", home, center, 4, 1000, true, true)));
        var first = new BotBrain(rules, 1, playMatch: true);
        var second = new BotBrain(rules, 2, playMatch: true);
        for (int tick = 0; tick < 121; tick++) StepBots(world, first, second);
        var restored = RoundTrip(rules, world);
        var copiedFirst = BotBrain.RestoreState(rules, JsonConvert.DeserializeObject<BotBrainState>(JsonConvert.SerializeObject(first.CaptureState()))!);
        var copiedSecond = BotBrain.RestoreState(rules, JsonConvert.DeserializeObject<BotBrainState>(JsonConvert.SerializeObject(second.CaptureState()))!);

        for (int tick = 0; tick < 650; tick++)
        {
            StepBots(world, first, second);
            StepBots(restored, copiedFirst, copiedSecond);
            AssertSameState(world, restored);
        }
    }

    [Fact]
    public void MigrationRejectsTamperedRuntimeStateAndKeepsSourceIndependent()
    {
        var rules = GameRulesTests.Rules();
        var original = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        original.Step();
        var captured = original.CaptureMigrationState();
        string before = JsonConvert.SerializeObject(captured);
        original.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX));
        original.Step();
        Assert.Equal(before, JsonConvert.SerializeObject(captured));
        var json = JObject.Parse(before);
        json["Players"]![0]!["Direction"]!["X"] = float.NaN;
        var invalid = json.ToObject<MigrationWorldState>()!;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(rules, invalid));
    }

    [Fact]
    public void MigrationKeepsCameraVisibilityByDefault()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.CameraVisibility = true;
        Assert.True(rules.CameraVisionHalfSize.X > rules.VisionRadius);
        float distance = (rules.CameraVisionHalfSize.X + rules.VisionRadius) / 2;
        setup.Units.Add(new UnitSpawn(1, "water", setup.Players[0].LegionPosition));
        setup.Units.Add(new UnitSpawn(-1, "water", setup.Players[0].LegionPosition + Vector2.UnitX * distance));
        var original = new SimulationWorld(rules, setup, 7);
        Assert.Single(original.GetView(1).Units, unit => unit.OwnerId < 0);

        var restored = RoundTrip(rules, original);

        Assert.Equal(original.GetView(1).Units.Select(unit => unit.Id), restored.GetView(1).Units.Select(unit => unit.Id));
    }

    [Fact]
    public void MigrationKeepsActiveConstructionGhostsAndDoesNotReplayOldEvents()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(800), new Vector2(810)));
        var original = new SimulationWorld(rules, setup, 7);
        original.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 200, 216));
        original.Step();
        Assert.Contains(original.GetView(1).Buildings, building => building.Kind == BuildingKind.Collector && building.IsUnderConstruction);
        Assert.True(original.BeginGhost(2));
        var restored = RoundTrip(rules, original);

        Assert.True(restored.HasGhosts);
        Assert.Empty(restored.GetView(1).Events);
        AssertSameState(original, restored);
        for (int tick = 0; tick < rules.TickRate * 6; tick++)
        {
            original.Step();
            restored.Step();
            AssertSameState(original, restored);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-17)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void StatefulRandomKeepsSystemRandomSeededSequenceAndResumesIt(int seed)
    {
        var type = typeof(SimulationWorld).Assembly.GetType("ElementalLegion.Shared.Sim.StatefulRandom", true)!;
        var actual = (Random)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new object[] { seed }, null)!;
        var expected = new Random(seed);
        for (int index = 0; index < 1000; index++)
        {
            Assert.Equal(expected.Next(), actual.Next());
            Assert.Equal(expected.Next(0), actual.Next(0));
            Assert.Equal(expected.Next(17), actual.Next(17));
            Assert.Equal(expected.Next(-50, 150), actual.Next(-50, 150));
            Assert.Equal(expected.Next(int.MinValue, int.MaxValue), actual.Next(int.MinValue, int.MaxValue));
            Assert.Equal(expected.NextDouble(), actual.NextDouble());
            var bytes = new byte[7];
            var compared = new byte[7];
            expected.NextBytes(bytes);
            actual.NextBytes(compared);
            Assert.Equal(bytes, compared);
        }
    }

    private static SimulationWorld RoundTrip(GameRules rules, SimulationWorld world) =>
        SimulationWorld.RestoreMigrationState(rules,
            JsonConvert.DeserializeObject<MigrationWorldState>(JsonConvert.SerializeObject(world.CaptureMigrationState()))!);

    private static void AssertSameState(SimulationWorld first, SimulationWorld second) =>
        Assert.Equal(JsonConvert.SerializeObject(first.CaptureMigrationState()), JsonConvert.SerializeObject(second.CaptureMigrationState()));

    private static void StepBots(SimulationWorld world, params BotBrain[] bots)
    {
        foreach (var bot in bots)
        {
            int owner = bot.CaptureState().PlayerId;
            if (world.TryGetView(owner, out var view) && bot.Decide(view) is SimCommand command) world.TryEnqueue(owner, command);
        }
        world.Step();
    }
}
