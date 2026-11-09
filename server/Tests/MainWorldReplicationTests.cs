using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>멀리 떨어진 자기 원소도 같은 기준점의 청크로 복제하고 귀환 뒤 새 위치를 적용한다.</summary>
public sealed class MainWorldReplicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WorldEdgesRoundTripThroughUdpAndWssBeforeAndAfterRecall(bool online, bool farAnchor)
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup { CameraVisibility = true };
        var anchor = new Vector2(farAnchor ? rules.MapSize - 8 : 8);
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(300), anchor, 4));
        for (int i = 0; i < 100; i++)
        {
            float x = (i / 25 & 1) == 0 ? 5 : rules.MapSize - 5;
            float z = (i / 50) == 0 ? 5 : rules.MapSize - 5;
            Vector2 point = i == 99 ? new Vector2(310, 300) : new Vector2(x, z) + new Vector2(i % 5 * .37f, i % 7 * .21f);
            setup.Units.Add(new UnitSpawn(1, "water", point));
        }
        setup.Units.Add(new UnitSpawn(-1, "fire", new Vector2(rules.MapSize / 2)));
        var world = new SimulationWorld(rules, setup, 1);
        var snapshots = new UnitSnapshotAssembler(rules);
        RoundTrip(world.GetView(1), rules, snapshots, online);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Transfer(1, 1, 3, 1)));
        world.Step();
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Focus(2, 3)));
        world.Step();
        var focused = world.GetView(1);
        Assert.Equal((byte)3, focused.FocusAnchor);
        Assert.Equal(99, focused.Squads[0].JoinedCount);
        Assert.Equal(1, focused.Squads[2].JoinedCount);
        var poseWriter = new NetDataWriter();
        Protocol.WritePose(poseWriter, focused);
        var poses = new LegionPose[4];
        Assert.True(Protocol.TryReadPoses(new NetDataReader(Transport(poseWriter.CopyData(), online)), rules, poses, out int poseCount));
        Assert.Equal(4, poseCount);
        Assert.Equal(focused.Squads[2].Position, poses[2].Position);
        RoundTrip(focused, rules, snapshots, online);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Focus(3, 1)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Recall(4, 1)));
        for (int tick = 0; tick <= rules.RecallCastTicks; tick++) world.Step();
        var after = world.GetView(1);
        Assert.Equal((byte)0, after.RecallSquad);
        Assert.True(after.RecallReadyTick > after.Tick);
        RoundTrip(after, rules, snapshots, online);
        Assert.All(after.Units, unit => Assert.True(Vector2.Distance(unit.Position, after.BasePosition) <= rules.GarrisonRadius));
    }

    private static void RoundTrip(WorldView view, GameRules rules, UnitSnapshotAssembler receiver, bool online)
    {
        Assert.Equal(100, view.Units.Count);
        Assert.All(view.Units, unit => Assert.Equal(1, unit.OwnerId));
        var writer = new NetDataWriter();
        int parts = Protocol.ChunkCount(view.Units.Count);
        for (int part = parts - 1; part >= 0; part--)
        {
            Protocol.WriteUnits(writer, view, rules.MapSize, part, 1);
            byte[] packet = Transport(writer.CopyData(), online);
            Assert.Equal(view.LegionPosition.X, BitConverter.ToSingle(packet, 13));
            Assert.Equal(view.LegionPosition.Y, BitConverter.ToSingle(packet, 17));
            Assert.Equal(part == 0, receiver.TryAccept(new NetDataReader(packet)));
            Assert.False(receiver.TryAccept(new NetDataReader(packet)), "중복 청크는 새 스냅샷을 발행하지 않습니다.");
        }
        Assert.Equal(100, receiver.Count);
        double axisError = rules.MapSize / (2d * short.MaxValue) + .0002;
        for (int index = 0; index < receiver.Count; index++)
        {
            var actual = receiver.Units[index];
            var expected = view.Units[index];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.SquadId, actual.SquadId);
            Assert.Equal(expected.MovementRevision, actual.MovementRevision);
            Assert.InRange(Math.Abs(actual.Position.X - expected.Position.X), 0, axisError);
            Assert.InRange(Math.Abs(actual.Position.Y - expected.Position.Y), 0, axisError);
        }
    }

    private static byte[] Transport(byte[] packet, bool online)
    {
        Assert.InRange(packet.Length, 1, Protocol.MaxDatagramBytes);
        if (!online) return packet;
        byte[] frame = OnlineFrame.Wrap(3, packet);
        Assert.InRange(frame.Length, OnlineFrame.HeaderBytes + 1, OnlineFrame.MaxMessageBytes);
        Assert.True(OnlineFrame.TryRead(frame, frame.Length, out ulong generation, out byte[] body));
        Assert.Equal(3UL, generation);
        Assert.Equal(packet, body);
        return body;
    }
}
