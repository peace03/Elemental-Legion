using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;
namespace ElementalLegion.Tests;

public sealed class DefenseProtocolTests
{
    [Fact]
    public void RaidAlertIsOwnerOnlyAndNeverCarriesAttackerMetadata()
    {
        var rules=GameRulesTests.Rules();var setup=WorldCoreTests.Setup();
        setup.Players.Add(new PlayerSpawn(2,"fire",new Vector2(80,50),new Vector2(70,50)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(50)));
        setup.Units.Add(new UnitSpawn(2,"fire",new Vector2(70,50)));
        var world=new SimulationWorld(rules,setup,1);
        var alert=new SimEvent((SimEventKind)12,1,2,1,new Vector2(50),3,123,45,rules.ElementIndex("fire"),new Vector2(70,50),true,1);
        foreach(int owner in new[]{1,2})
        {
            WorldView view=world.GetView(owner);view.EventBuffer.Add(alert);
            using var replica=new GameplayReplica(rules);var sender=new GameplayReplicationState(rules);var writer=new NetDataWriter();
            sender.Capture(view);sender.Prepare(view,1);
            for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);replica.TryAccept(new NetDataReader(writer.CopyData()),0);}
            var fog=new FogStateReplica(rules);var fogSender=new FogReplicationState(rules);fogSender.Prepare(view);
            for(int part=0;part<fogSender.ChunkCount;part++){fogSender.WriteChunk(writer,part);fog.TryAccept(new NetDataReader(writer.CopyData()));}
            Assert.True(replica.TryPublish(fog,out var frame));
            if(owner==2){Assert.Empty(frame.Events.ToArray());continue;}
            SimEvent decoded=Assert.Single(frame.Events.ToArray()).Data;
            Assert.Equal((SimEventKind)12,decoded.Kind);Assert.Equal(1,decoded.OwnerId);Assert.Equal(new Vector2(50),decoded.Position);Assert.Equal(3,decoded.Amount);
            Assert.Equal(0,decoded.SourceId);Assert.Equal(0,decoded.TargetId);Assert.Equal(0,decoded.EntityId);Assert.Equal(0,decoded.Detail);Assert.Equal(0UL,decoded.Sequence);
            Assert.Equal(-1,decoded.Element);Assert.False(decoded.HasSourcePosition);Assert.Equal(Vector2.Zero,decoded.SourcePosition);
        }
    }

    [Fact]
    public void ClientRejectsAnAlertAddressedToAnotherOwner()
    {
        var rules=GameRulesTests.Rules();var world=new SimulationWorld(rules,WorldCoreTests.Setup(),1);var view=world.GetView(1);
        var body=new NetDataWriter();var packet=new NetDataWriter();
        GameplayCodec.WriteBody(body,view,new[]{new NetworkEvent(1,view.Tick,new SimEvent((SimEventKind)12,2,0,0,view.BasePosition,1))},1);
        using var replica=new GameplayReplica(rules);
        for(int part=0;part<(body.Length+GameplayCodec.PayloadBytes-1)/GameplayCodec.PayloadBytes;part++)
        {GameplayCodec.WriteChunk(packet,body,view.Tick,1,1,1,part);replica.TryAccept(new NetDataReader(packet.CopyData()),0);}
        Assert.True(replica.Overflowed);Assert.Equal(-1,replica.State.Tick);
    }
}