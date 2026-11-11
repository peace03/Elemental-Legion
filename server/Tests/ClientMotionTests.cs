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
    public void GuardPredictionStaysWithinTheKnownBaseRadiusWithoutConstrainingFieldSquads()
    {
        var rules=GameRulesTests.Rules();var prediction=new MovementPrediction(rules);var home=new Vector2(100);
        prediction.SetBasePosition(home);
        var start=home+Vector2.UnitX*(rules.GarrisonRadius-0.01f);
        prediction.Reset(new LegionPose(1,1,0,start,Vector2.Zero,4));
        Assert.True(prediction.Push(1,Vector2.UnitX));
        Assert.InRange(Vector2.Distance(home,prediction.Position),0,rules.GarrisonRadius);
        Assert.InRange(Vector2.Distance(home,prediction.SampleDisplay(Vector2.UnitX,rules.DeltaTime,0)),0,rules.GarrisonRadius);
        prediction.Reset(new LegionPose(2,1,0,start,Vector2.Zero,1));
        Assert.True(prediction.Push(2,Vector2.UnitX));
        Assert.True(Vector2.Distance(home,prediction.Position)>rules.GarrisonRadius);
        Assert.Throws<ArgumentOutOfRangeException>(()=>prediction.SetBasePosition(new Vector2(float.NaN)));
    }
    [Fact]
    public void DirectReleaseAndSelectionNeverSendStopsToUnrelatedPointOrders()
    {
        var control = new SquadControlState();
        Assert.False(control.NextDirect(false, out _));
        Assert.True(control.NextDirect(true, out byte mask));
        Assert.Equal(1, mask);
        Assert.Equal(1, control.Select(4));
        Assert.Equal(3, control.Representative);
        Assert.False(control.NextDirect(false, out _));
        Assert.Equal(0, control.Select(7));
        Assert.Equal(1, control.Representative);
        Assert.True(control.NextDirect(true, out mask));
        Assert.Equal(7, mask);
        Assert.True(control.NextDirect(false, out mask));
        Assert.Equal(7, mask);
        Assert.False(control.NextDirect(false, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => control.Select(15));
        Assert.False(SquadControlState.ShouldMoveTo(true, false, true));
        Assert.False(SquadControlState.ShouldMoveTo(true, true, false));
        Assert.True(SquadControlState.ShouldMoveTo(true, false, false));
    }

    [Fact]
    public void UnitMovementRevisionJumpsEvenWhenRenderClockStillPointsBeforeTheNewFrame()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(60)));
        var view = new SimulationWorld(rules, setup, 1).GetView(1);
        var timeline = new SnapshotTimeline(4);
        Push(10, new Vector2(100), 0);
        Push(12, new Vector2(800), 1);
        Assert.True(timeline.TrySample(1, 10.5, out Vector2 position));
        Assert.Equal(new Vector2(800), position);
        void Push(long tick, Vector2 center, uint revision)
        {
            var writer = new NetDataWriter();
            Protocol.WriteUnits(writer, view, rules.MapSize, 0);
            byte[] bytes = writer.CopyData();
            BitConverter.GetBytes(tick).CopyTo(bytes, 1);
            var snapshot = new UnitSnapshotAssembler(rules);
            Assert.True(snapshot.TryAccept(new NetDataReader(bytes)));
            snapshot.Units[0] = new NetworkUnit(1, 1, 0, center, false, 1, 0, false, 0,
                squadId: 3, movementRevision: revision);
            timeline.Push(snapshot);
        }
    }

    [Theory]
    [InlineData(-1,0)]
    [InlineData(1,1)]
    [InlineData(1,4)]
    public void ConversionWaitsForAuthoritativeNewSquadInsteadOfInheritingTheVictimsGroup(int formerOwner,byte formerSquad)
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(60)));
        var world = new SimulationWorld(rules, setup, 1);
        var writer = new NetDataWriter();
        Protocol.WriteUnits(writer, world.GetView(1), rules.MapSize, 0);
        var snapshot = new UnitSnapshotAssembler(rules);
        Assert.True(snapshot.TryAccept(new NetDataReader(writer.CopyData())));
        snapshot.Units[0] = new NetworkUnit(1, formerOwner, 0, new Vector2(60), formerSquad==4, 1, 0, false, 0,
            ghost:formerOwner>0,squadId: formerSquad, movementRevision: 9);
        var timeline = new SnapshotTimeline(4);
        timeline.Push(snapshot);
        timeline.ResolveConversion(1, world.GetView(1).Tick, 2, 5);
        NetworkUnit converted = timeline.GetLatest(0);
        Assert.Equal(2, converted.OwnerId);
        Assert.Equal(0, converted.SquadId);
        Assert.False(converted.IsReinforcing);Assert.False(converted.Garrison);Assert.False(converted.Ghost);
        for(byte squad=1;squad<=4;squad++)Assert.False(SnapshotTimeline.UsesPrediction(converted,2,squad));
        Assert.Equal(9u, converted.MovementRevision);
        world.Step();Protocol.WriteUnits(writer,world.GetView(1),rules.MapSize,0);
        Assert.True(snapshot.TryAccept(new NetDataReader(writer.CopyData())));
        snapshot.Units[0]=new NetworkUnit(1,2,0,new Vector2(60),false,5,0,true,0,squadId:3);
        timeline.Push(snapshot);Assert.Equal((byte)3,timeline.GetLatest(0).SquadId);
        Assert.True(SnapshotTimeline.UsesPrediction(timeline.GetLatest(0),2,3));
        timeline.ResolveConversion(1,world.GetView(1).Tick,2,5);
        Assert.Equal((byte)3,timeline.GetLatest(0).SquadId);
    }

    [Fact]
    public void SelectionAndDiscontinuityResetPredictionWithoutOldInputOrDisplayOffset()
    {
        var prediction = new MovementPrediction(GameRulesTests.Rules());
        prediction.Reconcile(new LegionPose(10, 1, 0, new Vector2(100), Vector2.Zero));
        prediction.Push(1, Vector2.UnitX);
        prediction.Reset(new LegionPose(8, 1, 0, new Vector2(400), Vector2.Zero, 3));
        Assert.Equal(new Vector2(400), prediction.SampleDisplay(Vector2.Zero, 0, 0));
        Assert.Equal(0, prediction.PendingCount);
        prediction.Reconcile(new LegionPose(11, 1, 0, Vector2.Zero, Vector2.Zero, 1));
        Assert.Equal(new Vector2(400), prediction.Position);
        prediction.Push(2, Vector2.UnitX);
        prediction.Reconcile(new LegionPose(9, 1, 0, new Vector2(700), Vector2.Zero, 3, 1));
        Assert.Equal(new Vector2(700), prediction.SampleDisplay(Vector2.Zero, 0, 0));
        Assert.Equal(0, prediction.PendingCount);
    }

    [Theory]
    [InlineData(1, 3, false, true)]
    [InlineData(1, 2, false, false)]
    [InlineData(1, 4, false, false)]
    [InlineData(1, 3, true, false)]
    [InlineData(2, 3, false, false)]
    public void PredictionOffsetAffectsOnlySelectedJoinedSquad(int owner, byte squad, bool reinforcing, bool expected)
    {
        var unit = new NetworkUnit(1, owner, 0, new Vector2(10), false, 1, 0, false, 0,
            squadId: squad, isReinforcing: reinforcing);
        Assert.Equal(expected, SnapshotTimeline.UsesPrediction(unit, 1, 3));
    }

    [Fact]
    public void SquadAnchorsAndDiscontinuitiesDoNotInterpolateAcrossDifferentSquadsOrRevision()
    {
        var timeline = new SnapshotTimeline(4);
        timeline.ObservePose(new LegionPose(1, 1, 0, new Vector2(100), Vector2.Zero, 1));
        timeline.ObservePose(new LegionPose(1, 1, 0, new Vector2(400), Vector2.Zero, 3));
        timeline.ObservePose(new LegionPose(3, 1, 0, new Vector2(420), Vector2.Zero, 3));
        Assert.Equal(new Vector2(410), timeline.SampleSquadAnchor(3, 2));
        timeline.ObservePose(new LegionPose(4, 1, 0, new Vector2(800), Vector2.Zero, 3, 1));
        Assert.Equal(new Vector2(800), timeline.SampleSquadAnchor(3, 2));
        Assert.Equal(new Vector2(100), timeline.SampleSquadAnchor(1, 2));
        var migration = new SnapshotTimeline(4);
        Assert.Equal(Vector2.Zero, migration.SampleSquadAnchor(3, 2));
    }

    [Fact]
    public void FasterPosePacketsKeepTheirOwnInterpolationHistory()
    {
        var timeline = new SnapshotTimeline(6, 12);
        for (int tick = 1; tick <= 12; tick++)
            timeline.ObservePose(new LegionPose(tick, 1, 0, new Vector2(tick, 0), Vector2.UnitX));
        Assert.Equal(new Vector2(6.5f, 0), timeline.SampleSquadAnchor(1, 6.5));
    }

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
    public void PointMovementUsesTheInterpolatedAnchorAroundABuilding()
    {
        var rules = GameRulesTests.Rules();
        var start = new Vector2(50, 1);
        var goal = new Vector2(80, 1);
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(60, 2), start));
        setup.Units.Add(new UnitSpawn(1, "water", start));
        var world = new SimulationWorld(rules, setup, 31);
        var prediction = new MovementPrediction(rules);
        prediction.SetBuildings(world.GetView(1).Buildings.ToArray());
        var timeline = new SnapshotTimeline(8);
        Observe(world.GetView(1));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.MoveTo(1, 1, goal)));
        for (int tick = 0; tick < 140; tick++)
        {
            world.Step();
            var view = world.GetView(1);
            Observe(view);
            for (int frame = 0; frame < 3; frame++)
            {
                var anchor = timeline.SampleSquadAnchor(1, view.Tick - 2 + frame / 3.0);
                Assert.Equal(anchor, prediction.SampleDisplay(Vector2.Zero, 0, rules.DeltaTime / 3, anchor));
            }
        }
        Assert.Equal(goal, world.GetView(1).Squads[0].Position);
        Assert.Equal(goal, prediction.ConfirmedPosition);

        void Observe(WorldView view)
        {
            var squad = view.Squads[0];
            var pose = new LegionPose(view.Tick, view.PlayerId, squad.AppliedSequence, squad.Position, squad.Direction);
            timeline.ObservePose(pose);
            prediction.Reconcile(pose);
        }
    }

    [Fact]
    public void EndingDirectInputKeepsTheDisplayedPositionWhenTheBasisChanges()
    {
        var rules = GameRulesTests.Rules();
        var prediction = new MovementPrediction(rules);
        var origin = new Vector2(100);
        prediction.Reset(new LegionPose(1, 1, 0, origin, Vector2.Zero));
        Assert.True(prediction.Push(1, Vector2.UnitX));
        var displayed = prediction.SampleDisplay(Vector2.UnitX, 0, 0, origin);
        Assert.True(prediction.Push(2, Vector2.Zero));
        Assert.Equal(displayed, prediction.SampleDisplay(Vector2.Zero, 0, 0, origin));
        prediction.Reconcile(new LegionPose(2, 1, 2, displayed, Vector2.Zero));
        Assert.Equal(displayed, prediction.SampleDisplay(Vector2.Zero, 0, 0, origin));
        Assert.Equal(displayed, prediction.SampleDisplay(Vector2.Zero, 0, 1, displayed));
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
