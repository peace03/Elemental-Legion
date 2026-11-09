using System.Numerics;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class WorldViewSourceBoundsTests
{
    [Fact]
    public void SeparatedSourcesKeepExactCircleBoundariesAndDoNotRevealTheGap()
    {
        var rules = GameRulesTests.Rules();
        Vector2[] sources = [new(200), new(230.125f, 240.25f), new(900.875f, 1000.5f)];
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", sources[0], sources[1]));
        setup.Units.Add(new UnitSpawn(1, "water", sources[1]));
        setup.Units.Add(new UnitSpawn(1, "water", sources[2]));
        var world = new SimulationWorld(rules, setup, 1);
        var points = new List<Vector2> { new(0), new(1680), new(200, 1000), new(400, 400), new(float.NaN, 200), new(float.PositiveInfinity, 200) };
        foreach (Vector2 source in sources)
            foreach (Vector2 axis in new[] { Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY })
            {
                Vector2 boundary = source + axis * 25;
                points.Add(boundary);
                points.Add(new Vector2(MathF.BitIncrement(boundary.X), boundary.Y));
                points.Add(new Vector2(MathF.BitDecrement(boundary.X), boundary.Y));
                points.Add(new Vector2(boundary.X, MathF.BitIncrement(boundary.Y)));
                points.Add(new Vector2(boundary.X, MathF.BitDecrement(boundary.Y)));
            }
        foreach (Vector2 point in points)
        {
            bool insideMap = point.X >= 0 && point.Y >= 0 && point.X <= 1680 && point.Y <= 1680;
            bool expected = insideMap && sources.Any(source => Vector2.DistanceSquared(source, point) <= 625);
            Assert.Equal(expected, world.GetView(1).CanSeePoint(point));
        }
    }

    [Fact]
    public void BoundsRefreshAfterMovementAndCameraStillRevealsBeyondCircularSources()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(400)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(400)));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.False(world.GetView(1).CanSeePoint(new Vector2(450, 400)));
        setup.CameraVisibility = true;
        var camera = new SimulationWorld(rules, setup, 1);
        Assert.True(camera.GetView(1).CanSeePoint(new Vector2(450, 400)));
        Assert.False(world.GetView(1).CanSeePoint(new Vector2(1000)));
        world.TryEnqueue(1, SimCommand.MoveTo(1, 1, new Vector2(500)));
        for (int tick = 0; tick < 100; tick++) world.Step();
        WorldView view = world.GetView(1);
        Assert.True(view.CanSeePoint(view.Units.Single().Position + new Vector2(0, 25)));
        Assert.False(view.CanSeePoint(new Vector2(1000)));
    }

    [Theory]
    [InlineData(6, 6, 1)]
    [InlineData(1674, 1674, -1)]
    public void MapEdgesAgreeWithTheOriginalCircularUnion(float x, float z, int direction)
    {
        var rules = GameRulesTests.Rules();
        Vector2 home = new(x, z), field = home + new Vector2(0, direction * 8);
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", home, field));
        setup.Units.Add(new UnitSpawn(1, "water", field));
        var world = new SimulationWorld(rules, setup, 1);
        for (int dx = -35; dx <= 35; dx++)
            for (int dz = -35; dz <= 35; dz++)
            {
                Vector2 point = home + new Vector2(dx, dz);
                bool inMap = point.X >= 0 && point.Y >= 0 && point.X <= 1680 && point.Y <= 1680;
                bool expected = inMap && (Vector2.DistanceSquared(home, point) <= 625 || Vector2.DistanceSquared(field, point) <= 625);
                Assert.Equal(expected, world.GetView(1).CanSeePoint(point));
            }
    }
}
