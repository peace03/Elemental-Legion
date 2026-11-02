using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>지형 탐색 격자와 정확한 유닛 시야가 서로 다른 책임임을 검사한다.</summary>
public sealed class VisionGridTests
{
    [Fact]
    public void CellIntersectionIncludesCircleBoundaryButDoesNotRevealOutOfRangeUnits()
    {
        var rules = GameRulesTests.Rules(); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(55), new Vector2(55)));
        setup.Units.Add(new UnitSpawn(-1, "water", new Vector2(80, 55)));
        setup.Units.Add(new UnitSpawn(-1, "fire", new Vector2(80.01f, 55)));
        var world = new SimulationWorld(rules, setup, 1); var view = world.GetView(1);
        Assert.Equal(168, view.Vision.Width);
        Assert.Equal(VisionCellState.Visible, view.Vision.GetState(8, 5));
        Assert.Equal(VisionCellState.Visible, view.Vision.GetState(2, 5));
        Assert.Equal(VisionCellState.Unexplored, view.Vision.GetState(9, 5));
        Assert.Single(view.Units); Assert.Equal(rules.ElementIndex("water"), view.Units[0].Element);
    }

    [Fact]
    public void UnitSourcesExploreIndependentlyAndLeavingOnlyRemovesCurrentVisibility()
    {
        var rules = GameRulesTests.Rules(); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(1000), new Vector2(100)));
        setup.Players.Add(new PlayerSpawn(2, "fire", new Vector2(1400), new Vector2(1400)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(VisionCellState.Visible, world.GetView(1).Vision.GetState(10, 10));
        Assert.Equal(VisionCellState.Unexplored, world.GetView(2).Vision.GetState(10, 10));
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX));
        for (int index = 0; index < 220; index++) world.Step();
        Assert.Equal(VisionCellState.Explored, world.GetView(1).Vision.GetState(10, 10));
        Assert.Equal(VisionCellState.Visible, world.GetView(1).Vision.GetState(100, 100));
        Assert.Equal(VisionCellState.Unexplored, world.GetView(2).Vision.GetState(10, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DeathOrConversionRemovesOldOwnersSightWithoutErasingExploration(int conversionChance)
    {
        var data = GameRulesTests.Data(); data.Root["economy"]!["conversionChance"] = conversionChance;
        var rules = new GameRules(new GameDataDocument(data.Root.ToString())); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(1000), new Vector2(100)));
        setup.Players.Add(new PlayerSpawn(2, "fire", new Vector2(1400), new Vector2(101, 100)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100), health: 1));
        setup.Units.Add(new UnitSpawn(2, "fire", new Vector2(101, 100)));
        var world = new SimulationWorld(rules, setup, 1); world.Step();
        if(conversionChance==1) Assert.True(world.Metrics.Conversions > 0); else Assert.True(world.Metrics.Deaths>0);
        Assert.Equal(VisionCellState.Explored, world.GetView(1).Vision.GetState(10, 10));
        Assert.Equal(VisionCellState.Visible, world.GetView(2).Vision.GetState(10, 10));
    }

    [Fact]
    public void CornersAndNewSessionsHaveBoundedIndependentGrids()
    {
        var rules = GameRulesTests.Rules(); var world = new SimulationWorld(rules, new WorldSetup(), 1);
        world.TryAddPlayer(new PlayerSpawn(1, "water", Vector2.Zero, new Vector2(30)));
        world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(rules.MapSize), new Vector2(rules.MapSize - 30)));
        Assert.Equal(VisionCellState.Visible, world.GetView(1).Vision.GetState(0, 0));
        Assert.Equal(VisionCellState.Unexplored, world.GetView(1).Vision.GetState(167, 167));
        Assert.Equal(VisionCellState.Visible, world.GetView(2).Vision.GetState(167, 167));
        Assert.Equal(VisionCellState.Unexplored, world.GetView(2).Vision.GetState(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.GetView(1).Vision.GetState(-1, 0));
    }
}
