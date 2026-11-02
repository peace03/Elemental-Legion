using System.Numerics;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>Unity와 동일한 클라이언트 소스로 입력 재적용과 관측 보간의 결과를 검사한다.</summary>
public sealed class ClientMotionTests
{
    [Fact]
    public void ReconciliationReplaysOnlyUnacknowledgedInputsAndIgnoresOldPoses()
    {
        var rules = GameRulesTests.Rules(); var prediction = new MovementPrediction(rules);
        prediction.Reconcile(new LegionPose(1, 1, 0, new Vector2(100), Vector2.Zero));
        Assert.True(prediction.Push(1, Vector2.UnitX)); Assert.True(prediction.Push(2, Vector2.UnitY));
        float step = rules.LegionSpeed * (float)rules.DeltaTime;
        prediction.Reconcile(new LegionPose(2, 1, 1, new Vector2(100 + step, 100), Vector2.UnitX));
        Assert.InRange(Vector2.Distance(new Vector2(100 + step, 100 + step), prediction.Position), 0, 0.0001f);
        prediction.Reconcile(new LegionPose(1, 1, 0, Vector2.Zero, Vector2.Zero));
        Assert.True(prediction.Position.X > 100);
        prediction.Reconcile(new LegionPose(3, 1, 2, new Vector2(100 + step, 100 + step), Vector2.UnitY));
        Assert.Equal(0, prediction.PendingCount);
        Assert.False(prediction.Push(2, Vector2.UnitX));
        Assert.Throws<ArgumentException>(() => prediction.Push(3, new Vector2(float.NaN, 0)));
    }

    [Fact]
    public void PredictionNormalizesInputClampsMapAndBoundsPendingHistory()
    {
        var rules = GameRulesTests.Rules(); var prediction = new MovementPrediction(rules);
        prediction.Reconcile(new LegionPose(1, 1, 0, Vector2.Zero, Vector2.Zero));
        prediction.Push(1, new Vector2(-float.MaxValue));
        Assert.Equal(Vector2.Zero, prediction.Position);
        for (ulong sequence = 2; sequence <= 257; sequence++) prediction.Push(sequence, Vector2.One);
        Assert.InRange(prediction.PendingCount, 1, 256);
        Assert.False(prediction.Push(1000, Vector2.One));
    }

    [Fact]
    public void TimelineInterpolatesAndHidesUnitsMissingFromLatestCompleteFrame()
    {
        var rules = GameRulesTests.Rules(); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(60)));
        var world = new SimulationWorld(rules, setup, 1); var writer = new NetDataWriter();
        var assembler = new UnitSnapshotAssembler(rules); var timeline = new SnapshotTimeline(4);
        Protocol.WriteUnits(writer, world.GetView(1), rules.MapSize, 0);
        Assert.True(assembler.TryAccept(new NetDataReader(writer.CopyData()))); timeline.Push(assembler);
        int id = timeline.GetLatest(0).Id; Vector2 start = timeline.GetLatest(0).Position;
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX)); world.Step(); world.Step();
        Protocol.WriteUnits(writer, world.GetView(1), rules.MapSize, 0);
        Assert.True(assembler.TryAccept(new NetDataReader(writer.CopyData()))); timeline.Push(assembler);
        Vector2 end = timeline.GetLatest(0).Position;
        Assert.True(timeline.TrySample(id, 1, out Vector2 middle));
        Assert.InRange(Vector2.Distance((start + end) * 0.5f, middle), 0, 0.001f);
        Assert.True(timeline.TrySample(id, 999, out Vector2 held)); Assert.Equal(end, held);
        byte[] empty = writer.CopyData()[..29]; BitConverter.GetBytes(3L).CopyTo(empty, 1); empty[21] = 0; empty[22] = 0;
        Assert.True(assembler.TryAccept(new NetDataReader(empty))); timeline.Push(assembler);
        Assert.Equal(0, timeline.Count); Assert.False(timeline.TrySample(id, 1, out _));
    }

    [Fact]
    public void ForgetHidesImmediatelyAndLateFramesCannotResurrectButReentryCan()
    {
        var rules = GameRulesTests.Rules(); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(60)));
        var world = new SimulationWorld(rules, setup, 1); var writer = new NetDataWriter();
        var timeline = new SnapshotTimeline(4);
        UnitSnapshotAssembler Frame(long tick)
        {
            Protocol.WriteUnits(writer, world.GetView(1), rules.MapSize, 0); byte[] bytes=writer.CopyData(); BitConverter.GetBytes(tick).CopyTo(bytes, 1);
            var frame = new UnitSnapshotAssembler(rules); Assert.True(frame.TryAccept(new NetDataReader(bytes))); return frame;
        }
        timeline.Push(Frame(10)); int id = timeline.GetLatest(0).Id;
        timeline.Forget(id, 11);
        Assert.False(timeline.TrySample(id, 10, out _));
        timeline.Push(Frame(11)); Assert.False(timeline.TrySample(id, 11, out _));
        timeline.Push(Frame(12)); Assert.True(timeline.TrySample(id, 12, out _));
        timeline.Forget(id, 11); Assert.True(timeline.TrySample(id, 12, out _));
    }
}
