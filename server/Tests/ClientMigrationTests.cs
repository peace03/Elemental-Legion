using System.Numerics;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;
using DeliveryMethod = LiteNetLib.DeliveryMethod;

namespace ElementalLegion.Tests;

/// <summary>실제 복제 코덱으로 승계 중 표시 보존과 방장 패배 후 인계 순서를 확인한다.</summary>
public sealed class ClientMigrationTests
{
    [Fact]
    public void RepeatedLobbyUpdatesAdvertiseHostingOnlyWhenItsValueOrGenerationChanges()
    {
        using var client = new ClientConnection(Rules(), Settings());
        client.HostingCapable = true;
        client.ConnectOnline(new Uri("wss://localhost:1/"), new byte[32]);
        Migration(client, MigrationPhase.None, 1, false);
        var writer = new NetDataWriter();
        M5Protocol.WriteLobbyState(writer, "빠른 15인 준비", 0, 2, 15,
            [new LobbyMember("참가자", 0, false)], 0);
        for (int update = 0; update < 30; update++) Receive(client, writer.CopyData());
        Assert.Equal(10, client.SentPayloadBytes);
        client.HostingCapable = true;
        Assert.Equal(10, client.SentPayloadBytes);
        client.HostingCapable = false;
        Assert.Equal(20, client.SentPayloadBytes);
        Migration(client, MigrationPhase.Resyncing, 2, false);
        Receive(client, writer.CopyData());
        Assert.Equal(30, client.SentPayloadBytes);
    }

    [Fact]
    public void FailedCandidateDoesNotReplaceTheLastCompleteDisplayedWorld()
    {
        var rules = Rules();
        using var client = new ClientConnection(rules, Settings());
        Welcome(client);
        foreach (byte[] packet in Baseline(rules, World(rules, 8, true).GetView(1))) Receive(client, packet);
        GameplayReplica completeGameplay = client.Gameplay;
        FogStateReplica completeFog = client.Fog;
        int ready = 0;
        client.MigrationChanged += notice => { if (notice.Reason == "ready") ready++; };

        Migration(client, MigrationPhase.Resyncing, 2, false);
        Welcome(client);
        foreach (byte[] packet in Baseline(rules, World(rules, 2, false).GetView(1))
            .Where(packet => packet[0] != (byte)PacketKind.Pose)) Receive(client, packet);
        Assert.True(client.IsMigrating);

        Migration(client, MigrationPhase.Recovering, 3, false);
        Migration(client, MigrationPhase.Resyncing, 3, false);
        Assert.Same(completeGameplay, client.Gameplay);
        Assert.Same(completeFog, client.Fog);
        Assert.Equal(0, ready);
        Welcome(client);
        foreach (byte[] packet in Baseline(rules, World(rules, 3, false).GetView(1))) Receive(client, packet);
        Assert.False(client.IsMigrating);
        Assert.Equal(1, ready);
        Assert.NotSame(completeGameplay, client.Gameplay);
    }

    [Fact]
    public void StagedForgetDoesNotChangeOldViewAndAppliesAfterAtomicReset()
    {
        var rules = Rules();
        using var client = new ClientConnection(rules, Settings());
        var timeline = new SnapshotTimeline(3);
        var publications = new List<string>();
        int forgottenUnits = 0, forgottenEntities = 0;
        int readyBackupAge = -1;
        client.UnitsReceived += snapshot => { publications.Add("units"); timeline.Push(snapshot); };
        client.PoseReceived += _ => publications.Add("pose");
        client.FogChanged += _ => publications.Add("fog");
        client.GameplayReceived += _ => publications.Add("gameplay");
        client.UnitsForgotten += (tick, ids, count) =>
        {
            publications.Add("forget-units"); forgottenUnits += count;
            for (int index = 0; index < count; index++) timeline.Forget(ids[index], tick);
        };
        client.EntitiesForgotten += (_, _, count) => { publications.Add("forget-entities"); forgottenEntities += count; };
        client.MigrationChanged += notice =>
        {
            if (notice.Phase != MigrationPhase.None || notice.Reason != "ready") return;
            publications.Add("reset"); timeline = new SnapshotTimeline(3); readyBackupAge = notice.BackupAgeMs;
        };
        Welcome(client);
        var original = World(rules, 8, extraUnit: true);
        var originalBaseline = Baseline(rules, original.GetView(1));
        foreach (byte[] packet in originalBaseline) Receive(client, packet);
        Assert.Equal(8, timeline.LatestTick);
        Assert.Equal(3, timeline.Count);
        GameplayReplica oldGameplay = client.Gameplay;
        FogStateReplica oldFog = client.Fog;
        Assert.Single(oldGameplay.State.Cores.ToArray());

        Migration(client, MigrationPhase.Recovering, 2, false);
        Migration(client, MigrationPhase.Resyncing, 2, false, backupAgeMs: 321);
        Welcome(client);
        publications.Clear();
        var restored = World(rules, 2, extraUnit: false);
        WorldView view = restored.GetView(1);
        int neutral = view.Units.Single(unit => unit.OwnerId < 0).Id;
        int core = Assert.Single(view.Cores).Id;
        byte[][] baseline = Baseline(rules, view);
        foreach (byte[] packet in baseline.Where(packet => packet[0] != (byte)PacketKind.Gameplay)) Receive(client, packet);

        var writer = new NetDataWriter();
        writer.Put((byte)PacketKind.Forget); writer.Put(view.Tick + 1); writer.Put(1); writer.Put(2U); writer.Put((ushort)2);
        writer.Put((byte)ObservedEntityKind.Unit); writer.Put(neutral);
        writer.Put((byte)ObservedEntityKind.Core); writer.Put(core);
        Receive(client, writer.CopyData());
        Receive(client, writer.CopyData());
        Assert.True(client.IsMigrating);
        Assert.Same(oldGameplay, client.Gameplay);
        Assert.Same(oldFog, client.Fog);
        Assert.Single(client.Gameplay.State.Cores.ToArray());
        Assert.Equal(8, timeline.LatestTick);
        Assert.True(timeline.TrySample(neutral, 8, out _));
        Assert.Empty(publications);
        Assert.Equal(0, forgottenUnits);
        Assert.Equal(0, forgottenEntities);

        foreach (byte[] packet in baseline.Where(packet => packet[0] == (byte)PacketKind.Gameplay)) Receive(client, packet);
        Assert.False(client.IsMigrating);
        Assert.Equal(321, readyBackupAge);
        Assert.NotSame(oldGameplay, client.Gameplay);
        Assert.NotSame(oldFog, client.Fog);
        Assert.Equal("reset", publications[0]);
        Assert.Equal(1, forgottenUnits);
        Assert.Equal(2, forgottenEntities);
        Assert.Equal(2, timeline.LatestTick);
        Assert.Equal(2, timeline.Count);
        Assert.False(timeline.TrySample(neutral, 2, out _));
        Assert.Empty(client.Gameplay.State.Cores.ToArray());
        Assert.Contains("pose", publications);
        Assert.Contains("fog", publications);
        Assert.Contains("gameplay", publications);
    }

    [Fact]
    public void DefeatedHostKeepsConnectionAndDefeatReasonUntilHandoffAcknowledged()
    {
        using var client = new ClientConnection(Rules(), Settings());
        Migration(client, MigrationPhase.None, 1, true);
        Welcome(client);
        Receive(client, [(byte)PacketKind.SessionEnded, (byte)SessionEndReason.BaseDestroyed]);
        Assert.True(client.Defeated);
        Assert.True(client.AwaitingHostLeave);
        Assert.True(client.IsHostServer);
        Assert.True(client.IsMigrating);
        Assert.Equal(ConnectionState.Connected, client.State);
        long sent = client.SentPayloadBytes;
        client.SendMove(1, Vector2.UnitX);
        client.SendTrain(2, 0);
        client.SendRally(3);
        Assert.Equal(sent, client.SentPayloadBytes);

        Migration(client, MigrationPhase.Resyncing, 2, false);
        Assert.True(client.AwaitingHostLeave);
        Assert.Equal(ConnectionState.Connected, client.State);
        Migration(client, MigrationPhase.None, 2, false, "left");
        Assert.Equal(ConnectionState.Disconnected, client.State);
        Assert.True(client.Defeated);
        Assert.False(client.Victorious);
        Assert.False(client.AwaitingHostLeave);
    }

    [Theory]
    [InlineData(false, SessionEndReason.BaseDestroyed)]
    [InlineData(true, SessionEndReason.Victory)]
    public void GuestDefeatAndGameVictoryDisconnectImmediately(bool host, SessionEndReason reason)
    {
        using var client = new ClientConnection(Rules(), Settings());
        Migration(client, MigrationPhase.None, 1, host);
        Welcome(client);
        Receive(client, [(byte)PacketKind.SessionEnded, (byte)reason]);
        Assert.Equal(ConnectionState.Disconnected, client.State);
        Assert.Equal(reason == SessionEndReason.BaseDestroyed, client.Defeated);
        Assert.Equal(reason == SessionEndReason.Victory, client.Victorious);
        Assert.False(client.AwaitingHostLeave);
    }

    private static GameDataDocument Data() => new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
    private static GameRules Rules() => new(Data());
    private static NetworkSettings Settings() => new(Data());

    private static SimulationWorld World(GameRules rules, int ticks, bool extraUnit)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        setup.Units.Add(new UnitSpawn(-1, "water", new Vector2(115, 100)));
        if (extraUnit) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(101, 100)));
        setup.Cores.Add(new CoreSpawn(new Vector2(115, 100), 5));
        var world = new SimulationWorld(rules, setup, 1);
        for (int tick = 0; tick < ticks; tick++) world.Step();
        return world;
    }

    private static byte[][] Baseline(GameRules rules, WorldView view)
    {
        var packets = new List<byte[]>();
        var writer = new NetDataWriter();
        var fog = new FogReplicationState(rules);
        fog.Prepare(view);
        for (int part = 0; part < fog.ChunkCount; part++) { fog.WriteChunk(writer, part); packets.Add(writer.CopyData()); }
        var gameplay = new GameplayReplicationState(rules);
        gameplay.Capture(view); gameplay.Prepare(view, fog.Revision);
        for (int part = 0; part < gameplay.ChunkCount; part++) { gameplay.WriteChunk(writer, part); packets.Add(writer.CopyData()); }
        for (int part = 0; part < Protocol.ChunkCount(view.Units.Count); part++)
        { Protocol.WriteUnits(writer, view, rules.MapSize, part, fog.Revision); packets.Add(writer.CopyData()); }
        Protocol.WritePose(writer, view); packets.Add(writer.CopyData());
        return packets.ToArray();
    }

    private static void Welcome(ClientConnection client)
    {
        var writer = new NetDataWriter(); Protocol.WriteWelcome(writer, 1); Receive(client, writer.CopyData());
    }

    private static void Migration(ClientConnection client, MigrationPhase phase, ulong generation, bool host, string reason = "", int backupAgeMs = -1)
    {
        var writer = new NetDataWriter(); HostMigrationProtocol.WriteMigration(writer, phase, generation, 2, host, reason, backupAgeMs);
        Receive(client, writer.CopyData());
    }

    private static void Receive(ClientConnection client, byte[] packet)
    {
        PacketKind kind = (PacketKind)packet[0];
        byte channel = kind == PacketKind.Gameplay ? (byte)2 :
            kind == PacketKind.FogBaseline || kind == PacketKind.FogDelta || kind == PacketKind.Forget ? (byte)1 : (byte)0;
        DeliveryMethod method = kind == PacketKind.Pose || kind == PacketKind.Units ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
        client.ReceivePacket(new NetDataReader(packet), channel, method);
    }
}
