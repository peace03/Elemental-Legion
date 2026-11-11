using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class BuildingMemoryProtocolTests
{
    [Fact]
    public void EmptySiteRevisitPublishesEvenWhenItsFogCellDidNotChange()
    {
        var rules = GameRulesTests.Rules();
        Vector2 center = new Vector2(rules.CellSize * 10.25f);
        Vector2 partial = new Vector2(rules.CellSize * 10.75f);
        var building = new BuildingView(3, 3, BuildingKind.Collector, center, 1);
        var view = NewView(rules, 1);
        var sender = new FogReplicationState(rules);
        var replica = new FogStateReplica(rules);

        Observe(rules, view, 1, center, building);
        Assert.True(Send(sender, replica, view));
        Assert.True(replica.ClearedBuildingMemories.IsEmpty);
        byte[] visible = replica.Current.ToArray();
        Observe(rules, view, 2, partial);
        Assert.False(view.CanSeePoint(center));
        Assert.True(Send(sender, replica, view));
        Assert.True(replica.ClearedBuildingMemories.IsEmpty);
        Assert.Equal(visible, replica.Current.ToArray());

        Observe(rules, view, 3, partial);
        Assert.False(Send(sender, replica, view));
        Observe(rules, view, 4, center);
        Assert.True(view.CanSeePoint(center));
        Assert.True(Send(sender, replica, view));
        Assert.Equal(visible, replica.Current.ToArray());
        Assert.Equal(3U, replica.Revision);
        Assert.Equal(new[] { building.Id }, replica.ClearedBuildingMemories.ToArray());

        replica.Reset();
        Assert.True(replica.ClearedBuildingMemories.IsEmpty);
        Assert.True(Send(new FogReplicationState(rules), replica, view));
        Assert.True(replica.ClearedBuildingMemories.IsEmpty);
    }

    [Fact]
    public void HiddenCompletionDestructionAndReplacementClearOnlyThePriorRecipientsMemoryOnRevisit()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        var firstHome = new Vector2(60, 40);
        var secondHome = new Vector2(60, 180);
        var approach = new Vector2(106, 100);
        var otherApproach = new Vector2(108, 105);
        var site = new Vector2(124, 100);
        var ownerHome = new Vector2(165, 100);
        setup.Players.Add(new PlayerSpawn(1, "water", firstHome, approach));
        setup.Players.Add(new PlayerSpawn(2, "water", secondHome, secondHome));
        setup.Players.Add(new PlayerSpawn(3, "earth", ownerHome, ownerHome));
        setup.Units.Add(new UnitSpawn(1, "water", approach));
        setup.Units.Add(new UnitSpawn(2, "water", secondHome));
        var world = new SimulationWorld(rules, setup, 1);
        var state = (WorldState)typeof(SimulationWorld).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        PlayerState owner = state.PlayerById[3];
        state.AddBuilding(owner, BuildingKind.Collector, site, constructionEndTick: state.Tick + 3);
        BuildingState original = Assert.Single(state.Buildings, building => building.Kind == BuildingKind.Collector);
        world.Step();

        WorldView first = world.GetView(1), second = world.GetView(2);
        var firstSender = new FogReplicationState(rules);
        var secondSender = new FogReplicationState(rules);
        var firstReplica = new FogStateReplica(rules);
        var secondReplica = new FogStateReplica(rules);
        Assert.True(Assert.Single(first.Buildings, building => building.Id == original.Id).IsUnderConstruction);
        Assert.DoesNotContain(second.Buildings, building => building.Id == original.Id);
        Assert.True(Send(firstSender, firstReplica, first));
        Assert.True(Send(secondSender, secondReplica, second));

        MoveObserver(state, 1, firstHome);
        world.Step();
        Assert.False(first.CanSeePoint(site));
        Assert.True(Send(firstSender, firstReplica, first));
        Assert.False(Send(secondSender, secondReplica, second));
        Assert.True(firstReplica.ClearedBuildingMemories.IsEmpty);

        world.Step();
        Assert.False(original.IsUnderConstruction);
        Assert.False(Send(firstSender, firstReplica, first));
        Assert.False(Send(secondSender, secondReplica, second));

        original.Health = 0;
        world.Step();
        Assert.DoesNotContain(original, state.Buildings);
        Assert.False(Send(firstSender, firstReplica, first));
        Assert.False(Send(secondSender, secondReplica, second));

        state.AddBuilding(owner, BuildingKind.Collector, site);
        BuildingState replacement = Assert.Single(state.Buildings, building => building.Kind == BuildingKind.Collector);
        world.Step();
        Assert.DoesNotContain(first.Buildings, building => building.Id == replacement.Id);
        Assert.DoesNotContain(second.Buildings, building => building.Id == replacement.Id);
        Assert.False(Send(firstSender, firstReplica, first));
        Assert.False(Send(secondSender, secondReplica, second));

        MoveObserver(state, 1, approach);
        MoveObserver(state, 2, otherApproach);
        world.Step();
        Assert.Contains(first.Buildings, building => building.Id == replacement.Id);
        Assert.Contains(second.Buildings, building => building.Id == replacement.Id);
        Assert.True(Send(firstSender, firstReplica, first));
        Assert.True(Send(secondSender, secondReplica, second));
        Assert.Equal(new[] { original.Id }, firstReplica.ClearedBuildingMemories.ToArray());
        Assert.True(secondReplica.ClearedBuildingMemories.IsEmpty);
    }

    [Fact]
    public void CapacityEvictsTheOldestUnobservedIdsInACompleteDelta()
    {
        var rules = GameRulesTests.Rules();
        int cap = FogProtocol.MaxRememberedBuildings;
        var center = new Vector2(rules.CellSize * 10.25f);
        var remote = new Vector2(rules.CellSize * 20.25f);
        var thirdSite = new Vector2(rules.CellSize * 30.25f);
        var sender = new FogReplicationState(rules);
        var replica = new FogStateReplica(rules);
        var view = NewView(rules, 1);
        Observe(rules, view, 1, center, EnemyBuildings(1, cap, center));
        Assert.True(Send(sender, replica, view));

        Observe(rules, view, 2, remote, EnemyBuildings(cap + 1, 1, remote));
        Assert.True(Send(sender, replica, view));
        Assert.Equal(new[] { 1 }, replica.ClearedBuildingMemories.ToArray());

        Observe(rules, view, 3, thirdSite, EnemyBuildings(cap + 2, cap - 1, thirdSite));
        Assert.True(Send(sender, replica, view));
        Assert.Equal(Enumerable.Range(2, cap - 1), replica.ClearedBuildingMemories.ToArray());

        Observe(rules, view, 4, remote);
        Assert.True(Send(sender, replica, view));
        Assert.Equal(new[] { cap + 1 }, replica.ClearedBuildingMemories.ToArray());
    }

    [Theory]
    [InlineData(1, 0, -1)]
    [InlineData(1, -3, -1)]
    [InlineData(1, 5, 7)]
    [InlineData(2, 5, -1)]
    [InlineData(2, 5, 5)]
    [InlineData(2, 7, 5)]
    [InlineData(FogProtocol.MaxRememberedBuildings + 1, 1, -1)]
    public void MalformedMemoryListLeavesThePreviouslyAppliedCellsAndIdsUntouched(int clearCount, int firstId, int secondId)
    {
        var rules = GameRulesTests.Rules();
        var view = NewView(rules, 1);
        var replica = new FogStateReplica(rules);
        Observe(rules, view, 1, new Vector2(rules.CellSize * 10.25f));
        Assert.True(Send(new FogReplicationState(rules), replica, view));
        Assert.True(replica.TryAccept(MemoryDelta(2, false, 1, 11, -1)));

        Assert.False(replica.TryAccept(MemoryDelta(3, true, clearCount, firstId, secondId)));
        Assert.Equal(2U, replica.Revision);
        Assert.Equal(VisionCellState.Unexplored, replica.GetState(0, 0));
        Assert.Equal(new[] { 11 }, replica.ClearedBuildingMemories.ToArray());

        Assert.True(replica.TryAccept(MemoryDelta(3, true, 2, 5, 7)));
        Assert.Equal(VisionCellState.Visible, replica.GetState(0, 0));
        Assert.Equal(new[] { 5, 7 }, replica.ClearedBuildingMemories.ToArray());
    }

    private static void MoveObserver(WorldState state, int id, Vector2 position)
    {
        PlayerState player = state.PlayerById[id];
        player.LegionPosition = position;
        Assert.Single(player.Legion).Position = position;
    }

    private static BuildingView[] EnemyBuildings(int firstId, int count, Vector2 position) =>
        Enumerable.Range(firstId, count).Select(id => new BuildingView(id, 2, BuildingKind.Collector, position, 1)).ToArray();

    private static NetDataReader MemoryDelta(uint revision, bool cellChange, int clearCount, int firstId, int secondId)
    {
        var body = new NetDataWriter();
        body.Put(cellChange ? 1 : 0);
        if (cellChange)
        {
            body.Put(0);
            body.Put((byte)3);
        }
        body.Put((ushort)clearCount);
        body.Put(firstId);
        if (secondId != -1) body.Put(secondId);
        var writer = new NetDataWriter();
        FogProtocol.WriteChunk(writer, PacketKind.FogDelta, revision, 1, revision, revision - 1, body, 0);
        return new NetDataReader(writer.CopyData());
    }

    private static WorldView NewView(GameRules rules, int owner) => new()
    {
        PlayerId = owner,
        Vision = new VisionGrid(rules.MapSize, rules.CellSize)
    };

    private static void Observe(GameRules rules, WorldView view, long tick, Vector2 camera, params BuildingView[] buildings)
    {
        var sight = new CameraSight(camera, new Vector2(rules.CellSize * .05f), rules.MapSize);
        view.Tick = tick;
        view.ConfigureObservation(rules, new List<Vector2>(), sight);
        view.Vision.Begin();
        view.Vision.Reveal(sight);
        view.Vision.Commit();
        view.BuildingBuffer.Clear();
        view.BuildingBuffer.AddRange(buildings);
    }

    private static bool Send(FogReplicationState sender, FogStateReplica replica, WorldView view)
    {
        bool changed = sender.Prepare(view);
        var writer = new NetDataWriter();
        uint previousRevision = replica.Revision;
        int[] previousIds = replica.ClearedBuildingMemories.ToArray();
        for (int part = 0; part < sender.ChunkCount; part++)
        {
            sender.WriteChunk(writer, part);
            bool applied = replica.TryAccept(new NetDataReader(writer.CopyData()));
            Assert.Equal(part == sender.ChunkCount - 1, applied);
            if (!applied)
            {
                Assert.Equal(previousRevision, replica.Revision);
                Assert.Equal(previousIds, replica.ClearedBuildingMemories.ToArray());
            }
        }
        sender.Commit();
        return changed;
    }
}
