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
        var setup = Setup();
        setup.Units.Add(new UnitSpawn(1, "water", setup.Players[0].LegionPosition));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
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
    [InlineData(7, 1)]
    [InlineData(8, 1.6)]
    public void FormationReturnSpeedsUpAtTheCatchUpDistance(float gap, float multiplier)
    {
        var rules = GameRulesTests.Rules();
        var setup = Setup();
        Vector2 start = setup.Players[0].LegionPosition - Vector2.UnitX * gap;
        setup.Units.Add(new UnitSpawn(1, "water", start));
        var world = new SimulationWorld(rules, setup, 1);

        world.Step();

        var unit = Assert.Single(world.GetView(1).Units);
        Assert.Equal(rules.LegionSpeed * multiplier * (float)rules.DeltaTime, Vector2.Distance(start, unit.Position), 4);
    }

    [Fact]
    public void CatchUpContinuesUntilTheUnitReachesTheMovingFormation()
    {
        var rules = GameRulesTests.Rules();
        var setup = Setup();
        Vector2 start = setup.Players[0].LegionPosition - Vector2.UnitX * 12;
        setup.Units.Add(new UnitSpawn(1, "water", start));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX));

        var view = world.GetView(1);
        for (int tick = 0; tick < rules.TickRate * 4; tick++) world.Step();

        Assert.Equal(view.LegionPosition, Assert.Single(view.Units).Position);
    }

    [Theory]
    [InlineData("earth")]
    [InlineData("crystal")]
    [InlineData("water")]
    public void MovementInputOverridesUnitPursuitAndStoppingResumesIt(string element)
    {
        var rules = GameRulesTests.Rules();
        float range = rules.Elements[rules.ElementIndex(element)].Range;
        var setup = CombatTests.Battle(CombatTests.Unit(1, element, 50, garrison: false),
            CombatTests.Unit(2, "earth", 50 + range + 3));
        var world = new SimulationWorld(rules, setup, 1);
        world.Step();

        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitY)));
        for (int tick = 0; tick < 3; tick++)
        {
            world.Step();
            var view = world.GetView(1);
            Assert.Equal(view.LegionPosition, CombatTests.Find(world, 1).Position);
        }

        Vector2 before = CombatTests.Find(world, 1).Position;
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(2, Vector2.Zero)));
        world.Step();
        Assert.True(CombatTests.Find(world, 1).Position.X > before.X);
        Assert.Equal(before, world.GetView(1).LegionPosition);
    }

    [Theory]
    [InlineData("earth")]
    [InlineData("water")]
    public void MovingUnitsAttackInRangeWithoutStoppingOrResettingTheirCooldown(string element)
    {
        var rules = GameRulesTests.Rules();
        float range = rules.Elements[rules.ElementIndex(element)].Range;
        var setup = CombatTests.Battle(CombatTests.Unit(1, element, 50, garrison: false),
            CombatTests.Unit(2, "earth", 50 + range / 2));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitY));

        for (int tick = 0; tick < 3; tick++)
        {
            world.Step();
            var view = world.GetView(1);
            Assert.Equal(view.LegionPosition, CombatTests.Find(world, 1).Position);
            Assert.Equal(tick == 0 ? 1 : 0, view.Events.Count(item =>
                item.Kind == SimEventKind.Attack && item.SourceId == 1 && item.TargetId == 2));
        }
    }

    [Fact]
    public void MovementInputOverridesBuildingPursuitAndStoppingResumesIt()
    {
        var rules = GameRulesTests.Rules();
        var setup = Setup();
        var center = setup.Players[0].LegionPosition;
        float range = rules.Elements[rules.ElementIndex("water")].Range;
        var enemyBase = center + Vector2.UnitX * (rules.BaseRadius + range + 3);
        setup.Players.Add(new PlayerSpawn(2, "earth", enemyBase, enemyBase));
        setup.Units.Add(new UnitSpawn(1, "water", center));
        var world = new SimulationWorld(rules, setup, 1);
        world.Step();
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitY));

        for (int tick = 0; tick < 3; tick++)
        {
            world.Step();
            var view = world.GetView(1);
            Assert.Equal(view.LegionPosition, Assert.Single(view.Units).Position);
        }

        Vector2 before = Assert.Single(world.GetView(1).Units).Position;
        world.TryEnqueue(1, SimCommand.Move(2, Vector2.Zero));
        world.Step();
        Assert.True(Assert.Single(world.GetView(1).Units).Position.X > before.X);
    }

    [Fact]
    public void MovingUnitsKeepAttackingBuildingsInRange()
    {
        var rules = GameRulesTests.Rules();
        var setup = Setup();
        var center = setup.Players[0].LegionPosition;
        float range = rules.Elements[rules.ElementIndex("water")].Range;
        var enemyBase = center + Vector2.UnitX * (rules.BaseRadius + range / 2);
        setup.Players.Add(new PlayerSpawn(2, "earth", enemyBase, enemyBase));
        setup.Units.Add(new UnitSpawn(1, "water", center));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitY));

        for (int tick = 0; tick < 3; tick++)
        {
            world.Step();
            var view = world.GetView(1);
            Assert.Equal(view.LegionPosition, Assert.Single(view.Units).Position);
            Assert.Equal(tick == 0 ? 1 : 0, view.Events.Count(item =>
                item.Kind == SimEventKind.Attack && item.OwnerId == 1 && item.TargetKind == ObservedEntityKind.Building));
        }
    }

    [Fact]
    public void StationaryReservesSpreadOutWithoutJoiningTheLegion()
    {
        var rules = GameRulesTests.Rules();
        var setup = Setup();
        var spawn = new Vector2(200, 200 + rules.BaseRadius + rules.SlotSpacing);
        for (int index = 0; index < 3; index++)
            setup.Units.Add(new UnitSpawn(1, "water", spawn, garrison: true));
        var world = new SimulationWorld(rules, setup, 1);

        for (int tick = 0; tick < rules.TickRate; tick++) world.Step();

        var view = world.GetView(1);
        Assert.Equal(3, view.GarrisonCount);
        Assert.False(view.IsRallying);
        for (int left = 0; left < view.Units.Count; left++)
            for (int right = left + 1; right < view.Units.Count; right++)
                Assert.True(Vector2.Distance(view.Units[left].Position, view.Units[right].Position) >= rules.MinimumSeparation - 0.01f);
    }

    [Fact]
    public void AlliesPassEachOtherWhenTheirFormationSlotsSwap()
    {
        var rules = GameRulesTests.Rules();
        var setup = Setup();
        var center = setup.Players[0].LegionPosition;
        var offset = Vector2.UnitX * (rules.SlotSpacing / 2);
        setup.Units.Add(new UnitSpawn(1, "water", center + offset));
        setup.Units.Add(new UnitSpawn(1, "water", center - offset));
        var world = new SimulationWorld(rules, setup, 1);
        var view = world.GetView(1);

        for (int tick = 0; tick < rules.TickRate; tick++)
        {
            world.Step();
            float distance = Vector2.Distance(view.Units[0].Position, view.Units[1].Position);
            Assert.True(distance >= 1.2f, $"tick {tick}: gap {distance}");
        }
        Assert.Equal(center - offset, view.Units[0].Position);
        Assert.Equal(center + offset, view.Units[1].Position);
    }

    [Theory]
    [InlineData(float.Epsilon)]
    [InlineData(float.MaxValue)]
    public void ExtremeFiniteDirectionNormalizesWithoutUnderflowOrOverflow(float magnitude)
    {
        var setup = Setup();
        setup.Units.Add(new UnitSpawn(1, "water", setup.Players[0].LegionPosition));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
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
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(50, 50)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX));
        for (int i = 0; i < 100; i++) world.Step();
        Assert.InRange(world.GetView(1).LegionPosition.X, 53.99f, 54.001f);
        world.TryEnqueue(1, SimCommand.Move(2, -Vector2.One));
        for (int i = 0; i < 400; i++) world.Step();
        Assert.Equal(Vector2.Zero, world.GetView(1).LegionPosition);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 2)]
    public void UnitsWalkAroundABuildingToReachTheLegion(bool garrison, bool atMapEdge, int angle)
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        var building = new Vector2(1400, atMapEdge ? 2 : 1400);
        float radians = angle * MathF.PI / 180;
        var direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        var axis = atMapEdge ? new Vector2(building.X, 0.5f) : building;
        var center = axis + direction * (rules.BaseRadius + 12);
        var previous = axis - direction * (rules.BaseRadius + 4);
        setup.Players.Add(new PlayerSpawn(1, "water", building, center));
        setup.Units.Add(new UnitSpawn(1, "water", previous, garrison: garrison));
        var world = new SimulationWorld(rules, setup, 1);
        if (garrison) world.TryEnqueue(1, SimCommand.Rally(1));

        float step = rules.LegionSpeed * (garrison ? rules.RallySpeedMultiplier : rules.CatchUpSpeedMultiplier) * (float)rules.DeltaTime;
        for (int tick = 0; tick < rules.TickRate * 8; tick++)
        {
            world.Step();
            Vector2 position = Assert.Single(world.GetView(1).Units).Position;
            Vector2 delta = position - previous;
            float fraction = delta == Vector2.Zero ? 0 : Math.Clamp(Vector2.Dot(building - previous, delta) / delta.LengthSquared(), 0, 1);
            Assert.True(Vector2.Distance(previous + delta * fraction, building) >= rules.BaseRadius - 0.001f);
            Assert.InRange(delta.Length(), 0, step + 0.001f);
            previous = position;
        }
        var unit = Assert.Single(world.GetView(1).Units);
        Assert.False(unit.Garrison);
        Assert.Equal(center, unit.Position);
    }

    [Fact]
    public void FormationUsesASlotOutsideTheWallUntilItsOriginalSlotClears()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        var building = new Vector2(200, 200);
        var center = building + new Vector2(rules.BaseRadius + rules.SlotSpacing / 4, 0);
        setup.Players.Add(new PlayerSpawn(1, "water", building, center));
        setup.Units.Add(new UnitSpawn(1, "water", building - new Vector2(rules.BaseRadius + 1, 0)));
        setup.Units.Add(new UnitSpawn(1, "water", center + Vector2.UnitX * rules.SlotSpacing / 2));
        var world = new SimulationWorld(rules, setup, 1);
        for (int tick = 0; tick < rules.TickRate * 8; tick++) world.Step();
        var view = world.GetView(1);
        Assert.True(view.Units[0].Position.X >= building.X);
        Assert.InRange(Vector2.Distance(view.Units[0].Position, building), rules.BaseRadius - 0.001f, rules.BaseRadius + rules.SlotSpacing);
        Assert.True(Vector2.Distance(view.Units[0].Position, view.Units[1].Position) >= rules.MinimumSeparation - 0.01f);

        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitY));
        for (int tick = 0; tick < rules.TickRate * 2; tick++) world.Step();
        world.TryEnqueue(1, SimCommand.Move(2, Vector2.Zero));
        for (int tick = 0; tick < rules.TickRate; tick++) world.Step();
        Assert.Equal(view.LegionPosition - Vector2.UnitX * rules.SlotSpacing / 2, view.Units[0].Position);
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
    public void CommandBudgetsRemainBoundedAndIndependentAcrossHostMigration()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, Setup(), 1);
        ulong sequence = 0;
        for (int index = 0; index < rules.CommandRateLimit; index++)
        {
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(++sequence, Vector2.UnitX)));
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Focus(++sequence, 1)));
        }
        var restored = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        Assert.Equal(CommandAdmission.StaleSequence, restored.TryEnqueue(1, SimCommand.QuickTransfer(sequence, 4, 1)));
        Assert.Equal(CommandAdmission.RateLimited, restored.TryEnqueue(1, SimCommand.MoveTo(sequence + 1, 1, new Vector2(100))));
        Assert.Equal(CommandAdmission.RateLimited, restored.TryEnqueue(1, SimCommand.QuickTransfer(sequence + 1, 4, 1)));
        for (int tick = 0; tick < rules.TickRate; tick++) restored.Step();
        Assert.Equal(CommandAdmission.Accepted, restored.TryEnqueue(1, SimCommand.Move(++sequence, Vector2.UnitX)));
        Assert.Equal(CommandAdmission.Accepted, restored.TryEnqueue(1, SimCommand.QuickTransfer(++sequence, 4, 1)));
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
            for (int candidate = 0; candidate < 40; candidate++)
            {
                Vector2 point = source + new Vector2((float)(random.NextDouble() * 10 - 5), (float)(random.NextDouble() * 10 - 5));
                setup.Units.Add(new UnitSpawn(2, "life", point));
            }
            var world = new SimulationWorld(rules, setup, 1);
            world.Step();
            int expected = world.GetView(1).Units.Where(unit => unit.OwnerId == 2)
                .OrderBy(unit => Vector2.DistanceSquared(source, unit.Position)).ThenBy(unit => unit.Id).First().Id;
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
        for (int i = 0; i <= GameRulesTests.Rules().PopulationCap(4); i++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(50, 50)));
        Assert.Throws<ArgumentException>(() => new SimulationWorld(GameRulesTests.Rules(), setup, 1));
    }
}
