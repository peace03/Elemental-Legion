using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>신뢰 전투 프레임의 원자 반영·틱 사이 보존·시야 재검사를 검사한다.</summary>
public sealed class M3GameplayTests
{
    [Fact]
    public void RallyStateBelongsToTheOwnerAndRequiresAReserve()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(40), new Vector2(100)));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200), new Vector2(250)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(40), garrison: true));
        setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(47, 40), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.False(world.GetView(1).IsRallying);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Rally(1)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.Rally(1)));
        world.Step();
        Assert.True(world.GetView(1).IsRallying);
        Assert.False(world.GetView(2).IsRallying);

        var sender = new GameplayReplicationState(rules);
        using var receiver = new GameplayReplica(rules);
        sender.Prepare(world.GetView(1), 1);
        var writer = new NetDataWriter();
        sender.WriteChunk(writer, 0);
        byte[] packet = writer.CopyData();
        Assert.True(receiver.TryAccept(new NetDataReader(packet), 0));
        Assert.True(receiver.TryPublish(Fog(world.GetView(1)), out var frame));
        Assert.True(frame.IsRallying);
        Assert.Equal(2, frame.RallyingCount);
        Assert.Equal(0, frame.WaitingGarrisonCount);

        receiver.Reset();
        packet[GameplayCodec.HeaderBytes + 40] = 3;
        Assert.False(receiver.TryAccept(new NetDataReader(packet), 0));
        Assert.True(receiver.Overflowed);
    }

    [Fact]
    public void GarrisonArrivalIsReplicatedWhenTheLastReserveJoins()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200, 200), new Vector2(200, 245)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 210), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        var view = world.GetView(1);
        Assert.Equal(0, view.GarrisonArrivals);
        world.TryEnqueue(1, SimCommand.Rally(1));
        world.Step();
        Assert.True(view.IsRallying);
        for (int tick = 0; tick < 300 && view.GarrisonCount > 0; tick++) world.Step();
        Assert.False(view.IsRallying);
        Assert.Equal(0, view.GarrisonCount);
        Assert.Equal(1, view.GarrisonArrivals);

        var sender = new GameplayReplicationState(rules);
        using var receiver = new GameplayReplica(rules);
        sender.Prepare(view, 1);
        var writer = new NetDataWriter();
        sender.WriteChunk(writer, 0);
        Assert.True(receiver.TryAccept(new NetDataReader(writer.CopyData()), 0));
        Assert.True(receiver.TryPublish(Fog(view), out var frame));
        Assert.Equal(1, frame.GarrisonArrivals);
        Assert.Equal(0, frame.RallyingCount);
        Assert.False(frame.IsRallying);
    }

    [Fact]
    public void GameplayBaselineIsAtomicAndPublishesOwnedStateAndObservedObjects()
    {
        var rules=GameRulesTests.Rules();var setup=WorldCoreTests.Setup(cores:100,level:3);
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(50)));
        for(int i=0;i<200;i++)setup.Cores.Add(new CoreSpawn(new Vector2(55),1));
        var world=new SimulationWorld(rules,setup,1);var view=world.GetView(1);
        var sender=new GameplayReplicationState(rules);sender.Prepare(view,1);var receiver=new GameplayReplica(rules);receiver.BindPlayer(1);
        var writer=new NetDataWriter();Assert.True(sender.ChunkCount>1);
        for(int part=sender.ChunkCount-1;part>=0;part--)
        {
            sender.WriteChunk(writer,part);Assert.InRange(writer.Length,1,1024);
            Assert.Equal(part==0,receiver.TryAccept(new NetDataReader(writer.CopyData()),0));
            Assert.Equal(-1,receiver.State.Tick);
        }
        var fog=Fog(view);Assert.True(receiver.TryPublish(fog,out var frame));
        Assert.Equal(100,frame.CoreBalance);Assert.Equal(200,frame.Cores.Length);Assert.Equal(view.Buildings.Count,frame.Buildings.Length);
        Assert.Equal(1,frame.ElementCounts[rules.ElementIndex("water")]);Assert.True(frame.SpatialReady);
        receiver.Reset();Assert.Equal(-1,receiver.State.Tick);Assert.Equal(0,receiver.PendingBytes);
    }

    [Fact]
    public void CaptureKeepsOddTickCommandFailureUntilTheNextNetworkFlush()
    {
        var rules=GameRulesTests.Rules();var world=new SimulationWorld(rules,WorldCoreTests.Setup(cores:0),1);
        var sender=new GameplayReplicationState(rules);var receiver=new GameplayReplica(rules);receiver.BindPlayer(1);
        world.TryEnqueue(1,SimCommand.Train(1,"water"));world.Step();sender.Capture(world.GetView(1));
        world.Step();sender.Capture(world.GetView(1));Assert.Empty(world.GetView(1).Events);
        sender.Prepare(world.GetView(1),1);var writer=new NetDataWriter();
        for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);receiver.TryAccept(new NetDataReader(writer.CopyData()),0);}
        Assert.True(receiver.TryPublish(Fog(world.GetView(1)),out var frame));
        Assert.Single(frame.Events.ToArray(),e=>e.Data.Kind==SimEventKind.CommandRejected&&e.Data.Sequence==1);
    }

    [Fact]
    public void EveryReliableFrameWaitsForFogWithoutDroppingEarlierResults()
    {
        var rules=GameRulesTests.Rules();var world=new SimulationWorld(rules,WorldCoreTests.Setup(cores:0),1);
        var sender=new GameplayReplicationState(rules);var receiver=new GameplayReplica(rules);receiver.BindPlayer(1);var writer=new NetDataWriter();
        for(ulong sequence=1;sequence<=2;sequence++)
        {
            world.TryEnqueue(1,SimCommand.Train(sequence,"water"));world.Step();sender.Capture(world.GetView(1));sender.Prepare(world.GetView(1),1);
            for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);receiver.TryAccept(new NetDataReader(writer.CopyData()),0);}
            sender.Commit();
        }
        Assert.False(receiver.TryPublish(new FogStateReplica(rules),out _));var fog=Fog(world.GetView(1));
        Assert.True(receiver.TryPublish(fog,out var first));Assert.Equal(1UL,Assert.Single(first.Events.ToArray()).Data.Sequence);
        Assert.True(receiver.TryPublish(fog,out var second));Assert.Equal(2UL,Assert.Single(second.Events.ToArray()).Data.Sequence);
        Assert.False(receiver.TryPublish(fog,out _));
    }

    private static FogStateReplica Fog(WorldView view)
    {
        var replica=new FogStateReplica(GameRulesTests.Rules());var sender=new FogReplicationState(GameRulesTests.Rules());sender.Prepare(view);var writer=new NetDataWriter();
        for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);replica.TryAccept(new NetDataReader(writer.CopyData()));}return replica;
    }

    [Fact]
    public void RemovedCoreIsForgottenByTypedIdAndCannotReturnFromAnOlderFrame()
    {
        var rules=GameRulesTests.Rules();var setup=new WorldSetup();setup.Players.Add(new PlayerSpawn(1,"water",new Vector2(1000),new Vector2(100)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(100)));setup.Cores.Add(new CoreSpawn(new Vector2(100),5));
        var world=new SimulationWorld(rules,setup,1);var fog=new FogReplicationState(rules);var games=new GameplayReplicationState(rules);var replica=new GameplayReplica(rules);replica.BindPlayer(1);
        var view=world.GetView(1);int id=Assert.Single(view.Cores).Id;fog.Prepare(view);games.Prepare(view,1);var writer=new NetDataWriter();
        for(int part=0;part<games.ChunkCount;part++){games.WriteChunk(writer,part);replica.TryAccept(new NetDataReader(writer.CopyData()),0);}
        fog.Commit();games.Commit();world.Step();fog.Prepare(view);Assert.True(fog.ForgetChunkCount>0);fog.WriteForget(writer,0);
        var entities=new ObservedEntityId[256];Assert.True(FogProtocol.TryReadEntityForget(new NetDataReader(writer.CopyData()),entities,out long tick,out _,out _,out int count));
        Assert.Equal(1,count);Assert.Equal(new ObservedEntityId(ObservedEntityKind.Core,id),entities[0]);
        replica.Forget(entities[0],tick);Assert.True(replica.TryPublish(Fog(view),out var previous));Assert.Empty(previous.Cores.ToArray());
    }
}
