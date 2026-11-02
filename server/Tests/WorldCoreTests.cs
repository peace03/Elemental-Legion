using System.Numerics;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>명령·이동·충돌·시야와 공간 탐색의 경계를 확인한다.</summary>
public sealed class WorldCoreTests
{
    internal static WorldSetup Setup(string main = "water", int cores = 100, int level = 4)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, main, new Vector2(200, 200), new Vector2(50, 50), level, cores, true, true));
        return setup;
    }

    [Fact]
    public void DiagonalInputIsNormalizedAndInvalidInputChangesNothing()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Setup(), 1);
        Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.Move(1, new Vector2(float.NaN, 0))));
        Assert.Equal(CommandAdmission.UnknownPlayer, world.TryEnqueue(9, SimCommand.Move(1, Vector2.One)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(1, Vector2.One)));
        Assert.Equal(CommandAdmission.StaleSequence, world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX)));
        world.Step();
        Assert.Equal(0.35f, Vector2.Distance(new Vector2(50, 50), world.GetView(1).LegionPosition), 4);
    }

    [Fact]
    public void SmallDirectionKeepsFixedSpeedAndFormationSpacing()
    {
        var setup = Setup();
        setup.Units.Add(CombatTests.Unit(1, "water", 50, garrison: false));
        setup.Units.Add(CombatTests.Unit(1, "water", 50, garrison: false));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(1, new Vector2(0.1f, 0))));
        world.Step();
        Assert.Equal(0.35f, Vector2.Distance(new Vector2(50, 50), world.GetView(1).LegionPosition), 4);
        world.TryEnqueue(1, SimCommand.Move(2, Vector2.Zero));
        for (int tick = 0; tick < 10; tick++) world.Step();
        Assert.Equal(1.5f, Vector2.Distance(world.GetView(1).Units[0].Position, world.GetView(1).Units[1].Position), 4);
    }

    [Theory]
    [InlineData(float.Epsilon)]
    [InlineData(float.MaxValue)]
    public void ExtremeFiniteDirectionNormalizesWithoutUnderflowOrOverflow(float magnitude)
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Setup(), 1);
        world.TryEnqueue(1, SimCommand.Move(1, new Vector2(magnitude, 0)));
        world.Step();
        Assert.Equal(50.35f, world.GetView(1).LegionPosition.X, 4);
    }

    [Fact]
    public void BuildingsStopSweptMovementAndMapEdgesClamp()
    {
        var setup = Setup();
        setup.Players.Clear();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(60, 50), new Vector2(50, 50)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX));
        for (int i = 0; i < 100; i++) world.Step();
        Assert.InRange(world.GetView(1).LegionPosition.X, 53.99f, 54.001f);
        world.TryEnqueue(1, SimCommand.Move(2, -Vector2.One));
        for (int i = 0; i < 400; i++) world.Step();
        Assert.Equal(Vector2.Zero, world.GetView(1).LegionPosition);
    }

    [Fact]
    public void RateLimitAndBoundedQueueApplyToBotsToo()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, Setup(), 1);
        for (ulong i = 1; i <= (ulong)rules.CommandRateLimit; i++)
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(i, Vector2.Zero)));
        Assert.Equal(CommandAdmission.RateLimited, world.TryEnqueue(1, SimCommand.Move(99, Vector2.Zero)));
        for (int i = 0; i < rules.TickRate; i++) world.Step();
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(100, Vector2.Zero)));
    }

    [Fact]
    public void VisionUsesExactRadiusAcrossCellBoundariesAndOwnUnitsRemainKnown()
    {
        var setup = Setup();
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(50, 50)));
        setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(75, 50)));
        setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(75.01f, 50)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        Assert.Equal(2, world.GetView(1).Units.Count);
        Assert.DoesNotContain(world.GetView(1).Units, unit => unit.Position.X > 75);
        Assert.Throws<ArgumentException>(() => world.GetView(999));
    }

    [Fact]
    public void NearestQueryMatchesFullScanAcrossCellBoundaries()
    {
        var random = new Random(17);
        var rules = GameRulesTests.Rules();
        for (int sample = 0; sample < 24; sample++)
        {
            float x = sample % 2 == 0 ? 49.99f : 50.01f;
            Vector2 source = new(x, 50);
            var setup = CombatTests.Battle(CombatTests.Unit(1, "water", x));
            int expected = 0;
            float nearest = float.PositiveInfinity;
            for (int candidate = 0; candidate < 40; candidate++)
            {
                Vector2 point = source + new Vector2((float)(random.NextDouble() * 10 - 5), (float)(random.NextDouble() * 10 - 5));
                setup.Units.Add(new UnitSpawn(2, "life", point, garrison: true));
                float squared = Vector2.DistanceSquared(source, point);
                if (squared < nearest) { nearest = squared; expected = candidate + 2; }
            }
            var world = new SimulationWorld(rules, setup, 1);
            world.Step();
            Assert.Equal(expected, Assert.Single(world.GetView(1).Events, item => item.Kind == SimEventKind.Attack && item.OwnerId == 1).TargetId);
        }
    }

    [Fact]
    public void SetupRejectsUnknownOwnersBadPositionsAndExcessPopulation()
    {
        var setup = Setup();
        setup.Units.Add(new UnitSpawn(999, "water", Vector2.One));
        Assert.Throws<ArgumentException>(() => new SimulationWorld(GameRulesTests.Rules(), setup, 1));
        setup.Units.Clear();
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(float.PositiveInfinity, 0)));
        Assert.Throws<ArgumentException>(() => new SimulationWorld(GameRulesTests.Rules(), setup, 1));
        setup.Units.Clear();
        for (int i = 0; i < 51; i++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(50, 50)));
        Assert.Throws<ArgumentException>(() => new SimulationWorld(GameRulesTests.Rules(), setup, 1));
    }
}
