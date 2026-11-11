using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>분대 명령과 여러 자기 분대의 포즈가 전송 중 혼동되지 않아야 한다.</summary>
public sealed class SquadProtocolTests
{
    [Fact]
    public void QuickTransferPacketCarriesOnlySequenceAndSquads()
    {
        var writer = new NetDataWriter();
        var rules = GameRulesTests.Rules();
        byte[] packet = QuickTransferPacket(7, 4, 2);
        Assert.Equal(11, packet.Length);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(packet), rules, out var command));
        Assert.Equal(CommandKind.QuickTransfer, command.Kind);
        Assert.Equal(7UL, command.Sequence);
        Assert.Equal((byte)4, command.FromSquad);
        Assert.Equal((byte)2, command.TargetSquad);
        Assert.Equal(0, command.Count);
        Protocol.WriteQuickTransfer(writer, 7, 4, 2);
        Assert.Equal(packet, writer.CopyData());
        for (int length = 0; length < packet.Length; length++)
            Assert.False(Protocol.TryReadCommand(new NetDataReader(packet[..length]), rules, out _));
        Assert.False(Protocol.TryReadCommand(new NetDataReader(packet.Concat(new byte[4]).ToArray()), rules, out _));
        foreach (byte[] invalid in new[] { QuickTransferPacket(0, 1, 2), QuickTransferPacket(7, 0, 2),
            QuickTransferPacket(7, 5, 2), QuickTransferPacket(7, 1, 5), QuickTransferPacket(7, 1, 1) })
            Assert.False(Protocol.TryReadCommand(new NetDataReader(invalid), rules, out _));
    }

    private static byte[] QuickTransferPacket(ulong sequence, byte from, byte to)
    {
        var writer = new NetDataWriter();
        writer.Put((byte)40); writer.Put(sequence); writer.Put(from); writer.Put(to);
        return writer.CopyData();
    }

    [Fact]
    public void QuickTransferDispatchResultReplicatesOnlyToItsOwner()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(50), new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(215, 200), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.QuickTransfer(7, 4, 2)));
        world.Step();
        foreach (int owner in new[] { 1, 2 })
        {
            WorldView view = world.GetView(owner);
            using var replica = new GameplayReplica(rules);
            var sender = new GameplayReplicationState(rules);
            var writer = new NetDataWriter();
            sender.Capture(view);
            sender.Prepare(view, 1);
            for (int part = 0; part < sender.ChunkCount; part++)
            {
                sender.WriteChunk(writer, part);
                replica.TryAccept(new NetDataReader(writer.CopyData()), 0);
            }
            var fog = new FogStateReplica(rules);
            var fogSender = new FogReplicationState(rules);
            fogSender.Prepare(view);
            for (int part = 0; part < fogSender.ChunkCount; part++)
            {
                fogSender.WriteChunk(writer, part);
                fog.TryAccept(new NetDataReader(writer.CopyData()));
            }
            Assert.True(replica.TryPublish(fog, out var frame));
            if (owner == 2)
            {
                Assert.Empty(frame.Events.ToArray());
                continue;
            }
            SimEvent result = Assert.Single(frame.Events.ToArray()).Data;
            Assert.Equal(SimEventKind.CommandApplied, result.Kind);
            Assert.Equal((int)CommandKind.QuickTransfer, result.Detail);
            Assert.Equal(7UL, result.Sequence);
            Assert.Equal(1, result.Amount);
            Assert.Equal(Vector2.Zero, result.Position);
        }
    }

    [Theory]
    [InlineData(1, 7UL, 0f)]
    [InlineData(1, 7UL, 1.5f)]
    [InlineData(1, 7UL, 4f)]
    [InlineData(1, 7UL, float.NaN)]
    [InlineData(2, 7UL, 1f)]
    [InlineData(1, 0UL, 1f)]
    public void QuickTransferDispatchRejectsInvalidResults(int owner, ulong sequence, float amount)
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var view = world.GetView(1);
        var body = new NetDataWriter();
        var result = new SimEvent(SimEventKind.CommandApplied, owner, 0, 0, Vector2.Zero,
            amount: amount, detail: (int)CommandKind.QuickTransfer, sequence: sequence);
        GameplayCodec.WriteBody(body, view, new[] { new NetworkEvent(1, view.Tick, result) }, 1);
        Assert.False(GameplayCodec.ReadBody(new NetDataReader(body.CopyData()), rules, new GameplayFrame(rules), view.Tick, 1, 1));
    }

    [Fact]
    public void LegacyMoveCallerStillSendsAnExplicitSquadMask()
    {
        var writer = new NetDataWriter();
        Protocol.WriteMove(writer, 7, Vector2.UnitX);
        byte[] packet = writer.CopyData();
        Assert.Equal(18, packet.Length);
        Assert.Equal(1, packet[9]);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(packet), GameRulesTests.Rules(), out var command));
        Assert.Equal(1, command.SquadMask);
    }

    [Fact]
    public void SquadCommandsRoundTripAndRejectBadMasksCoordinatesAndCounts()
    {
        var writer = new NetDataWriter();
        var rules = GameRulesTests.Rules();
        Protocol.WriteMoveTo(writer, 1, 7, new Vector2(60, 70));
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var move));
        Assert.Equal(CommandKind.MoveTo, move.Kind);
        Assert.Equal(7, move.SquadMask);
        Assert.Equal(new Vector2(60, 70), move.Direction);
        byte[] invalid = writer.CopyData();
        invalid[9] = 0;
        Assert.False(Protocol.TryReadCommand(new NetDataReader(invalid), rules, out _));
        invalid[9] = 16;
        Assert.False(Protocol.TryReadCommand(new NetDataReader(invalid), rules, out _));
        Protocol.WriteMoveTo(writer, 1, 1, new Vector2(float.NaN, 50));
        Assert.False(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out _));
        Protocol.WriteTransfer(writer, 2, 1, 4, 2);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var transfer));
        Assert.Equal((byte)1, transfer.FromSquad);
        Assert.Equal((byte)4, transfer.TargetSquad);
        Assert.Equal(2, transfer.Count);
        invalid = writer.CopyData();
        Array.Fill(invalid, (byte)0, 11, 4);
        Assert.False(Protocol.TryReadCommand(new NetDataReader(invalid), rules, out _));
        Protocol.WriteFocus(writer, 3, 4);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var focus));
        Assert.Equal((byte)4, focus.TargetSquad);
        invalid = writer.CopyData(); invalid[9] = 5;
        Assert.False(Protocol.TryReadCommand(new NetDataReader(invalid), rules, out _));
        foreach(Action write in new Action[]{()=>Protocol.WriteMoveTo(writer,4,7,new Vector2(70)),
            ()=>Protocol.WriteTransfer(writer,4,1,4,2),()=>Protocol.WriteFocus(writer,4,4),()=>Protocol.WriteRally(writer,4,2,2)})
        {
            write();byte[] complete=writer.CopyData();
            for(int length=0;length<complete.Length;length++)Assert.False(Protocol.TryReadCommand(new NetDataReader(complete[..length]),rules,out _));
            Assert.False(Protocol.TryReadCommand(new NetDataReader(complete.Concat(new byte[1]).ToArray()),rules,out _));
        }
    }

    [Fact]
    public void FourPosesShareOneBoundedFrameWithoutOverwritingEachOther()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var writer = new NetDataWriter();
        Protocol.WritePose(writer, world.GetView(1));
        byte[] packet = writer.CopyData();
        Assert.True(packet.Length <= Protocol.MaxDatagramBytes);
        var poses = new LegionPose[4];
        Assert.True(Protocol.TryReadPoses(new NetDataReader(packet), rules, poses, out int count));
        Assert.Equal(4, count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, poses.Select(item => item.SquadId));
        for (int length = 0; length < packet.Length; length++)
            Assert.False(Protocol.TryReadPoses(new NetDataReader(packet[..length]), rules, poses, out _));
        packet[14 + 29] = packet[14];
        Assert.False(Protocol.TryReadPoses(new NetDataReader(packet), rules, poses, out _));
        Assert.False(Protocol.TryReadPose(new NetDataReader(packet),rules,out _));
    }

    [Fact]
    public void PrivateSquadOrdersAreScopedToTheirRecipientAndValidatedBeforePublication()
    {
        var rules=GameRulesTests.Rules();var setup=WorldCoreTests.Setup();
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(50)));
        var world=new SimulationWorld(rules,setup,1);
        world.TryEnqueue(1,SimCommand.MoveTo(1,1,new Vector2(70,60)));world.TryEnqueue(1,SimCommand.Focus(2,2));world.Step();
        var view=world.GetView(1);var sender=new GameplayReplicationState(rules);var replica=new GameplayReplica(rules);var writer=new NetDataWriter();
        sender.Prepare(view,1);
        for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);replica.TryAccept(new NetDataReader(writer.CopyData()),0);}
        var fog=new FogStateReplica(rules);var fogSender=new FogReplicationState(rules);fogSender.Prepare(view);
        for(int part=0;part<fogSender.ChunkCount;part++){fogSender.WriteChunk(writer,part);fog.TryAccept(new NetDataReader(writer.CopyData()));}
        Assert.True(replica.TryPublish(fog,out _));
        Assert.Equal((byte)2,replica.State.FocusAnchor);Assert.Equal(4,replica.State.Squads.Length);
        Assert.Equal(SquadOrderKind.MoveTo,replica.State.Squads[0].Order);Assert.Equal(new Vector2(70,60),replica.State.Squads[0].Destination);
        Assert.Equal(view.PlayerId,replica.State.PlayerId);
        using var rejected=new GameplayReplica(rules);
        for(int part=0;part<sender.ChunkCount;part++)
        {
            sender.WriteChunk(writer,part);byte[] invalid=writer.CopyData();if(part==0)invalid[GameplayCodec.HeaderBytes+100]=5;
            rejected.TryAccept(new NetDataReader(invalid),0);
        }
        Assert.Equal(-1,rejected.State.Tick);Assert.True(rejected.Overflowed);
    }

    [Fact]
    public void InvalidUnitSquadCannotPoisonAnIncompleteFrame()
    {
        var rules=GameRulesTests.Rules();var setup=WorldCoreTests.Setup();
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(50)));
        var world=new SimulationWorld(rules,setup,1);var writer=new NetDataWriter();
        Protocol.WriteUnits(writer,world.GetView(1),rules.MapSize,0,1);byte[] valid=writer.CopyData();
        byte[] invalid=(byte[])valid.Clone();invalid[^5]=0;
        var receiver=new UnitSnapshotAssembler(rules);Assert.False(receiver.TryAccept(new NetDataReader(invalid)));Assert.Equal(-1,receiver.Tick);
        Assert.True(receiver.TryAccept(new NetDataReader(valid)));Assert.Equal((byte)1,receiver.Units[0].SquadId);
    }

    [Fact]
    public void RallyingGarrisonCanBeSerializedWhileItTravelsToItsSquad()
    {
        var rules=GameRulesTests.Rules();var setup=WorldCoreTests.Setup();
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(50)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(50),garrison:true));
        var world=new SimulationWorld(rules,setup,1);
        world.TryEnqueue(1,SimCommand.MoveTo(1,1,new Vector2(90,50)));
        for(int i=0;i<70;i++)world.Step();
        world.TryEnqueue(1,SimCommand.Rally(2,1,1));world.Step();
        var view=world.GetView(1);Assert.Contains(view.Units,unit=>unit.IsReinforcing);
        var writer=new NetDataWriter();Protocol.WriteUnits(writer,view,rules.MapSize,0,1);
        var replica=new UnitSnapshotAssembler(rules);Assert.True(replica.TryAccept(new NetDataReader(writer.CopyData())));
        Assert.Contains(replica.Units.Take(replica.Count),unit=>unit.IsReinforcing);
    }

    [Fact]
    public void ExpandedUnitChunksStayWithinMtuAndPreserveSquadMetadata()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(50)));
        for (int index = 0; index < 100; index++)
            setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(52, 50 + index * 0.01f)));
        var world = new SimulationWorld(rules, setup, 1);
        WorldView view = world.GetView(1);
        var assembler = new UnitSnapshotAssembler(rules);
        var writer = new NetDataWriter();
        for (int part = Protocol.ChunkCount(view.Units.Count) - 1; part >= 0; part--)
        {
            Protocol.WriteUnits(writer, view, rules.MapSize, part, 1);
            Assert.True(writer.Length <= 1024);
            assembler.TryAccept(new NetDataReader(writer.CopyData()));
        }
        Assert.Equal(view.Units.Count, assembler.Count);
        var own = Assert.Single(assembler.Units.Take(assembler.Count), unit => unit.OwnerId == 1);
        Assert.Equal((byte)1, own.SquadId);
        Assert.False(own.IsReinforcing);
        Assert.All(assembler.Units.Take(assembler.Count).Where(unit => unit.OwnerId < 0), unit => Assert.Equal((byte)0, unit.SquadId));
    }
}
