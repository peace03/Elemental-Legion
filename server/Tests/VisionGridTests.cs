using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>지형 탐색 격자와 정확한 유닛 시야가 서로 다른 책임임을 검사한다.</summary>
public sealed class VisionGridTests
{
    [Fact]
    public void NetworkWorldIncludesTheGroundAtTheCameraCorner()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        Assert.Equal(JoinFailure.None, network.TryJoin(rules.StartingElements[0], out int owner));
        var view = network.World.GetView(owner);
        // 16:9 화면의 직교 카메라에서 좌우 반폭은 53.3m, 지면 반깊이는 33.8m다.
        Assert.True(view.CanSeePoint(view.LegionPosition + new Vector2(-53.3f, 33.8f)));
        Assert.False(view.CanSeePoint(view.LegionPosition + new Vector2(-75, 33.8f)));
    }

    [Fact]
    public void CameraCornerReplicatesNeutralWhileAnOutsideUnitInTheSameFogCellStaysHidden()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup { CameraVisibility = true };
        var center = new Vector2(100, 100);
        var visible = new Vector2(153.3f, 133.8f);
        var hidden = new Vector2(159, 133.8f);
        setup.Players.Add(new PlayerSpawn(1, "water", center, center));
        setup.Units.Add(new UnitSpawn(-1, "water", visible));
        setup.Units.Add(new UnitSpawn(-1, "fire", hidden));
        setup.Cores.Add(new CoreSpawn(visible, 1));
        setup.Cores.Add(new CoreSpawn(hidden, 2));
        var world = new SimulationWorld(rules, setup, 1);
        var view = world.GetView(1);
        var writer = new NetDataWriter();
        Protocol.WriteUnits(writer, view, rules.MapSize, 0);
        var snapshot = new UnitSnapshotAssembler(rules);
        Assert.True(snapshot.TryAccept(new NetDataReader(writer.CopyData())));
        Assert.Equal(1, snapshot.Count);
        Assert.Equal(rules.ElementIndex("water"), snapshot.Units[0].Element);
        Assert.Equal(1, Assert.Single(view.Cores).Amount);
        Assert.Equal(VisionCellState.Visible, view.Vision.GetState(15, 13));
        Assert.True(world.BeginGhost(1));
        Assert.Empty(view.Units);
        Assert.Equal(VisionCellState.Explored, view.Vision.GetState(15, 13));
    }

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
        Assert.True(world.Metrics.Deaths > 0);
        Assert.Equal(0, world.Metrics.Conversions);
        if (conversionChance == 1)
            Assert.Equal(RecoveryRole.Secure, Assert.Single(world.GetView(2).Candidates).Role);
        else Assert.Empty(world.GetView(2).Candidates);
        Assert.Equal(VisionCellState.Explored, world.GetView(1).Vision.GetState(10, 10));
        Assert.Equal(VisionCellState.Visible, world.GetView(2).Vision.GetState(10, 10));
        if (conversionChance == 1)
        {
            for (int tick = 0; tick < 60; tick++) world.Step();
            Assert.Equal(1, world.Metrics.Conversions);
            Assert.Empty(world.GetView(2).Candidates);
            Assert.Equal(VisionCellState.Explored, world.GetView(1).Vision.GetState(10, 10));
        }
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
