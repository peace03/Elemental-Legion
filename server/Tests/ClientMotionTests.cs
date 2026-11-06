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
    public void RenderClockAbsorbsPacketArrivalJitterAndStopsAtTheLatestUnits()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(60)));
        var view = new SimulationWorld(rules, setup, 1).GetView(1);
        var writer = new NetDataWriter();
        var assembler = new UnitSnapshotAssembler(rules);
        var timeline = new SnapshotTimeline(4);
        Push(timeline, 30);
        Assert.Equal(28, timeline.SampleRenderTick(30, 1, 1, .1, .05), 5);
        double before = timeline.SampleRenderTick(30, 1, 1.09, .1, .05);
        double delayed = timeline.SampleRenderTick(31, 1.10, 1.10, .1, .05);
        Assert.InRange(delayed - before, .15, .21);
        Push(timeline, 40);
        double burst = timeline.SampleRenderTick(40, 1.11, 1.11, .1, .05);
        Assert.InRange(burst - delayed, .19, .25);
        Assert.Equal(40, timeline.SampleRenderTick(42, 2, 2, .1, .05));
        Assert.Equal(40, timeline.SampleRenderTick(43, 2.1, 2.1, .1, .05));

        var restored = new SnapshotTimeline(4);
        Push(restored, 3);
        Assert.Equal(1, restored.SampleRenderTick(3, 4, 4, .1, .05), 5);

        void Push(SnapshotTimeline target, long tick)
        {
            Protocol.WriteUnits(writer, view, rules.MapSize, 0);
            byte[] bytes = writer.CopyData();
            BitConverter.GetBytes(tick).CopyTo(bytes, 1);
            assembler.Reset();
            Assert.True(assembler.TryAccept(new NetDataReader(bytes)));
            target.Push(assembler);
        }
    }

    [Fact]
    public void RenderClockResumesInterpolatingAfterAStallEvictsItsHistory()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(60)));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX));
        var writer = new NetDataWriter();
        var assembler = new UnitSnapshotAssembler(rules);
        var timeline = new SnapshotTimeline(4);
        double resumed = 0;
        for (int tick = 1; tick <= 52; tick++)
        {
            world.Step();
            if (tick == 45)
                Assert.Equal(30, timeline.SampleRenderTick(45, 1.75, 1.79, .1, .05));
            if (tick != 30 && (tick < 46 || tick % 2 != 0)) continue;
            Protocol.WriteUnits(writer, world.GetView(1), rules.MapSize, 0);
            Assert.True(assembler.TryAccept(new NetDataReader(writer.CopyData())));
            timeline.Push(assembler);
            double received = 1 + (tick - 30) * .05;
            resumed = timeline.SampleRenderTick(tick, received, received, .1, .05);
        }

        int id = timeline.GetLatest(0).Id;
        Assert.InRange(resumed, 46, 52);
        Assert.True(timeline.TrySample(id, resumed, out Vector2 first));
        double next = timeline.SampleRenderTick(52, 2.1, 2.1 + 1.0 / 60, .1, .05);
        Assert.InRange(next - resumed, .26, .41);
        Assert.True(timeline.TrySample(id, next, out Vector2 second));
        Assert.True(second.X > first.X);
    }

    [Fact]
    public void DelayedAcknowledgementsPreserveTheDisplayedCenterAndStillConvergeToPrediction()
    {
        var rules = GameRulesTests.Rules();
        var prediction = new MovementPrediction(rules);
        var origin = new Vector2(100, 100);
        prediction.Reconcile(new LegionPose(1, 1, 0, origin, Vector2.UnitX));
        for (ulong sequence = 1; sequence <= 4; sequence++) Assert.True(prediction.Push(sequence, Vector2.UnitX));
        Vector2 previous = prediction.SampleDisplay(Vector2.UnitX, 0, 0);
        var confirmed = origin + Vector2.UnitX * rules.LegionSpeed * (float)rules.DeltaTime;
        prediction.Reconcile(new LegionPose(2, 1, 4, confirmed, Vector2.UnitX));
        Assert.Equal(confirmed, prediction.Position);
        Assert.Equal(confirmed, prediction.ConfirmedPosition);
        Assert.Equal(previous, prediction.SampleDisplay(Vector2.UnitX, 0, 0));

        Assert.True(prediction.Push(5, Vector2.UnitX));
        Vector2 next = prediction.SampleDisplay(Vector2.UnitX, 0, rules.DeltaTime);
        Assert.True(next.X > previous.X);
        Assert.Equal(prediction.Sample(Vector2.Zero, 0), prediction.SampleDisplay(Vector2.Zero, 0, 1));
    }

    [Fact]
    public void LargeDisplayCorrectionStaysWithinTheMapAndCameraAllowance()
    {
        var rules = GameRulesTests.Rules();
        var prediction = new MovementPrediction(rules);
        prediction.Reconcile(new LegionPose(1, 1, 0, new Vector2(100, 0), Vector2.Zero));
        prediction.Reconcile(new LegionPose(2, 1, 0, Vector2.Zero, Vector2.Zero));
        Vector2 displayed = prediction.SampleDisplay(Vector2.Zero, 0, 0);
        Assert.Equal(rules.CameraVisionPadding * .5f, displayed.X, 4);
        Assert.Equal(Vector2.Zero, prediction.SampleDisplay(Vector2.Zero, 0, 1));
        prediction.Reconcile(new LegionPose(3, 1, 0, new Vector2(rules.MapSize, 0), Vector2.Zero));
        prediction.SampleDisplay(Vector2.Zero, 0, 1);
        prediction.Reconcile(new LegionPose(4, 1, 0, new Vector2(rules.MapSize - .1f, 0), Vector2.Zero));
        displayed = prediction.SampleDisplay(Vector2.UnitX, rules.DeltaTime, 0);
        Assert.Equal(new Vector2(rules.MapSize, 0), displayed);

        var setup = new WorldSetup();
        var wall = new Vector2(105, 100);
        var nearWall = wall - Vector2.UnitX * (rules.BaseRadius + .1f);
        setup.Players.Add(new PlayerSpawn(1, "water", wall, nearWall));
        var world = new SimulationWorld(rules, setup, 1);
        var obstacle = new MovementPrediction(rules);
        obstacle.SetBuildings(world.GetView(1).Buildings.ToArray());
        obstacle.Reconcile(new LegionPose(1, 1, 0, wall + Vector2.UnitX * (rules.BaseRadius + 1), Vector2.Zero));
        obstacle.Reconcile(new LegionPose(2, 1, 0, nearWall, Vector2.Zero));
        Assert.InRange(obstacle.SampleDisplay(Vector2.Zero, 0, 0).X, nearWall.X, wall.X - rules.BaseRadius + .001f);
    }

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
