using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>후보 예약은 양성 주문과 구분하고 현재 관측만 원자적으로 복제한다.</summary>
public sealed class RecoveryProtocolTests
{
    [Theory]
    [InlineData(RecoveryRole.Observer, 0)]
    [InlineData(RecoveryRole.Secure, 24)]
    [InlineData(RecoveryRole.Rescue, 59)]
    public void CandidateRoundTripsAndOnlyItsOwnKindIsForgotten(RecoveryRole role, int progress)
    {
        var rules = GameRulesTests.Rules();
        WorldView view = View(rules);
        var candidate = new ConversionCandidateView(19, 2, new Vector2(100), 200, role, progress);
        Candidates(view).Add(candidate);
        using var receiver = new GameplayReplica(rules);
        var frame = Publish(rules, view, receiver);
        var actual = Assert.Single(frame.Candidates.ToArray());
        Assert.Equal(candidate, actual);
        receiver.Forget(new ObservedEntityId(ObservedEntityKind.Unit, 19), view.Tick);
        Assert.Single(frame.Candidates.ToArray());
        receiver.Forget(new ObservedEntityId(ObservedEntityKind.Candidate, 19), view.Tick);
        Assert.Empty(frame.Candidates.ToArray());
        Assert.Equal(1, frame.Population);
        Assert.Equal(20, Protocol.Version);
    }

    [Theory]
    [InlineData("zeroId")]
    [InlineData("duplicate")]
    [InlineData("reverse")]
    [InlineData("element")]
    [InlineData("nan")]
    [InlineData("outside")]
    [InlineData("expired")]
    [InlineData("tooLate")]
    [InlineData("role")]
    [InlineData("negativeProgress")]
    [InlineData("tooMuchProgress")]
    [InlineData("observerProgress")]
    public void InvalidCandidateDoesNotPublishPartialState(string invalid)
    {
        var rules = GameRulesTests.Rules();
        WorldView view = View(rules);
        var point = invalid == "nan" ? new Vector2(float.NaN, 100) : invalid == "outside" ? new Vector2(rules.MapSize + 1) : new Vector2(100);
        int id = invalid == "zeroId" ? 0 : 19;
        int element = invalid == "element" ? rules.Elements.Count : 2;
        long end = invalid == "expired" ? view.Tick : invalid == "tooLate" ? view.Tick + rules.PvpRecoveryLifetimeTicks + 1 : 200;
        RecoveryRole role = invalid == "role" ? (RecoveryRole)3 : invalid == "observerProgress" ? RecoveryRole.Observer : RecoveryRole.Secure;
        int progress = invalid == "negativeProgress" ? -1 : invalid == "tooMuchProgress" ? (int)rules.PvpRecoverySecureTicks + 1 : 1;
        Candidates(view).Add(new ConversionCandidateView(id, element, point, end, role, progress));
        if (invalid == "duplicate" || invalid == "reverse")
            Candidates(view).Add(new ConversionCandidateView(invalid == "duplicate" ? id : id - 1, 2, new Vector2(100), 200, RecoveryRole.Observer, 0));
        using var receiver = new GameplayReplica(rules);
        foreach (byte[] packet in Packets(rules, view)) Assert.False(receiver.TryAccept(new NetDataReader(packet), 0));
        Assert.True(receiver.Overflowed);
        Assert.Equal(-1, receiver.State.Tick);
        Assert.Empty(receiver.State.Candidates.ToArray());
    }

    [Fact]
    public void CandidateForgottenWhileWaitingForFogCannotReturnFromQueuedFrame()
    {
        var rules = GameRulesTests.Rules();
        WorldView view = View(rules);
        Candidates(view).Add(new ConversionCandidateView(19, 2, new Vector2(100), 200, RecoveryRole.Rescue, 12));
        using var receiver = new GameplayReplica(rules);
        byte[][] packets = Packets(rules, view);
        for (int index = packets.Length - 1; index >= 0; index--)
        {
            Assert.Equal(index == 0, receiver.TryAccept(new NetDataReader(packets[index]), 0));
            Assert.Empty(receiver.State.Candidates.ToArray());
        }
        Assert.False(receiver.TryPublish(new FogStateReplica(rules), out _));
        receiver.Forget(new ObservedEntityId(ObservedEntityKind.Candidate, 19), view.Tick + 1);
        Assert.True(receiver.TryPublish(Fog(rules, view), out var frame));
        Assert.Empty(frame.Candidates.ToArray());
    }

    [Fact]
    public void CandidateCountStaysBoundedWithoutIncreasingTheDatagramLimit()
    {
        var rules = GameRulesTests.Rules();
        WorldView view = View(rules);
        for (int id = 1; id <= GameplayCodec.MaxObjects; id++)
            Candidates(view).Add(new ConversionCandidateView(id, 0, new Vector2(100), 200, RecoveryRole.Observer, 0));
        using var receiver = new GameplayReplica(rules);
        Assert.Equal(GameplayCodec.MaxObjects, Publish(rules, view, receiver).Candidates.Length);
        Candidates(view).Add(new ConversionCandidateView(GameplayCodec.MaxObjects + 1, 0, new Vector2(100), 200, RecoveryRole.Observer, 0));
        Assert.Throws<ReplicationOverflowException>(() => Packets(rules, view));
    }

    [Fact]
    public void CandidateReservationWithoutTrainingDoesNotInventAQueuedOrder()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(40), new Vector2(100)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        var view = new SimulationWorld(rules, setup, 1).GetView(1);
        typeof(WorldView).GetProperty(nameof(WorldView.ReservedPopulation))!.SetValue(view, 1);
        var squads = (SquadView[])typeof(WorldView).GetField("SquadBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        SquadView first = squads[0];
        squads[0] = new SquadView(first.Id, first.Position, first.Direction, first.Heading, first.Destination,
            first.Order, first.AppliedSequence, first.JoinedCount, first.ReinforcingCount, 1, first.MovementRevision);
        using var receiver = new GameplayReplica(rules);
        var frame = Publish(rules, view, receiver);
        Assert.Equal(1, frame.ReservedPopulation);
        Assert.Equal(-1, frame.TrainingElement);
        Assert.Equal(0, frame.TrainingTargetSquad);
        Assert.Empty(frame.TrainingQueue);
        Assert.Equal(1, frame.Squads[0].ReservedPopulation);
    }

    private static List<ConversionCandidateView> Candidates(WorldView view) =>
        (List<ConversionCandidateView>)typeof(WorldView).GetField("CandidateBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static WorldView View(GameRules rules)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(40), new Vector2(100)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        return new SimulationWorld(rules, setup, 1).GetView(1);
    }

    private static FogStateReplica Fog(GameRules rules, WorldView view)
    {
        var fog = new FogStateReplica(rules);
        var vision = new FogReplicationState(rules);
        vision.Prepare(view);
        var writer = new NetDataWriter();
        for (int part = 0; part < vision.ChunkCount; part++)
        {
            vision.WriteChunk(writer, part);
            fog.TryAccept(new NetDataReader(writer.CopyData()));
        }
        return fog;
    }

    private static byte[][] Packets(GameRules rules, WorldView view)
    {
        var writer = new NetDataWriter();
        var sender = new GameplayReplicationState(rules);
        sender.Prepare(view, 1);
        var packets = new byte[sender.ChunkCount][];
        for (int part = 0; part < sender.ChunkCount; part++)
        {
            sender.WriteChunk(writer, part);
            Assert.InRange(writer.Length, 1, Protocol.MaxDatagramBytes);
            packets[part] = writer.CopyData();
        }
        return packets;
    }

    internal static GameplayFrame Publish(GameRules rules, WorldView view, GameplayReplica receiver)
    {
        byte[][] packets = Packets(rules, view);
        for (int index = 0; index < packets.Length; index++)
            Assert.Equal(index == packets.Length - 1, receiver.TryAccept(new NetDataReader(packets[index]), 0));
        Assert.True(receiver.TryPublish(Fog(rules, view), out var frame));
        return frame;
    }
}
