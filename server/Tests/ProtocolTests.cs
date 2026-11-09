using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>통신 경계의 버전·길이·수치 검증과 시야 스냅샷을 검사한다.</summary>
public sealed class ProtocolTests
{
    [Fact]
    public void HelloChecksHashVersionAndStartingElement()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        Protocol.WriteHello(writer, rules.DataHash, rules.StartingElements[0]);
        Assert.Equal(JoinFailure.None, Protocol.ReadHello(new NetDataReader(writer.CopyData()), rules, out int element));
        Assert.Equal(rules.StartingElements[0], element);
        var different = new GameRules(new GameDataDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")) + "\n"));
        Assert.Equal(JoinFailure.DataMismatch, Protocol.ReadHello(new NetDataReader(writer.CopyData()), different, out _));
        var bytes = writer.CopyData(); bytes[1]++;
        Assert.Equal(JoinFailure.VersionMismatch, Protocol.ReadHello(new NetDataReader(bytes), rules, out _));
        Protocol.WriteHello(writer, rules.DataHash, rules.ElementIndex("light"));
        Assert.Equal(JoinFailure.InvalidElement, Protocol.ReadHello(new NetDataReader(writer.CopyData()), rules, out _));
    }

    [Fact]
    public void MoveRejectsTruncationTrailingBytesAndNonFiniteDirections()
    {
        var writer = new NetDataWriter();
        Protocol.WriteMove(writer, 7, Vector2.UnitX);
        byte[] bytes = writer.CopyData();
        Assert.True(Protocol.TryReadMove(new NetDataReader(bytes), out ulong sequence, out var direction));
        Assert.Equal(7UL, sequence); Assert.Equal(Vector2.UnitX, direction);
        for (int length = 0; length < bytes.Length; length++)
            Assert.False(Protocol.TryReadMove(new NetDataReader(bytes[..length]), out _, out _));
        Assert.False(Protocol.TryReadMove(new NetDataReader(bytes.Concat(new byte[1]).ToArray()), out _, out _));
        Protocol.WriteMove(writer, 8, new Vector2(float.NaN, 0));
        Assert.False(Protocol.TryReadMove(new NetDataReader(writer.CopyData()), out _, out _));
        Protocol.WriteMove(writer, 0, Vector2.Zero);
        Assert.False(Protocol.TryReadMove(new NetDataReader(writer.CopyData()), out _, out _));
    }

    [Fact]
    public void QuantizedChunksAreAtomicBoundedAndContainOnlyVisibleUnits()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        setup.Players.Add(new PlayerSpawn(2, "fire", new Vector2(500), new Vector2(510)));
        for (int index = 0; index < 70; index++) setup.Units.Add(new UnitSpawn(-1, "water", new Vector2(62 + index * 0.01f, 62)));
        setup.Units.Add(new UnitSpawn(2, "fire", new Vector2(510)));
        var world = new SimulationWorld(rules, setup, 1);
        var view = world.GetView(1);
        var writer = new NetDataWriter();
        var assembler = new UnitSnapshotAssembler(rules);
        for(int part=2;part<Protocol.ChunkCount(view.Units.Count);part++)
        {Protocol.WriteUnits(writer,view,rules.MapSize,part);Assert.False(assembler.TryAccept(new NetDataReader(writer.CopyData())));}
        Protocol.WriteUnits(writer, view, rules.MapSize, 1);
        byte[] last = writer.CopyData();
        Assert.InRange(last.Length, 1, Protocol.MaxDatagramBytes);
        Assert.False(assembler.TryAccept(new NetDataReader(last)));
        Protocol.WriteUnits(writer, view, rules.MapSize, 0);
        Assert.True(assembler.TryAccept(new NetDataReader(writer.CopyData())));
        Assert.Equal(70, assembler.Count);
        Assert.All(assembler.Units.Take(assembler.Count), unit => Assert.Equal(-1, unit.OwnerId));
        for (int index = 0; index < assembler.Count; index++)
            Assert.InRange(Vector2.Distance(view.Units[index].Position, assembler.Units[index].Position), 0, rules.MapSize / short.MaxValue);
        Assert.False(assembler.TryAccept(new NetDataReader(last)));
        for (int length = 0; length < last.Length; length++) Assert.False(new UnitSnapshotAssembler(rules).TryAccept(new NetDataReader(last[..length])));
    }

    [Fact]
    public void NetworkSettingsRejectUnsupportedSnapshotRates()
    {
        var document = new GameDataDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
        Assert.Equal(7777, new NetworkSettings(document).Port);
        document.Root["server"]!["snapshot"]!["unitHz"] = 11;
        Assert.Throws<ArgumentException>(() => new NetworkSettings(document));
    }

    [Fact]
    public void OldChunksCannotOverwriteANewerIncompleteFrameAndResetClearsSession()
    {
        var rules = GameRulesTests.Rules(); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        for (int index = 0; index < 70; index++) setup.Units.Add(new UnitSpawn(-1, "water", new Vector2(62)));
        var world = new SimulationWorld(rules, setup, 1); var writer = new NetDataWriter();
        Protocol.WriteUnits(writer, world.GetView(1), rules.MapSize, 0); byte[] first = writer.CopyData();
        Protocol.WriteUnits(writer, world.GetView(1), rules.MapSize, 1); byte[] last = writer.CopyData();
        BitConverter.GetBytes(10L).CopyTo(first, 1); BitConverter.GetBytes(10L).CopyTo(last, 1);
        byte[] old = (byte[])first.Clone(); BitConverter.GetBytes(9L).CopyTo(old, 1); BitConverter.GetBytes(999999).CopyTo(old, 29);
        var assembler = new UnitSnapshotAssembler(rules);
        for(int part=2;part<Protocol.ChunkCount(world.GetView(1).Units.Count);part++)
        {Protocol.WriteUnits(writer,world.GetView(1),rules.MapSize,part);byte[] extra=writer.CopyData();BitConverter.GetBytes(10L).CopyTo(extra,1);Assert.False(assembler.TryAccept(new NetDataReader(extra)));}
        Assert.False(assembler.TryAccept(new NetDataReader(first)));
        Assert.False(assembler.TryAccept(new NetDataReader(old)));
        Assert.True(assembler.TryAccept(new NetDataReader(last)));
        Assert.Equal(world.GetView(1).Units[0].Id, assembler.Units[0].Id);
        assembler.Reset();
        Assert.Equal(-1, assembler.Tick); Assert.Equal(0, assembler.Count);
    }
}
