using System.Numerics;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class M3MotionTests
{
    [Fact]
    public void PredictionStopsAtObservedBaseAndAllowsTravelAwayFromInside()
    {
        var rules=GameRulesTests.Rules(); var prediction=new MovementPrediction(rules);
        prediction.SetBuildings(new[]{new BuildingView(1,1,BuildingKind.Base,new Vector2(50),rules.BaseRadius)});
        prediction.Reconcile(new LegionPose(1,1,0,new Vector2(50-rules.BaseRadius-0.1f,50),Vector2.Zero));
        Assert.True(prediction.Push(1,Vector2.UnitX));
        Assert.True(prediction.Position.X<=50-rules.BaseRadius);
        prediction.Reconcile(new LegionPose(2,1,1,new Vector2(50),Vector2.Zero));
        Assert.True(prediction.Push(3,Vector2.UnitX)); Assert.True(prediction.Position.X>50);
        Assert.Equal(1,prediction.PendingCount);
    }

    [Fact]
    public void ConversionKeepsIdAndOnlyANewerAuthoritativeFrameCanReviveAfterDeath()
    {
        var rules=GameRulesTests.Rules();var setup=new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1,"water",new Vector2(50),new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(60)));
        var world=new SimulationWorld(rules,setup,1);var writer=new NetDataWriter();var timeline=new SnapshotTimeline(4);
        void Frame(long tick)
        {
            Protocol.WriteUnits(writer,world.GetView(1),rules.MapSize,0);byte[] bytes=writer.CopyData();BitConverter.GetBytes(tick).CopyTo(bytes,1);
            var assembler=new UnitSnapshotAssembler(rules);Assert.True(assembler.TryAccept(new NetDataReader(bytes)));timeline.Push(assembler);
        }
        Frame(10);int id=timeline.GetLatest(0).Id;
        timeline.ResolveConversion(id,11,2,20);
        Assert.Equal(2,timeline.GetLatest(0).OwnerId);
        Frame(11);Assert.Equal(2,timeline.GetLatest(0).OwnerId);
        timeline.ResolveDeath(id,12);Frame(12);Assert.False(timeline.TrySample(id,12,out _));
        Frame(13);Assert.True(timeline.TrySample(id,13,out _));
        Assert.Equal(1,timeline.GetLatest(0).OwnerId);
    }
}
