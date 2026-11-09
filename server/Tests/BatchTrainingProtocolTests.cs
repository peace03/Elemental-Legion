using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;
namespace ElementalLegion.Tests;
public sealed class BatchTrainingProtocolTests
{
    [Fact]
    public void LegacyCallerIncludesOneUnitAndGuardDestinationExplicitly()
    {
        var rules=GameRulesTests.Rules();var writer=new NetDataWriter();Protocol.WriteTrain(writer,7,rules.ElementIndex("water"));
        Assert.Equal(12,writer.Length);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()),rules,out var command));
        Assert.Equal(1,command.Count);Assert.Equal((byte)4,command.TargetSquad);
    }

    [Fact]
    public void BatchCountAndTargetHaveBoundedExactPayloads()
    {
        var rules=GameRulesTests.Rules();var writer=new NetDataWriter();
        writer.Put((byte)PacketKind.Train);writer.Put(8UL);writer.Put((byte)rules.ElementIndex("water"));writer.Put((byte)4);writer.Put((byte)2);
        byte[] valid=writer.CopyData();Assert.True(Protocol.TryReadCommand(new NetDataReader(valid),rules,out var command));
        Assert.Equal(4,command.Count);Assert.Equal((byte)2,command.TargetSquad);
        for(int length=0;length<valid.Length;length++)Assert.False(Protocol.TryReadCommand(new NetDataReader(valid[..length]),rules,out _));
        Assert.False(Protocol.TryReadCommand(new NetDataReader(valid.Concat(new byte[1]).ToArray()),rules,out _));
        foreach(byte count in new byte[]{0,5,255}){var invalid=(byte[])valid.Clone();invalid[10]=count;Assert.False(Protocol.TryReadCommand(new NetDataReader(invalid),rules,out _));}
        foreach(byte target in new byte[]{0,5,255}){var invalid=(byte[])valid.Clone();invalid[11]=target;Assert.False(Protocol.TryReadCommand(new NetDataReader(invalid),rules,out _));}
        valid[9]=255;Assert.False(Protocol.TryReadCommand(new NetDataReader(valid),rules,out _));
    }

    [Fact]
    public void ActiveAndWaitingDestinationsConserveTheirOwnSquadReservationsAfterReplication()
    {
        var rules=GameRulesTests.Rules();var setup=WorldCoreTests.Setup(cores:10000);
        var world=new SimulationWorld(rules,setup,1);
        Assert.Equal(CommandAdmission.Accepted,world.TryEnqueue(1,SimCommand.Train(1,"water",4,2)));
        Assert.Equal(CommandAdmission.Accepted,world.TryEnqueue(1,SimCommand.Train(2,"fire",2,4)));world.Step();
        var view=world.GetView(1);Assert.Equal(6,view.ReservedPopulation);
        using var receiver=new GameplayReplica(rules);var sender=new GameplayReplicationState(rules);var writer=new NetDataWriter();
        sender.Prepare(view,1);
        for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);receiver.TryAccept(new NetDataReader(writer.CopyData()),0);}
        var fog=new FogStateReplica(rules);var fogSender=new FogReplicationState(rules);fogSender.Prepare(view);
        for(int part=0;part<fogSender.ChunkCount;part++){fogSender.WriteChunk(writer,part);fog.TryAccept(new NetDataReader(writer.CopyData()));}
        Assert.True(receiver.TryPublish(fog,out var frame));
        Assert.Equal((byte)2,frame.TrainingTargetSquad);Assert.Equal(5,frame.TrainingQueue.Count);
        Assert.Equal(new byte[]{2,2,2,4,4},frame.TrainingQueue.Select(order=>order.TargetSquad));
        Assert.Equal(4,frame.Squads[1].ReservedPopulation);Assert.Equal(2,frame.Squads[3].ReservedPopulation);
        Assert.Equal(frame.ReservedPopulation,frame.Squads.ToArray().Sum(squad=>squad.ReservedPopulation));
    }
}
