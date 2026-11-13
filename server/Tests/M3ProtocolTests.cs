using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>확장 스냅샷의 최대 분할·상태와 M3 명령 경계를 실제 바이트로 검사한다.</summary>
public sealed class M3ProtocolTests
{
    [Fact]
    public void ExpandedUnitSnapshotPublishes7168UnitsFrom211Chunks()
    {
        var rules=GameRulesTests.Rules();var setup=new WorldSetup();setup.Players.Add(new PlayerSpawn(1,"water",new Vector2(50),new Vector2(60)));
        for(int i=0;i<Protocol.MaxUnits;i++)setup.Units.Add(new UnitSpawn(-1,"fire",new Vector2(62),health:1,converted:true));
        var world=new SimulationWorld(rules,setup,1);var view=world.GetView(1);var reader=new UnitSnapshotAssembler(rules);var writer=new NetDataWriter();
        int parts=Protocol.ChunkCount(view.Units.Count);Assert.Equal(211,parts);
        for(int part=parts-1;part>=0;part--)
        {
            Protocol.WriteUnits(writer,view,rules.MapSize,part,1);Assert.InRange(writer.Length,1,1024);
            Assert.Equal(part==0,reader.TryAccept(new NetDataReader(writer.CopyData())));
        }
        Assert.Equal(7168,reader.Count);Assert.All(reader.Units.Take(reader.Count),unit=>{Assert.Equal(1,unit.Health);Assert.True(unit.Converted);});
    }

    [Fact]
    public void TrainAndRallyHaveOneSequenceAndValidateTheirEntirePayload()
    {
        var rules=GameRulesTests.Rules();var writer=new NetDataWriter();
        Protocol.WriteTrain(writer,3,rules.ElementIndex("life"));
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()),rules,out var command));
        Assert.Equal(CommandKind.Train,command.Kind);Assert.Equal("life",command.Element);Assert.Equal(3UL,command.Sequence);
        byte[] bytes=writer.CopyData();for(int length=0;length<bytes.Length;length++)Assert.False(Protocol.TryReadCommand(new NetDataReader(bytes[..length]),rules,out _));
        bytes[^1]=255;Assert.False(Protocol.TryReadCommand(new NetDataReader(bytes),rules,out _));
        Protocol.WriteRally(writer,4);Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()),rules,out command));Assert.Equal(CommandKind.Rally,command.Kind);
        Protocol.WriteHello(writer,rules.DataHash,rules.StartingElements[0]);bytes=writer.CopyData();bytes[1]=2;
        Assert.Equal(JoinFailure.VersionMismatch,Protocol.ReadHello(new NetDataReader(bytes),rules,out _));
    }
}
