using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>시야 기준·변경분의 원자 적용과 잘못된 조각의 격리를 검사한다.</summary>
public sealed class FogProtocolTests
{
    private static SimulationWorld World()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(1000), new Vector2(100)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        return new SimulationWorld(GameRulesTests.Rules(), setup, 1);
    }

    [Fact]
    public void BaselineIsAtomicHandlesReverseOrderAndDoesNotAcceptTruncation()
    {
        var rules = GameRulesTests.Rules(); var world = World();
        var sender = new FogReplicationState(rules); var replica = new FogStateReplica(rules); var writer = new NetDataWriter();
        Assert.True(sender.Prepare(world.GetView(1)));
        Assert.True(sender.ChunkCount > 1);
        for (int part = sender.ChunkCount - 1; part >= 0; part--)
        {
            sender.WriteChunk(writer, part); Assert.InRange(writer.Length, 1, Protocol.MaxDatagramBytes);
            Assert.Equal(part == 0, replica.TryAccept(new NetDataReader(writer.CopyData())));
            if (part > 0) Assert.False(replica.Ready);
        }
        Assert.True(replica.Ready); Assert.Equal(1U, replica.Revision);
        Assert.Equal(world.GetView(1).Vision.Current.ToArray(), replica.Current.ToArray());
        Assert.False(replica.TryAccept(new NetDataReader(writer.CopyData())));
        replica.Reset();
        byte[] bytes = writer.CopyData();
        for (int length = 0; length < bytes.Length; length++) Assert.False(replica.TryAccept(new NetDataReader(bytes[..length])));
        Assert.False(replica.Ready);
    }

    [Fact]
    public void DeltaKeepsExplorationAndRejectsMissingBaselineOrWrongRecipient()
    {
        var rules = GameRulesTests.Rules(); var world = World(); var sender = new FogReplicationState(rules);
        var replica = new FogStateReplica(rules); var writer = new NetDataWriter();
        Send(sender, replica, world.GetView(1));
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX));
        for (int tick = 0; tick < 220; tick++) world.Step();
        Assert.True(sender.Prepare(world.GetView(1)));
        sender.WriteChunk(writer, 0); byte[] delta = writer.CopyData();
        Assert.False(new FogStateReplica(rules).TryAccept(new NetDataReader(delta)));
        for (int part = 0; part < sender.ChunkCount; part++) { sender.WriteChunk(writer, part); replica.TryAccept(new NetDataReader(writer.CopyData())); }
        sender.Commit();
        Assert.Equal(VisionCellState.Explored, replica.GetState(10, 10));
        Assert.Equal(world.GetView(1).Vision.Current.ToArray(), replica.Current.ToArray());
        Assert.False(replica.BindPlayer(2)); Assert.True(replica.BindPlayer(1));
        replica.Reset(); Assert.False(replica.Ready); Assert.Equal(0U, replica.Revision);
        Assert.Equal(VisionCellState.Unexplored, replica.GetState(10, 10));
    }

    [Fact]
    public void ForgetMessagesContainOnlyPreviouslyObservedIdsAndNoState()
    {
        var rules = GameRulesTests.Rules(); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(1000), new Vector2(100)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        setup.Units.Add(new UnitSpawn(-1, "fire", new Vector2(120, 100)));
        setup.Units.Add(new UnitSpawn(-1, "ice", new Vector2(500)));
        var world = new SimulationWorld(rules, setup, 1); var sender = new FogReplicationState(rules); var replica = new FogStateReplica(rules);
        int target = world.GetView(1).Units.Single(unit => unit.OwnerId == -1).Id;
        Send(sender, replica, world.GetView(1));
        world.TryEnqueue(1, SimCommand.Move(1, -Vector2.UnitX));
        for (int tick = 0; tick < 100; tick++) world.Step();
        Assert.True(sender.Prepare(world.GetView(1)));
        var writer = new NetDataWriter(); sender.WriteForget(writer, 0);
        var ids = new int[Protocol.MaxUnits];
        Assert.True(FogProtocol.TryReadForget(new NetDataReader(writer.CopyData()), ids, out long at, out uint revision, out int owner, out int count));
        Assert.Equal(1, owner); Assert.Equal(world.Metrics.Tick, at); Assert.Equal(sender.Revision, revision);
        Assert.Single(ids.Take(count)); Assert.Equal(target, ids[0]); Assert.Equal(FogProtocol.ForgetHeaderBytes + 5, writer.Length);
    }

    [Fact]
    public void V1HelloIsRejectedAndUnitsCarryVisibilityRevision()
    {
        var rules = GameRulesTests.Rules(); var writer = new NetDataWriter();
        Protocol.WriteHello(writer, rules.DataHash, rules.StartingElements[0]); byte[] old = writer.CopyData(); old[1] = 1;
        Assert.Equal(JoinFailure.VersionMismatch, Protocol.ReadHello(new NetDataReader(old), rules, out _));
        Protocol.WriteUnits(writer, World().GetView(1), rules.MapSize, 0, 7);
        var units = new UnitSnapshotAssembler(rules);
        Assert.True(units.TryAccept(new NetDataReader(writer.CopyData()))); Assert.Equal(7U, units.Revision);
    }

    private static void Send(FogReplicationState sender, FogStateReplica replica, WorldView view)
    {
        sender.Prepare(view); var writer = new NetDataWriter();
        for (int part = 0; part < sender.ChunkCount; part++) { sender.WriteChunk(writer, part); replica.TryAccept(new NetDataReader(writer.CopyData())); }
        sender.Commit();
    }
}
