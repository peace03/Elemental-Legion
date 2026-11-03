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
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var view = world.GetView(1);
        int water = rules.ElementIndex("water"), fire = rules.ElementIndex("fire"), light = rules.ElementIndex("light");
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.TryEnqueue(1, SimCommand.Train(2, "fire"));
        world.TryEnqueue(1, SimCommand.Train(3, "light"));
        world.Step();
        Assert.Equal(65, view.CoreBalance);
        Assert.Equal(3, view.ReservedPopulation);
        Assert.Equal(water, view.TrainingElement);
        Assert.Equal(new[] { fire, light }, view.TrainingQueue.Select(order => order.Element));
        Assert.Equal(1 + 6 * rules.TickRate, view.TrainingEndTick);
        while (view.Tick < view.TrainingEndTick - 1) world.Step();
        Assert.Equal(0, view.Population);
        world.Step();
        Assert.Equal(fire, view.TrainingElement);
        Assert.Equal(2, view.ReservedPopulation);
        Assert.Equal(8 * rules.TickRate, view.TrainingEndTick - view.TrainingStartTick);
        while (view.IsTraining) world.Step();
        Assert.Equal(65, view.CoreBalance);
        Assert.Equal(0, view.ReservedPopulation);
        Assert.Equal(new[] { water, fire, light }, view.Units.Select(unit => unit.Element));
        Assert.All(view.Units, unit => Assert.True(unit.Garrison));
    }

    [Fact]
    public void CancelRefundsOnlyTheSelectedWaitingOrder()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var view = world.GetView(1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.TryEnqueue(1, SimCommand.Train(2, "fire"));
        world.TryEnqueue(1, SimCommand.Train(3, "fire"));
        world.Step();
        long due = view.TrainingEndTick;

        world.TryEnqueue(1, SimCommand.CancelTraining(4, 3));
        world.TryEnqueue(1, SimCommand.CancelTraining(5, 3));
        world.TryEnqueue(1, SimCommand.CancelTraining(6, 1));
        world.Step();
        Assert.Equal(82, view.CoreBalance);
        Assert.Equal(2, view.ReservedPopulation);
        Assert.Equal(2UL, Assert.Single(view.TrainingQueue).Id);
        Assert.Equal(due, view.TrainingEndTick);
        Assert.Equal(2, view.Events.Count(e => e.Kind == SimEventKind.CommandRejected &&
            e.Detail == (int)CommandFailure.TrainingNotWaiting));
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
        for (int i = 0; i < 17; i++) setup.Units.Add(CombatTests.Unit(1, "earth", 500 + i, 500));
        var world = new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.TryEnqueue(1, SimCommand.Train(2, "fire"));
        world.TryEnqueue(1, SimCommand.Train(3, "earth"));
        world.Step();
        var view = world.GetView(1);
        Assert.Equal(18, view.Population);
        Assert.Equal(2, view.ReservedPopulation);
        Assert.Equal(87, view.CoreBalance);
        Assert.Contains(view.Events, e => e.Sequence == 3 && e.Detail == (int)CommandFailure.PopulationCap);
        Assert.Equal(0, world.Metrics.Conversions);
        Assert.Equal(1, world.Metrics.Drops);
        Assert.Equal(1, world.Metrics.Pickups);
        Assert.True(world.BeginGhost(1));
        Assert.Equal(105, view.CoreBalance);
        Assert.Equal(0, view.ReservedPopulation);
        Assert.False(view.IsTraining);
        Assert.Empty(view.TrainingQueue);
    }

    [Fact]
    public void NearbyGarrisonWaitsForRallyThenMovesAtItsConfiguredSpeed()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200, 200), new Vector2(200, 218)));
        setup.Units.Add(CombatTests.Unit(1, "earth", 200, 207.5f));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        Assert.True(Assert.Single(world.GetView(1).Units).Garrison);
        Assert.False(world.GetView(1).IsRallying);
        world.TryEnqueue(1, SimCommand.Rally(1)); world.Step();
        var unit = Assert.Single(world.GetView(1).Units);
        Assert.Equal(7f * 1.3f * 0.05f, Vector2.Distance(new Vector2(200, 207.5f), unit.Position), 3);
        Assert.True(unit.Garrison);
        Assert.True(world.GetView(1).IsRallying);
        for (int tick = 0; tick < 40 && world.GetView(1).IsRallying; tick++) world.Step();
        Assert.False(Assert.Single(world.GetView(1).Units).Garrison);
        Assert.Equal(1, world.GetView(1).GarrisonArrivals);
    }

    [Fact]
    public void DistantRallyingGarrisonWaitsUntilItReachesTheLegion()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200, 200), new Vector2(200, 218)));
        setup.Units.Add(CombatTests.Unit(1, "earth", 500, 500));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.TryEnqueue(1, SimCommand.Rally(1));
        world.Step();
        Assert.True(Assert.Single(world.GetView(1).Units).Garrison);
        Assert.True(world.GetView(1).IsRallying);
    }

    [Fact]
    public void TrainingPlacesNewReservesInFreeSlots()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var view = world.GetView(1);
        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(sequence, "water")));
            for (int tick = 0; tick < rules.TickRate * 10 && view.TrainedCount < (long)sequence; tick++) world.Step();
            Assert.Equal((long)sequence, view.TrainedCount);
            Assert.Equal((int)sequence, view.GarrisonCount);
            UnitView newUnit = view.Units[^1];
            foreach (UnitView unit in view.Units)
                if (unit.Id != newUnit.Id)
                    Assert.True(Vector2.Distance(unit.Position, newUnit.Position) >= rules.MinimumSeparation);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RallyOnlySendsReservesThatAlreadyExisted(bool hasReserve)
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        var basePosition = new Vector2(200, 200);
        var reservePosition = basePosition + new Vector2(0, rules.BaseRadius + rules.SlotSpacing);
        setup.Players.Add(new PlayerSpawn(1, "water", basePosition, new Vector2(200, 500), 1, 100, true));
        if (hasReserve) setup.Units.Add(new UnitSpawn(1, "earth", reservePosition, garrison: true));
        var world = new SimulationWorld(rules, setup, 1);

        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Rally(1)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(2, "water")));
        int ticks = (int)Math.Ceiling(rules.NatureTrainSeconds * rules.TickRate) + 4;
        for (int tick = 0; tick < ticks; tick++) world.Step();

        WorldView view = world.GetView(1);
        UnitView trained = Assert.Single(view.Units, unit => unit.Element == rules.ElementIndex("water"));
        Assert.True(trained.Garrison);
        Assert.Equal(reservePosition, trained.Position);
        Assert.Equal(hasReserve, view.IsRallying);
        Assert.Equal(hasReserve ? 1 : 0, view.RallyingCount);
        Assert.Equal(1, view.GarrisonCount - view.RallyingCount);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Rally(3)));
        world.Step();
        Assert.NotEqual(reservePosition, Assert.Single(view.Units, unit => unit.Id == trained.Id).Position);
        Assert.Equal(hasReserve ? 2 : 1, view.RallyingCount);
        Assert.Equal(view.GarrisonCount, view.RallyingCount);
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
