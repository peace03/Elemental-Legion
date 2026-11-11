using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>지점 이동의 우회와 빈 분대 명령, 귀환 이후의 진형 복귀를 검증한다.</summary>
public sealed class MovementFeedbackTests
{
    private static WorldState State(SimulationWorld world) => (WorldState)typeof(SimulationWorld).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
    private static SimulationWorld World(Vector2 start, Vector2 obstacle, int count = 1)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", obstacle, start, baseLevel: 4));
        for (int i = 0; i < count; i++) setup.Units.Add(new UnitSpawn(1, "water", start + new Vector2(i * 1.5f, 0)));
        return new SimulationWorld(GameRulesTests.Rules(), setup, 31);
    }

    [Theory]
    [InlineData(50, 50, 60, 50, 80, 50)]
    [InlineData(50, 1, 60, 2, 80, 1)]
    public void MoveToRoundsBuildingWithoutCrossingIt(float x, float y, float bx, float by, float gx, float gy)
    {
        var world = World(new Vector2(x, y), new Vector2(bx, by));
        Vector2 goal = new Vector2(gx, gy);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 1, goal));
        AdvanceSafely(world, 220, continuous: true);
        Assert.Equal(goal, world.GetView(1).Squads[0].Position);
    }

    [Fact]
    public void MoveToRoundsSeveralBuildingsAndMigrationKeepsTheSameRoute()
    {
        var world = World(new Vector2(50), new Vector2(60, 50));
        var state = State(world);
        state.AddBuilding(state.Players[0], BuildingKind.NatureForge, new Vector2(70, 56));
        state.AddBuilding(state.Players[0], BuildingKind.NatureForge, new Vector2(70, 44));
        Assert.True(RecallSystem.TryCreateLayout(state, state.Players[0].BasePosition, out var slots));
        state.Players[0].RecallArrivalSlots = Array.AsReadOnly(slots);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 1, new Vector2(90, 50)));
        AdvanceSafely(world, 25);
        var resumed = SimulationWorld.RestoreMigrationState(state.Rules, world.CaptureMigrationState());
        for (int i = 0; i < 220; i++)
        {
            AdvanceSafely(world, 1); resumed.Step();
            Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(resumed.CaptureMigrationState()));
        }
        Assert.Equal(new Vector2(90, 50), world.GetView(1).Squads[0].Position);
    }

    [Fact]
    public void GoalInsideBuildingStopsSafelyAndRemovingObstacleAllowsMovement()
    {
        var world = World(new Vector2(50), new Vector2(60, 50));
        world.TryEnqueue(1, SimCommand.MoveTo(1, 1, new Vector2(60, 50)));
        AdvanceSafely(world, 100);
        Assert.InRange(world.GetView(1).Squads[0].Position.X, 53.99f, 54.001f);
        var state = State(world);
        var obstacle = state.Buildings[0];
        state.Buildings.Remove(obstacle); state.BuildingById.Remove(obstacle.Id);
        for (int i = 0; i < 100; i++) world.Step();
        Assert.Equal(new Vector2(60, 50), world.GetView(1).Squads[0].Position);
    }

    [Fact]
    public void ClearNarrowGapRemainsPassableAndBlockedGapNeverAllowsPenetration()
    {
        foreach (float gap in new[] { .1f, -.1f })
        {
            var world = World(new Vector2(50), new Vector2(60, 56 + gap));
            var state = State(world);
            state.AddBuilding(state.Players[0], BuildingKind.Base, new Vector2(60, 44 - gap));
            world.TryEnqueue(1, SimCommand.MoveTo(1, 1, new Vector2(80, 50)));
            AdvanceSafely(world, 300);
            Assert.Equal(new Vector2(80, 50), world.GetView(1).Squads[0].Position);
        }
    }

    [Fact]
    public void RallyIntoStationarySquadDoesNotLeaveStackedUnits()
    {
        var world = World(new Vector2(100, 115), new Vector2(100), 8);
        var state = State(world);
        for (int i = 0; i < 4; i++) state.AddUnit(1, state.Rules.ElementIndex("water"), new Vector2(110, 100 + i * 1.5f), garrison: true);
        world.TryEnqueue(1, SimCommand.Rally(1, 1, 4));
        for (int i = 0; i < 300; i++) world.Step();
        Assert.Equal(12, world.GetView(1).Squads[0].JoinedCount);
        for (int i = 0; i < state.Units.Count; i++)
            for (int j = i + 1; j < state.Units.Count; j++)
                Assert.True(Vector2.Distance(state.Units[i].Position, state.Units[j].Position) >= state.Rules.MinimumSeparation - .01f);
    }

    [Fact]
    public void EmptySquadKeepsDestinationWhenReinforcementIsSent()
    {
        var world = World(new Vector2(100, 110), new Vector2(100));
        var state = State(world);
        state.AddUnit(1, state.Rules.ElementIndex("water"), new Vector2(110, 110), garrison: true);
        var goal = new Vector2(140, 120);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 2, goal));
        for (int i = 0; i < 150; i++) world.Step();
        Assert.Equal(goal, world.GetView(1).Squads[1].Position);
        Assert.Equal(world.GetView(1).BasePosition, SquadSystem.FocusPosition(state.Players[0], 0));
        world.TryEnqueue(1, SimCommand.Rally(2, 2, 1));
        for (int i = 0; i < 200; i++) world.Step();
        Assert.Equal(goal, world.GetView(1).Squads[1].Position);
        Assert.Equal(1, world.GetView(1).Squads[1].JoinedCount);
    }

    [Fact]
    public void EmptySquadCenterSurvivesReconnectWithoutGainingRemoteVision()
    {
        var world = World(new Vector2(100, 110), new Vector2(100));
        var goal = new Vector2(180, 110);
        world.TryEnqueue(1, SimCommand.MoveTo(1, 2, goal));
        for (int tick = 0; tick < 300; tick++) world.Step();
        Assert.Equal(goal, world.GetView(1).Squads[1].Position);
        Assert.True(world.BeginGhost(1));
        Assert.True(world.TryResumeGhost(1));
        Assert.Equal(goal, world.GetView(1).Squads[1].Position);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Focus(1, 2)));
        world.Step();
        Assert.Equal(goal, world.GetView(1).Squads[1].Position);
        Assert.Equal(new Vector2(100), SquadSystem.FocusPosition(State(world), State(world).Players[0]));
    }

    [Fact]
    public void NewEmptySquadsStartBesideBaseAndAcceptTheFirstReinforcementThere()
    {
        var world = World(new Vector2(100, 110), new Vector2(100));
        foreach (SquadView squad in world.GetView(1).Squads.Skip(1))
            Assert.InRange(Vector2.Distance(squad.Position, world.GetView(1).BasePosition), State(world).Rules.BaseRadius, State(world).Rules.GarrisonRadius);
        State(world).AddUnit(1, State(world).Rules.ElementIndex("water"), new Vector2(110, 110), garrison: true);
        world.TryEnqueue(1, SimCommand.Rally(1, 2, 1));
        for (int tick = 0; tick < 100; tick++) world.Step();
        Assert.Equal(1, world.GetView(1).Squads[1].JoinedCount);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(300), new Vector2(300, 310))));
        foreach (SquadView squad in world.GetView(2).Squads.Skip(1))
            Assert.InRange(Vector2.Distance(squad.Position, world.GetView(2).BasePosition), State(world).Rules.BaseRadius, State(world).Rules.GarrisonRadius);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecallKeepsArrivalSlotsUntilMovementThenRestoresRoleFormation(bool moveTo)
    {
        var world = World(new Vector2(200), new Vector2(100), 8);
        world.TryEnqueue(1, SimCommand.Recall(1, 1));
        for (int i = 0; i < 61; i++) world.Step();
        var state = State(world);
        var arrivals = state.Units.Select(unit => unit.Position).ToArray();
        for (int i = 0; i < 5; i++) world.Step();
        Assert.Equal(arrivals, state.Units.Select(unit => unit.Position));
        world.TryEnqueue(1, moveTo ? SimCommand.MoveTo(2, 1, new Vector2(150, 130)) : SimCommand.Move(2, 1, Vector2.UnitX));
        world.Step();
        var members = state.Players[0].Squads[0].Units.OrderBy(unit => unit.Role).ThenBy(unit => unit.Id).ToArray();
        for (int i = 0; i < members.Length; i++)
            Assert.Equal(new Vector2(i % 3 - 1, 1 - i / 3) * state.Rules.SlotSpacing, members[i].Slot);
    }

    private static void AdvanceSafely(SimulationWorld world, int ticks, bool continuous = false)
    {
        var state = State(world);
        float step = state.Rules.LegionSpeed * (float)state.Rules.DeltaTime;
        for (int i = 0; i < ticks; i++)
        {
            Vector2 before = world.GetView(1).Squads[0].Position;
            world.Step();
            SquadView squad = world.GetView(1).Squads[0];
            Vector2 delta = squad.Position - before;
            Assert.InRange(delta.Length(), continuous && squad.Order == SquadOrderKind.MoveTo ? step * .9f : 0, step + .001f);
            foreach (var building in state.Buildings.Where(building => building.BlocksMovement))
            {
                float fraction = delta == Vector2.Zero ? 0 : Math.Clamp(Vector2.Dot(building.Position - before, delta) / delta.LengthSquared(), 0, 1);
                Assert.True(Vector2.Distance(before + delta * fraction, building.Position) >= building.Radius - .001f);
            }
        }
    }
}
