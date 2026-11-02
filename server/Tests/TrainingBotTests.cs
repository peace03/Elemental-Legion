using System.Numerics;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>양성 예약·수비대·공용 봇과 시드 재현성을 확인한다.</summary>
public sealed class TrainingBotTests
{
    [Fact]
    public void TrainingReservesFundsAndPopulationThenCreatesGarrison()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.TryEnqueue(1, SimCommand.Train(2, "fire"));
        world.Step();
        Assert.Equal(92, world.GetView(1).CoreBalance);
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
        Assert.Contains(world.GetView(1).Events, item => item.Sequence == 2 && item.Detail == (int)CommandFailure.Busy);
        for (int i = 0; i < 159; i++) world.Step();
        Assert.True(world.GetView(1).IsTraining);
        world.Step();
        Assert.False(world.GetView(1).IsTraining);
        Assert.Equal(1, world.GetView(1).Population);
        Assert.True(Assert.Single(world.GetView(1).Units).Garrison);
    }

    [Theory]
    [InlineData(0, 4, "water", CommandFailure.InsufficientCores)]
    [InlineData(100, 1, "light", CommandFailure.NotUnlocked)]
    public void TrainingRejectionsPreserveFunds(int cores, int level, string element, CommandFailure reason)
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(cores: cores, level: level), 1);
        world.TryEnqueue(1, SimCommand.Train(1, element));
        world.Step();
        Assert.Equal(cores, world.GetView(1).CoreBalance);
        Assert.Equal(0, world.GetView(1).ReservedPopulation);
        Assert.Equal(reason, (CommandFailure)Assert.Single(world.GetView(1).Events).Detail);
    }

    [Fact]
    public void TrainingReservationPreventsConversionFromTakingItsSlot()
    {
        var setup = CombatTests.Battle(CombatTests.Unit(1, "water", 50), CombatTests.Unit(2, "earth", 51, hp: 1));
        setup.Players[0] = new PlayerSpawn(1, "water", new Vector2(300, 300), new Vector2(50, 50), 1, 100, true);
        for (int i = 0; i < 18; i++) setup.Units.Add(CombatTests.Unit(1, "earth", 500 + i, 500));
        var world = new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.Step();
        Assert.Equal(19, world.GetView(1).Population);
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
        Assert.Equal(0, world.Metrics.Conversions);
        Assert.Equal(1, world.Metrics.Drops);
    }

    [Fact]
    public void NearbyGarrisonJoinsAndRallyUsesItsConfiguredSpeed()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200, 200), new Vector2(200, 218)));
        setup.Units.Add(CombatTests.Unit(1, "earth", 200, 207.5f));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        Assert.False(Assert.Single(world.GetView(1).Units).Garrison);
        setup.Players[0] = new PlayerSpawn(1, "water", new Vector2(200, 200), new Vector2(300, 300));
        world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.TryEnqueue(1, SimCommand.Rally(1)); world.Step();
        var unit = Assert.Single(world.GetView(1).Units);
        Assert.Equal(7f * 1.3f * 0.05f, Vector2.Distance(new Vector2(200, 207.5f), unit.Position), 3);
        Assert.True(unit.Garrison);
    }

    [Fact]
    public void DistantRallyingGarrisonDoesNotJoinJustBecauseArmyReturnedToBase()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200, 200), new Vector2(200, 218)));
        setup.Units.Add(CombatTests.Unit(1, "earth", 500, 500));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        Assert.True(Assert.Single(world.GetView(1).Units).Garrison);
    }

    [Fact]
    public void BotUsesOnlyItsViewAndNormalCommandAdmission()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Units.Add(CombatTests.Unit(1, "water", 50, garrison: false));
        setup.Units.Add(CombatTests.Unit(-1, "earth", 60));
        var world = new SimulationWorld(rules, setup, 1);
        var bot = new BotBrain(rules, 1);
        SimCommand command = bot.Decide(world.GetView(1))!.Value;
        Assert.Equal(CommandKind.MoveDir, command.Kind);
        Assert.True(command.Direction.X > 0);
        Assert.Null(bot.Decide(world.GetView(1)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, command));
    }

    [Fact]
    public void SameSeedAndInputsProduceSameObservableStateAndEvents()
    {
        var rules = GameRulesTests.Rules();
        var setup = CombatTests.Battle(CombatTests.Unit(1, "water", 50, garrison: false), CombatTests.Unit(1, "lightning", 50, garrison: false), CombatTests.Unit(2, "earth", 53));
        var first = new SimulationWorld(rules, setup, 42);
        var second = new SimulationWorld(rules, setup, 42);
        for (int tick = 0; tick < 300; tick++)
        {
            first.Step(); second.Step();
            Assert.Equal(first.GetView(1).Units.ToArray(), second.GetView(1).Units.ToArray());
            Assert.Equal(first.GetView(1).Events.ToArray(), second.GetView(1).Events.ToArray());
            Assert.Equal(first.GetView(1).CoreBalance, second.GetView(1).CoreBalance);
        }
    }
}
