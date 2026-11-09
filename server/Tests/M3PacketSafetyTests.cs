using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class M3PacketSafetyTests
{
    [Fact]
    public void MissingReliableChunkCannotBeSilentlyReplacedAndWaitExpires()
    {
        var rules=GameRulesTests.Rules();var setup=WorldCoreTests.Setup();setup.Units.Add(new UnitSpawn(1,"water",new Vector2(50)));for(int i=0;i<200;i++)setup.Cores.Add(new CoreSpawn(new Vector2(55),1));
        var world=new SimulationWorld(rules,setup,1);var sender=new GameplayReplicationState(rules);var replica=new GameplayReplica(rules);var writer=new NetDataWriter();
        sender.Prepare(world.GetView(1),1);sender.WriteChunk(writer,0);byte[] first=writer.CopyData();
        Assert.False(replica.TryAccept(new NetDataReader(first),0));Assert.False(replica.HasExpired(2));Assert.True(replica.HasExpired(2.001));
        byte[] next=(byte[])first.Clone();BitConverter.GetBytes(2L).CopyTo(next,1);
        Assert.False(replica.TryAccept(new NetDataReader(next),1));Assert.True(replica.Overflowed);
        replica.Reset();Assert.False(replica.Overflowed);Assert.Equal(0,replica.PendingBytes);
    }

    [Fact]
    public void InvalidCompletedReliableFrameRequiresExplicitDisconnectInsteadOfLosingItsResults()
    {
        var rules=GameRulesTests.Rules();var world=new SimulationWorld(rules,WorldCoreTests.Setup(),1);var sender=new GameplayReplicationState(rules);var receiver=new GameplayReplica(rules);var writer=new NetDataWriter();
        sender.Prepare(world.GetView(1),1);sender.WriteChunk(writer,0);byte[] packet=writer.CopyData();packet[GameplayCodec.HeaderBytes+12]=255;
        Assert.False(receiver.TryAccept(new NetDataReader(packet),0));Assert.True(receiver.HasExpired(0));Assert.Equal(-1,receiver.State.Tick);
    }

    [Fact]
    public void HiddenAttackerFieldsAreStrippedFromDecodedEvents()
    {
        var document=GameRulesTests.Data();var json=document.Root.DeepClone();
        int light=GameRulesTests.Rules().ElementIndex("light");json["elements"]![light]!["range"]=50;
        var rules=new GameRules(new GameDataDocument(json.ToString()));var setup=new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1,"water",new Vector2(900),new Vector2(100)));
        setup.Players.Add(new PlayerSpawn(2,"fire",new Vector2(164,100),new Vector2(140,100)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(100)));
        setup.Units.Add(new UnitSpawn(2,"light",new Vector2(140,100),garrison:true));
        var world=new SimulationWorld(rules,setup,1);world.Step();var view=world.GetView(1);
        Assert.DoesNotContain(view.Units,unit=>unit.OwnerId==2);
        var sender=new GameplayReplicationState(rules);sender.Capture(view);sender.Prepare(view,1);var replica=new GameplayReplica(rules);var writer=new NetDataWriter();
        for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);replica.TryAccept(new NetDataReader(writer.CopyData()),0);}
        var fog=new FogStateReplica(rules);var fogSender=new FogReplicationState(rules);fogSender.Prepare(view);
        for(int part=0;part<fogSender.ChunkCount;part++){fogSender.WriteChunk(writer,part);fog.TryAccept(new NetDataReader(writer.CopyData()));}
        Assert.True(replica.TryPublish(fog,out var frame));var attack=Assert.Single(frame.Events.ToArray(),entry=>entry.Data.Kind==SimEventKind.Attack);
        Assert.Equal(0,attack.Data.SourceId);Assert.Equal(-1,attack.Data.OwnerId);Assert.Equal(-1,attack.Data.Element);Assert.False(attack.Data.HasSourcePosition);Assert.Equal(Vector2.Zero,attack.Data.SourcePosition);
        Assert.All(frame.Buildings.ToArray(),building=>Assert.Equal(1,building.OwnerId));
    }
}
