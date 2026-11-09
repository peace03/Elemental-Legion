using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;
namespace ElementalLegion.Tests;
public sealed class RecallProtocolTests
{
    [Fact]
    public void RecallAdmitsOnlyOneOwnedFieldSquadWithAnExactPayload()
    {
        var rules=GameRulesTests.Rules();var writer=new NetDataWriter();
        writer.Put((byte)38);writer.Put(7UL);writer.Put((byte)2);byte[] valid=writer.CopyData();
        Assert.True(Protocol.TryReadCommand(new NetDataReader(valid),rules,out var command));
        Assert.Equal((CommandKind)9,command.Kind);Assert.Equal((byte)2,command.TargetSquad);Assert.Equal(7UL,command.Sequence);
        for(int length=0;length<valid.Length;length++)Assert.False(Protocol.TryReadCommand(new NetDataReader(valid[..length]),rules,out _));
        Assert.False(Protocol.TryReadCommand(new NetDataReader(valid.Concat(new byte[1]).ToArray()),rules,out _));
        foreach(byte squad in new byte[]{0,4,7,255})
        {byte[] invalid=(byte[])valid.Clone();invalid[9]=squad;Assert.False(Protocol.TryReadCommand(new NetDataReader(invalid),rules,out _));}
        Array.Clear(valid,1,8);Assert.False(Protocol.TryReadCommand(new NetDataReader(valid),rules,out _));
        Protocol.WriteRecall(writer,8,3);Assert.Equal(10,writer.Length);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()),rules,out command));Assert.Equal((byte)3,command.TargetSquad);
    }

    [Fact]
    public void CastAndReservedSlotsArePrivateToEachRecipient()
    {
        var rules=GameRulesTests.Rules();var setup=WorldCoreTests.Setup();
        setup.Players.Add(new PlayerSpawn(2,"fire",new Vector2(500),new Vector2(520)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(70)));
        setup.Units.Add(new UnitSpawn(2,"fire",new Vector2(520)));
        var world=new SimulationWorld(rules,setup,1);world.TryEnqueue(1,SimCommand.Recall(1,1));world.Step();
        Assert.Equal((byte)1,world.GetView(1).RecallSquad);
        foreach(int owner in new[]{1,2})
        {
            var view=world.GetView(owner);using var receiver=new GameplayReplica(rules);var writer=new NetDataWriter();
            var sender=new GameplayReplicationState(rules);sender.Prepare(view,1);
            for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);receiver.TryAccept(new NetDataReader(writer.CopyData()),0);}
            var fog=new FogStateReplica(rules);var fogSender=new FogReplicationState(rules);fogSender.Prepare(view);
            for(int part=0;part<fogSender.ChunkCount;part++){fogSender.WriteChunk(writer,part);fog.TryAccept(new NetDataReader(writer.CopyData()));}
            Assert.True(receiver.TryPublish(fog,out var frame));Assert.Equal(view.RecallSquad,frame.RecallSquad);
            Assert.Equal(owner==1?(byte)1:(byte)0,frame.RecallSquad);Assert.Equal(view.RecallEndTick,frame.RecallEndTick);
            Assert.Equal(view.RecallReadyTick,frame.RecallReadyTick);Assert.Equal(view.RecallArrivalSlots.ToArray(),frame.RecallArrivalSlots.ToArray());
            Assert.All(frame.RecallArrivalSlots.ToArray(),slot=>Assert.InRange(Vector2.Distance(slot,view.BasePosition),rules.BaseRadius,rules.BuildRadius));
        }
    }

    [Fact]
    public void NonFiniteReservedSlotNeverPublishesAPartialGameplayFrame()
    {
        var rules=GameRulesTests.Rules();var world=new SimulationWorld(rules,WorldCoreTests.Setup(),1);
        var sender=new GameplayReplicationState(rules);sender.Prepare(world.GetView(1),1);var writer=new NetDataWriter();
        using var receiver=new GameplayReplica(rules);
        for(int part=0;part<sender.ChunkCount;part++)
        {
            sender.WriteChunk(writer,part);byte[] bytes=writer.CopyData();
            if(part==0)BitConverter.GetBytes(float.NaN).CopyTo(bytes,GameplayCodec.HeaderBytes+352);
            receiver.TryAccept(new NetDataReader(bytes),0);
        }
        Assert.True(receiver.Overflowed);Assert.Equal(-1,receiver.State.Tick);
    }
}
