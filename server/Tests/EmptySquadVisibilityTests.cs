using System.Linq;
using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>빈 분대의 가상 중심으로 숨은 건물을 탐지하지 못하고 증원이 합류하는 경계를 검증한다.</summary>
public sealed class EmptySquadVisibilityTests
{
    private static readonly Vector2 HiddenBase = new Vector2(220, 107.5f);
    private static WorldState State(SimulationWorld world) => (WorldState)typeof(SimulationWorld).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
    private static SimulationWorld World(bool onPath)
    {
        var setup = new WorldSetup { CameraVisibility = true };
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
        setup.Players.Add(new PlayerSpawn(2, "earth", onPath ? HiddenBase : new Vector2(220, 300), new Vector2(220, 310)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 31);
        var squad = State(world).Players[0].Squads[1];
        squad.Position = squad.Destination = new Vector2(100, 107.5f);
        VisibilitySystem.Refresh(State(world));
        return world;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenBuildingDoesNotChangeEmptySquadPoseOrBecomeObserved(bool moveTo)
    {
        var onPath = World(true); var elsewhere = World(false);
        var command = moveTo ? SimCommand.MoveTo(1, 2, HiddenBase + new Vector2(40, 0)) : SimCommand.Move(1, 2, Vector2.UnitX);
        foreach (var world in new[] { onPath, elsewhere })
        {
            world.TryEnqueue(1, command);
            world.TryEnqueue(1, SimCommand.Focus(2, 2));
        }
        for (int tick = 0; tick < 500; tick++)
        {
            onPath.Step(); elsewhere.Step();
            var actual = onPath.GetView(1); var expected = elsewhere.GetView(1);
            Assert.Equal(expected.Squads[1].Position, actual.Squads[1].Position);
            Assert.Equal(expected.Squads[1].Direction, actual.Squads[1].Direction);
            Assert.Equal(expected.Squads[1].Heading, actual.Squads[1].Heading);
            Assert.DoesNotContain(actual.Buildings, building => building.OwnerId == 2);
            Assert.DoesNotContain(actual.Units, unit => unit.OwnerId == 2);
            Assert.False(actual.CanSeePoint(HiddenBase));
        }
        var resumed = SimulationWorld.RestoreMigrationState(State(onPath).Rules,
            JsonConvert.DeserializeObject<MigrationWorldState>(JsonConvert.SerializeObject(onPath.CaptureMigrationState()))!);
        onPath.Step(); resumed.Step();
        Assert.Equal(onPath.GetView(1).Squads[1].Position, resumed.GetView(1).Squads[1].Position);
        Assert.DoesNotContain(resumed.GetView(1).Buildings, building => building.OwnerId == 2);
    }

    [Fact]
    public void ReinforcementRevealsBuildingBeforeCenterIsMovedOutsideAndCanThenJoin()
    {
        var world = World(true);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 2, HiddenBase));
        for (int tick = 0; tick < 400; tick++) world.Step();
        Assert.Equal(HiddenBase, world.GetView(1).Squads[1].Position);
        Assert.DoesNotContain(world.GetView(1).Buildings, building => building.OwnerId == 2);
        var state = State(world);
        var traveller = state.AddUnit(1, state.Rules.ElementIndex("water"), HiddenBase - new Vector2(24, 0));
        traveller.SquadId = 0; traveller.DestinationSquadId = 2; traveller.IsReinforcing = true;
        world.Step();
        Assert.Equal(HiddenBase, world.GetView(1).Squads[1].Position);
        Assert.Contains(world.GetView(1).Buildings, building => building.OwnerId == 2);
        uint revision = world.GetView(1).Squads[1].MovementRevision;
        world.Step();
        var center = world.GetView(1).Squads[1];
        Assert.True(Vector2.Distance(center.Position, HiddenBase) >= state.Rules.BaseRadius - .001f);
        Assert.InRange(Vector2.Distance(center.Position, HiddenBase), 0, state.Rules.BaseRadius + state.Rules.SlotSpacing);
        Assert.True(center.MovementRevision > revision);
        for (int tick = 0; tick < 150; tick++) world.Step();
        Assert.False(traveller.IsReinforcing);
        Assert.Equal((byte)2, traveller.SquadId);
        Assert.True(Vector2.Distance(traveller.Position, HiddenBase) >= state.Rules.BaseRadius - .001f);
    }

    [Fact]
    public void EmptyGuardCenterInsideHiddenBuildingSurvivesMigrationAndDiskRestore()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(130.5f, 100), new Vector2(140, 110)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 31);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 8, new Vector2(125, 100)));
        for (int tick = 0; tick < 100; tick++) world.Step();
        Assert.Equal(new Vector2(125, 100), world.GetView(1).Squads[3].Position);
        Assert.DoesNotContain(world.GetView(1).Buildings, building => building.OwnerId == 2);
        var state = State(world);
        var resumed = SimulationWorld.RestoreMigrationState(state.Rules, world.CaptureMigrationState());
        var disk = SimulationWorld.RestoreCheckpoint(state.Rules, world.CaptureCheckpoint());
        Assert.Equal(world.GetView(1).Squads[3].Position, resumed.GetView(1).Squads[3].Position);
        Assert.Equal(world.GetView(1).Squads[3].Position, disk.GetView(1).Squads[3].Position);
        var tampered = Newtonsoft.Json.Linq.JObject.FromObject(world.CaptureMigrationState());
        tampered["World"]!["Players"]![0]!["Squads"]![3]!["Position"] = Newtonsoft.Json.Linq.JObject.FromObject(new Vector2(100));
        Assert.Throws<System.ArgumentException>(() => SimulationWorld.RestoreMigrationState(state.Rules, tampered.ToObject<MigrationWorldState>()!));
        state.AddUnit(1, state.Rules.ElementIndex("water"), new Vector2(110, 100), garrison: true);
        Assert.Throws<System.ArgumentException>(() => SimulationWorld.RestoreMigrationState(state.Rules, world.CaptureMigrationState()));
    }

    [Fact]
    public void TrainingTheFirstGuardKeepsTheTickCheckpointRestorable()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 90), cores: 1000, natureForge: true));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(100, 130.5f), new Vector2(110, 140)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 90)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 31);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 8, new Vector2(100, 125)));
        for (int tick = 0; tick < 100; tick++) world.Step();
        Assert.Equal(new Vector2(100, 125), world.GetView(1).Squads[3].Position);
        Assert.DoesNotContain(world.GetView(1).Buildings, building => building.OwnerId == 2);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(2, "water", 1, 4)));
        for (int tick = 0; tick < 300 && world.GetView(1).Squads[3].JoinedCount == 0; tick++) world.Step();
        Assert.Equal(1, world.GetView(1).Squads[3].JoinedCount);
        SimulationWorld.RestoreMigrationState(State(world).Rules, world.CaptureMigrationState());
        Assert.True(Vector2.Distance(world.GetView(1).Squads[3].Position, new Vector2(100, 130.5f)) >= State(world).Rules.BaseRadius - .001f);
    }

    [Fact]
    public void TransferringTheFirstGuardKeepsTheTickCheckpointRestorable()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(130.5f, 100), new Vector2(140, 110)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 31);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 8, new Vector2(125, 100)));
        for (int tick = 0; tick < 100; tick++) world.Step();
        Assert.Equal(new Vector2(125, 100), world.GetView(1).Squads[3].Position);
        Assert.DoesNotContain(world.GetView(1).Buildings, building => building.OwnerId == 2);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Transfer(2, 1, 4, 1)));
        world.Step();
        Assert.Equal(1, world.GetView(1).Squads[3].JoinedCount);
        SimulationWorld.RestoreMigrationState(State(world).Rules, world.CaptureMigrationState());
        Assert.True(Vector2.Distance(world.GetView(1).Squads[3].Position, new Vector2(130.5f, 100)) >= State(world).Rules.BaseRadius - .001f);
    }
}
