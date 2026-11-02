using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>미래 시야의 유닛 상태를 한 프레임만 보관하고 기준이 도착하기 전에는 공개하지 않는다.</summary>
public sealed class VisibilityFrameBufferTests
{
    [Fact]
    public void FutureFrameWaitsForFogAndOnlyNewestCompleteFrameIsReleased()
    {
        var rules=GameRulesTests.Rules(); var setup=new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1,"water",new Vector2(50),new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(60)));
        var world=new SimulationWorld(rules,setup,1); var writer=new NetDataWriter();
        var fog=new FogStateReplica(rules); var gate=new VisibilityFrameBuffer(rules);
        UnitSnapshotAssembler Frame(long tick)
        {
            Protocol.WriteUnits(writer,world.GetView(1),rules.MapSize,0,1); byte[] bytes=writer.CopyData(); BitConverter.GetBytes(tick).CopyTo(bytes,1);
            var frame=new UnitSnapshotAssembler(rules); Assert.True(frame.TryAccept(new NetDataReader(bytes))); return frame;
        }
        Assert.False(gate.Accept(Frame(1),fog,out _)); Assert.False(gate.Accept(Frame(2),fog,out _));
        Assert.False(gate.Release(fog,out _));
        var sender=new FogReplicationState(rules); sender.Prepare(world.GetView(1));
        for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part); fog.TryAccept(new NetDataReader(writer.CopyData()));}
        Assert.True(gate.Release(fog,out var published)); Assert.Equal(2,published.Tick);
        Assert.False(gate.Accept(Frame(1),fog,out _));
        gate.Reset(); fog.Reset(); Assert.False(gate.Release(fog,out _));
    }
}
