using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using System.Numerics;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>확인 명령에는 카운터·소유자를 받지 않고 여정 결과는 자기에게만 보낸다.</summary>
public sealed class JourneyProtocolTests
{
    [Fact]
    public void JourneyAcknowledgmentContainsOnlyItsSequence()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        writer.Put((byte)39);
        writer.Put(17UL);
        byte[] packet = writer.CopyData();
        Assert.True(Protocol.TryReadCommand(new NetDataReader(packet), rules, out var command));
        Assert.Equal(CommandKind.AcknowledgeJourney, command.Kind);
        Assert.Equal(17UL, command.Sequence);
        var encoded = new NetDataWriter();
        Protocol.WriteAcknowledgeJourney(encoded, 17);
        Assert.Equal(packet, encoded.CopyData());
        for (int length = 0; length < packet.Length; length++)
            Assert.False(Protocol.TryReadCommand(new NetDataReader(packet[..length]), rules, out _));
        writer.Put(2);
        Assert.False(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out _));
        Array.Clear(packet, 1, 8);
        Assert.False(Protocol.TryReadCommand(new NetDataReader(packet), rules, out _));
    }

    [Fact]
    public void GameplayCarriesOnlyTheRecipientsJourneyAndKeepsTheDatagramLimit()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(250), new Vector2(270), cores: 23));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(750), new Vector2(770), cores: 61));
        var world = new SimulationWorld(rules, setup, 1);
        using var first = new GameplayReplica(rules);
        using var second = new GameplayReplica(rules);
        var a = RecoveryProtocolTests.Publish(rules, world.GetView(1), first).Journey;
        var b = RecoveryProtocolTests.Publish(rules, world.GetView(2), second).Journey;
        Assert.Equal(1, a.Id); Assert.Equal(2, b.Id);
        Assert.Equal(23, a.Total.CoresIn); Assert.Equal(61, b.Total.CoresIn);
        Assert.Equal(a.Total, a.SinceLast);
        Assert.Equal(b.Total, b.SinceLast);
        Assert.Equal(world.GetView(1).Journey, a);
        first.Reset();Assert.Equal(0, first.State.Journey.Id);
    }

    [Theory]
    [InlineData(SessionEndReason.BaseDestroyed)]
    [InlineData(SessionEndReason.Victory)]
    public void SessionResultRoundTripsTheExactSameOwnerOnlySummaryOverBothTransports(SessionEndReason reason)
    {
        var total = new JourneyTotals(10, 2, 5, long.MaxValue, 8, 3, 1);
        var delta = new JourneyTotals(3, 0, 1, 9, 8, 1, 0);
        var result = new JourneyResultView(7, total, delta);
        var writer = new NetDataWriter();
        Protocol.WriteSessionEnded(writer, reason, result);
        byte[] wss = writer.CopyData();
        Assert.Equal(122, wss.Length);
        Assert.True(Protocol.TryReadSessionEnded(new NetDataReader(wss), 7, out var readReason, out var actual));
        Assert.Equal(reason, readReason); Assert.Equal(result, actual);
        Protocol.WriteSessionEndData(writer, reason, result);
        byte[] udp = writer.CopyData();
        Assert.Equal(121, udp.Length);Assert.Equal(wss[1..], udp);
        Assert.True(Protocol.TryReadSessionEndData(new NetDataReader(udp), 7, out readReason, out actual));
        Assert.Equal(reason, readReason); Assert.Equal(result, actual);
        Assert.False(Protocol.TryReadSessionEndData(new NetDataReader(udp), 8, out _, out _));
        for (int length = 0; length < wss.Length; length++)
            Assert.False(Protocol.TryReadSessionEnded(new NetDataReader(wss[..length]), 7, out _, out _));
        Assert.False(Protocol.TryReadSessionEnded(new NetDataReader(wss.Concat(new byte[1]).ToArray()), 7, out _, out _));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("reason")]
    [InlineData("negativeTotal")]
    [InlineData("negativeDelta")]
    [InlineData("deltaExceedsTotal")]
    public void InvalidSessionResultIsRejectedWithoutUsingItsCounters(string invalid)
    {
        var writer = new NetDataWriter();
        var total = new JourneyTotals(10, 2, 5, 20, 8, 3, 1);
        Protocol.WriteSessionEnded(writer, SessionEndReason.BaseDestroyed, new JourneyResultView(7, total, total));
        byte[] bytes = writer.CopyData();
        switch (invalid)
        {
            case "owner": BitConverter.GetBytes(8L).CopyTo(bytes, 2); break;
            case "reason": bytes[1] = 0; break;
            case "negativeTotal": BitConverter.GetBytes(-1L).CopyTo(bytes, 10); break;
            case "negativeDelta": BitConverter.GetBytes(-1L).CopyTo(bytes, 66); break;
            case "deltaExceedsTotal": BitConverter.GetBytes(11L).CopyTo(bytes, 66); break;
        }
        Assert.False(Protocol.TryReadSessionEnded(new NetDataReader(bytes), 7, out _, out var actual));
        Assert.Equal(0, actual.Id);
    }
}
