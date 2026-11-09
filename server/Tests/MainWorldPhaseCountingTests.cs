using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>경제·시야 단계의 인원 집계가 핵 경쟁과 최종 관측을 보존하는지 확인한다.</summary>
public sealed class MainWorldPhaseCountingTests
{
    [Fact]
    public void SequentialCoresRecheckOverflowAfterTheLowerIdWinsATie()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(800), new Vector2(50)));
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(50), cores: long.MaxValue - 8));
        setup.Units.Add(new UnitSpawn(2, "water", new Vector2(50)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(50)));
        setup.Cores.Add(new CoreSpawn(new Vector2(50), 8));
        setup.Cores.Add(new CoreSpawn(new Vector2(50), 8));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);

        world.Step();

        Assert.Equal(long.MaxValue, world.GetView(1).CoreBalance);
        Assert.Equal(8, world.GetView(2).CoreBalance);
        Assert.Equal(new[] { 1, -1 }, world.GetView(1).Events.Where(item => item.Kind == SimEventKind.CorePickedUp).Select(item => item.OwnerId));
        Assert.Equal(new[] { -1, 2 }, world.GetView(2).Events.Where(item => item.Kind == SimEventKind.CorePickedUp).Select(item => item.OwnerId));
        Assert.Equal(2, world.Metrics.Pickups);
        Assert.Equal(0, world.Metrics.DroppedCores);
    }

    [Theory]
    [InlineData(false, false, false, false, 1)]
    [InlineData(true, false, false, false, 0)]
    [InlineData(false, true, false, false, 0)]
    [InlineData(false, false, true, false, 0)]
    [InlineData(false, false, false, true, 0)]
    public void SquadCenterPickupRequiresALivingJoinedFieldMember(bool reinforcing, bool dead, bool ghost, bool guard, int pickups)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(200)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200)));
        setup.Cores.Add(new CoreSpawn(new Vector2(300), 8));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        if (ghost) world.BeginGhost(1);
        object unit = Units(world).Single();
        byte squad = guard ? (byte)4 : (byte)2;
        Set(unit, "SquadId", reinforcing ? (byte)0 : squad);
        Set(unit, "IsReinforcing", reinforcing);
        Set(unit, "DestinationSquadId", reinforcing ? squad : (byte)0);
        Set(unit, "Garrison", guard);
        if (dead) { Set(unit, "Health", 0f); Set(unit, "Dead", true); }
        Refresh(world);
        Set(((Array)Get(Player(world, 1), "Squads")).GetValue(squad - 1)!, "Position", new Vector2(300));

        object economy = Get(world, "economy");
        economy.GetType().GetMethod("TickCores", Internal)!.Invoke(economy, new[] { State(world) });
        Refresh(world);

        Assert.Equal(pickups, world.Metrics.Pickups);
        Assert.Equal(pickups * 8, world.GetView(1).CoreBalance);
        Assert.Equal(1 - pickups, world.Metrics.DroppedCores);
    }

    [Fact]
    public void VisibilityCountsAllSquadsReinforcementsAndReservationsAfterMembershipChanges()
    {
        var setup = new WorldSetup { CameraVisibility = true };
        setup.Players.Add(new PlayerSpawn(7, "water", new Vector2(100), new Vector2(200), 4, 1000, true));
        for (int index = 0; index < 7; index++)
            setup.Units.Add(new UnitSpawn(7, "water", new Vector2(200 + index * 2, 200)));
        setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(320, 300)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(7, SimCommand.Train(1, "water", 2, 2)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(7, SimCommand.Train(2, "water", 1, 4)));
        world.Step();
        object player = Player(world, 7);
        object[] own = Units(world).Where(unit => (int)Get(unit, "OwnerId") == 7).ToArray();
        for (int index = 0; index < 4; index++)
        {
            Set(own[index], "SquadId", (byte)(index + 1));
            Set(own[index], "Position", new Vector2(200 + index * 100));
            Set(own[index], "Garrison", index == 3);
            Set(((Array)Get(player, "Squads")).GetValue(index)!, "Position", new Vector2(200 + index * 100));
        }
        for (int index = 4; index < 6; index++)
        {
            Set(own[index], "SquadId", (byte)0);
            Set(own[index], "IsReinforcing", true);
            Set(own[index], "DestinationSquadId", index == 4 ? (byte)2 : (byte)4);
            Set(own[index], "Position", new Vector2(600 + index * 100));
        }
        Set(own[6], "Health", 0f); Set(own[6], "Dead", true);
        Set(player, "FocusAnchor", (byte)2);

        Refresh(world);

        WorldView view = world.GetView(7);
        Assert.Equal(new[] { 1, 1, 1, 1 }, view.Squads.Select(squad => squad.JoinedCount));
        Assert.Equal(new[] { 0, 1, 0, 1 }, view.Squads.Select(squad => squad.ReinforcingCount));
        Assert.Equal(new[] { 0, 2, 0, 1 }, view.Squads.Select(squad => squad.ReservedPopulation));
        Assert.Equal(3, view.ReservedPopulation);
        Assert.Equal(view.ReservedPopulation, view.Squads.Sum(squad => squad.ReservedPopulation));
        Assert.Equal(6, view.ElementCounts.Sum());
        Assert.Equal(3, view.GarrisonCount);
        Assert.Equal(2, view.RallyingCount);
        Assert.Equal(own.Take(6).Select(unit => (int)Get(unit, "Id")), view.Units.Where(unit => unit.OwnerId == 7).Select(unit => unit.Id));
        Assert.True(view.CanSeePoint(new Vector2(320, 300)));
        Assert.Contains(view.Units, unit => unit.OwnerId == -1);

        Set(own[1], "Health", 0f); Set(own[1], "Dead", true);
        Refresh(world);

        Assert.Equal(0, view.Squads[1].JoinedCount);
        Assert.Equal(1, view.Squads[1].ReinforcingCount);
        Assert.False(view.CanSeePoint(new Vector2(320, 300)));
        Assert.DoesNotContain(view.Units, unit => unit.OwnerId == -1);
        Assert.Equal(3, view.ReservedPopulation);
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(0f)]
    [InlineData(1680f)]
    public void CameraCircleElisionMatchesEveryOriginalCircleAtBoundariesAndAfterGhosting(float coordinate)
    {
        GameRules rules = GameRulesTests.Rules();
        var center = new Vector2(coordinate);
        float radius = rules.VisionRadius;
        Vector2 limit = new Vector2(rules.MapSize);
        Vector2[] sources =
        {
            center,
            Vector2.Clamp(center + new Vector2(rules.CameraVisionHalfSize.X - radius, 0), Vector2.Zero, limit),
            Vector2.Clamp(center + new Vector2(0, rules.CameraVisionHalfSize.Y - radius), Vector2.Zero, limit),
            Vector2.Clamp(center - new Vector2(rules.CameraVisionHalfSize.X - radius, 0), Vector2.Zero, limit),
            Vector2.Clamp(center - new Vector2(0, rules.CameraVisionHalfSize.Y - radius), Vector2.Zero, limit),
            Vector2.Clamp(center + new Vector2(rules.CameraVisionHalfSize.X + radius * 2, 0), Vector2.Zero, limit)
        };
        var setup = new WorldSetup { CameraVisibility = true };
        setup.Players.Add(new PlayerSpawn(1, "water", center, center));
        foreach (Vector2 source in sources) setup.Units.Add(new UnitSpawn(1, "water", source));
        Vector2 min = Vector2.Max(Vector2.Zero, center - rules.CameraVisionHalfSize);
        Vector2 max = Vector2.Min(limit, center + rules.CameraVisionHalfSize);
        var probes = sources.SelectMany(source => new[]
        {
            source + new Vector2(radius, 0), source - new Vector2(radius, 0),
            source + new Vector2(0, radius), source - new Vector2(0, radius),
            new Vector2(MathF.BitIncrement(source.X + radius), source.Y),
            new Vector2(MathF.BitDecrement(source.X + radius), source.Y)
        }).Concat(new[] { min, max, new Vector2(min.X, max.Y), new Vector2(max.X, min.Y),
            new Vector2(MathF.BitIncrement(max.X), center.Y), new Vector2(MathF.BitDecrement(min.X), center.Y) }).ToArray();
        foreach (Vector2 point in probes)
            if (point.X >= 0 && point.Y >= 0 && point.X <= rules.MapSize && point.Y <= rules.MapSize)
                setup.Units.Add(new UnitSpawn(-1, "earth", point));
        var world = new SimulationWorld(rules, setup, 1);
        var baseline = (VisionGrid)Activator.CreateInstance(typeof(VisionGrid), Internal, null,
            new object[] { rules.MapSize, rules.CellSize }, null)!;
        Vector2[] allSources = sources.Concat(new[] { center }).ToArray();

        AssertCircleUnion(world, rules, baseline, allSources, probes, center, true);
        Assert.True(world.BeginGhost(1));
        AssertCircleUnion(world, rules, baseline, allSources, probes, center, false);
    }

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(33f, 0f, false)]
    [InlineData(0f, 13f, false)]
    [InlineData(-33f, 0f, false)]
    [InlineData(0f, -13f, false)]
    [InlineData(33.00003f, 0f, false)]
    public void CircleContainmentKeepsTangentAndOutsideCircles(float x, float y, bool contained)
    {
        GameRules rules = GameRulesTests.Rules();
        Type sightType = typeof(SimulationWorld).Assembly.GetType("ElementalLegion.Shared.Sim.CameraSight")!;
        object sight = Activator.CreateInstance(sightType, Internal, null,
            new object[] { new Vector2(300), rules.CameraVisionHalfSize, rules.MapSize }, null)!;

        Assert.Equal(contained, sightType.GetMethod("ContainsCircle", Internal)!.Invoke(sight,
            new object[] { new Vector2(300 + x, 300 + y), rules.VisionRadius }));
    }

    private static void AssertCircleUnion(SimulationWorld world, GameRules rules, VisionGrid baseline,
        Vector2[] sources, Vector2[] probes, Vector2 center, bool camera)
    {
        WorldView view = world.GetView(1);
        Vector2 min = Vector2.Max(Vector2.Zero, center - rules.CameraVisionHalfSize);
        Vector2 max = Vector2.Min(new Vector2(rules.MapSize), center + rules.CameraVisionHalfSize);
        bool Visible(Vector2 point) => point.X >= 0 && point.Y >= 0 && point.X <= rules.MapSize && point.Y <= rules.MapSize &&
            (camera && point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y ||
                sources.Any(source => Vector2.DistanceSquared(source, point) <= rules.VisionRadius * rules.VisionRadius));
        foreach (object unit in Units(world))
            Assert.Equal(Visible((Vector2)Get(unit, "Position")), ((ulong)Get(unit, "VisibleTo") & 1UL) != 0);
        Assert.Equal(Units(world).Where(unit => Visible((Vector2)Get(unit, "Position"))).Select(unit => (int)Get(unit, "Id")), view.Units.Select(unit => unit.Id));
        foreach (Vector2 point in probes) Assert.Equal(Visible(point), view.CanSeePoint(point));
        Assert.Equal(sources, ((IEnumerable)Get(Player(world, 1), "VisionSources")).Cast<Vector2>());

        typeof(VisionGrid).GetMethod("Begin", Internal)!.Invoke(baseline, null);
        if (camera)
        {
            Type sightType = typeof(SimulationWorld).Assembly.GetType("ElementalLegion.Shared.Sim.CameraSight")!;
            object sight = Activator.CreateInstance(sightType, Internal, null,
                new object[] { center, rules.CameraVisionHalfSize, rules.MapSize }, null)!;
            typeof(VisionGrid).GetMethod("Reveal", Internal, null, new[] { sightType }, null)!.Invoke(baseline, new[] { sight });
        }
        MethodInfo reveal = typeof(VisionGrid).GetMethod("Reveal", Internal, null, new[] { typeof(Vector2), typeof(float) }, null)!;
        foreach (Vector2 source in sources) reveal.Invoke(baseline, new object[] { source, rules.VisionRadius });
        typeof(VisionGrid).GetMethod("Commit", Internal)!.Invoke(baseline, null);
        Assert.Equal(baseline.Current.ToArray(), view.Vision.Current.ToArray());
        Assert.Equal(baseline.Explored.ToArray(), view.Vision.Explored.ToArray());
    }

    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object State(SimulationWorld world) => Get(world, "state");
    private static object[] Units(SimulationWorld world) => ((IEnumerable)Get(State(world), "Units")).Cast<object>().ToArray();
    private static object Player(SimulationWorld world, int id) => ((IEnumerable)Get(State(world), "Players")).Cast<object>().Single(player => (int)Get(player, "Id") == id);
    private static object Get(object target, string field) => target.GetType().GetField(field, Internal)!.GetValue(target)!;
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Internal)!.SetValue(target, value);
    private static void Refresh(SimulationWorld world) => typeof(SimulationWorld).Assembly.GetType("ElementalLegion.Shared.Sim.VisibilitySystem")!
        .GetMethod("Refresh", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { State(world) });
}
