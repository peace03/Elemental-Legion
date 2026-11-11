using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class FogAdversarialTests
{
    [Fact]
    public void GuardFocusUsesOnlyBaseRectangleWhileEveryOwnUnitKeepsCircularSight()
    {
        var setup = new WorldSetup { CameraVisibility = true };
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(600)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(600)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(225, 200), garrison: true));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.TryEnqueue(1, SimCommand.Focus(1, 4));
        world.TryEnqueue(1, SimCommand.MoveTo(2, 8, new Vector2(225, 200))); world.Step();
        var view = world.GetView(1);
        Assert.True(view.CanSeePoint(new Vector2(255, 235)));
        Assert.False(view.CanSeePoint(new Vector2(259, 230)));
        Assert.True(view.CanSeePoint(new Vector2(620, 600)));
        Assert.False(view.CanSeePoint(new Vector2(650, 635)));
        world.TryEnqueue(1, SimCommand.Focus(3, 1)); world.Step();
        Assert.True(view.CanSeePoint(new Vector2(650, 635)));
        world.TryEnqueue(1, SimCommand.Focus(4, 4)); world.Step();
        Assert.False(view.CanSeePoint(new Vector2(650, 635)));
        Assert.True(view.CanSeePoint(new Vector2(620, 600)));
    }
    [Fact]
    public void CorruptDuplicateIndexAndMissingChunkNeverPublishPartialBaseline()
    {
        var rules=GameRulesTests.Rules(); var sender=new FogReplicationState(rules); var replica=new FogStateReplica(rules);
        var world=World(); var writer=new NetDataWriter(); sender.Prepare(world.GetView(1));
        sender.WriteChunk(writer,0); byte[] first=writer.CopyData();
        Assert.False(replica.TryAccept(new NetDataReader(first)));
        Assert.False(replica.TryAccept(new NetDataReader(first)));
        byte[] corrupt=(byte[])first.Clone(); corrupt[^1]^=1;
        Assert.False(replica.TryAccept(new NetDataReader(corrupt)));
        byte[] index=(byte[])first.Clone(); BitConverter.GetBytes(-1).CopyTo(index,25);
        Assert.False(replica.TryAccept(new NetDataReader(index)));
        BitConverter.GetBytes(FogProtocol.PayloadBytes+1).CopyTo(index,25);
        Assert.False(replica.TryAccept(new NetDataReader(index)));
        for(int part=2;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part); Assert.False(replica.TryAccept(new NetDataReader(writer.CopyData())));}
        Assert.False(replica.Ready); Assert.All(replica.Current.ToArray(),value=>Assert.Equal(0,value));
        sender.WriteChunk(writer,1); Assert.True(replica.TryAccept(new NetDataReader(writer.CopyData())));
    }

    [Theory]
    [InlineData(-1,2)] [InlineData(28224,2)] [InlineData(1,1)] [InlineData(1,4)]
    public void InvalidDeltaCellOrFlagsDoNotChangeAppliedRevision(int cell,byte flags)
    {
        var replica=Baseline(out _); byte[] before=replica.Current.ToArray();
        Assert.False(replica.TryAccept(new NetDataReader(Delta(1,2,cell,flags))));
        Assert.Equal(1U,replica.Revision); Assert.Equal(before,replica.Current.ToArray());
    }

    [Fact]
    public void DeltaRejectsPastTickWrongBaseAndExplorationErasure()
    {
        var replica=Baseline(out var world);
        int known=Enumerable.Range(0,replica.CellCount).First(cell=>(replica.Explored[cell>>3]&(1<<(cell&7)))!=0);
        Assert.False(replica.TryAccept(new NetDataReader(Delta(1,2,known,0))));
        Assert.False(replica.TryAccept(new NetDataReader(Delta(1,2,0,2,baseline:0))));
        Assert.False(replica.TryAccept(new NetDataReader(Delta(replica.Tick-1,2,0,2))));
        Assert.Equal(1U,replica.Revision);
    }

    private static SimulationWorld World()
    {
        var setup=new WorldSetup();setup.Players.Add(new PlayerSpawn(1,"water",new Vector2(100),new Vector2(100)));
        var world=new SimulationWorld(GameRulesTests.Rules(),setup,1); world.Step(); return world;
    }
    private static FogStateReplica Baseline(out SimulationWorld world)
    {
        world=World();var sender=new FogReplicationState(GameRulesTests.Rules());var replica=new FogStateReplica(GameRulesTests.Rules());var writer=new NetDataWriter();
        sender.Prepare(world.GetView(1));for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);replica.TryAccept(new NetDataReader(writer.CopyData()));}return replica;
    }
    private static byte[] Delta(long tick,uint revision,int cell,byte flags,uint baseline=1)
    {
        var writer=new NetDataWriter();writer.Put((byte)PacketKind.FogDelta);writer.Put(tick);writer.Put(1);writer.Put(revision);writer.Put(baseline);writer.Put(11);writer.Put(0);writer.Put(1);writer.Put(cell);writer.Put(flags);writer.Put((ushort)0);return writer.CopyData();
    }
}
