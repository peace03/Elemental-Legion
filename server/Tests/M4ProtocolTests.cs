using System.Diagnostics;
using System.Numerics;
using System.Text;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>M4 명령과 관측의 바이너리 경계를 검증한다.</summary>
public sealed class M4ProtocolTests
{
    [Fact]
    public void HelloRejectsThePreviousVersion()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        Protocol.WriteHello(writer, rules.DataHash, rules.StartingElements[0]);
        byte[] packet = writer.CopyData();
        Assert.Equal(Protocol.Version, BitConverter.ToUInt16(packet, 1));
        BitConverter.GetBytes((ushort)(Protocol.Version - 1)).CopyTo(packet, 1);
        Assert.Equal(JoinFailure.VersionMismatch, Protocol.ReadHello(new NetDataReader(packet), rules, out _));
    }

    [Fact]
    public void HelloAcceptsBoundedUtf8NicknameAndRejectsInvalidPayloads()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        int start = rules.StartingElements[0];
        string name = new string('별', 16);
        Protocol.WriteHello(writer, rules.DataHash, start, name);
        byte[] packet = writer.CopyData();
        Assert.Equal(86, packet.Length);
        Assert.Equal(JoinFailure.None, Protocol.ReadHello(new NetDataReader(packet), rules, out int element, out string nickname));
        Assert.Equal(start, element);
        Assert.Equal(name, nickname);
        Assert.Equal(JoinFailure.Malformed, Protocol.ReadHello(new NetDataReader(packet.Concat(new byte[] { 0 }).ToArray()), rules, out _));
        Assert.Equal(JoinFailure.InvalidNickname, Protocol.ReadHello(new NetDataReader(packet[..^1]), rules, out _));

        byte[] prefix = packet[..36];
        foreach (byte[] invalid in new[]
        {
            Array.Empty<byte>(), new byte[] { 0xc0, 0xaf }, Encoding.UTF8.GetBytes(new string('A', 17)),
            Encoding.UTF8.GetBytes("<color=red>"), Encoding.UTF8.GetBytes("A\u202eB"), Encoding.UTF8.GetBytes(" _- "),
            Encoding.UTF8.GetBytes("별\n밤"), Encoding.UTF8.GetBytes("\u3164")
        })
        {
            writer.Reset();
            writer.Put(prefix);
            writer.Put((ushort)(invalid.Length + 1));
            writer.Put(invalid);
            Assert.Equal(JoinFailure.InvalidNickname, Protocol.ReadHello(new NetDataReader(writer.CopyData()), rules, out _));
        }
    }

    [Fact]
    public void GameplayReplicatesWaitingOrdersToTheirOwnerInOrder()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(400), new Vector2(400, 220)));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.TryEnqueue(1, SimCommand.Train(2, "light"));
        world.TryEnqueue(1, SimCommand.Train(3, "fire"));
        world.Step();
        var first = RoundTrip(world.GetView(1), rules);
        var other = RoundTrip(world.GetView(2), rules);
        Assert.Equal(new[] { rules.ElementIndex("light"), rules.ElementIndex("fire") }, first.TrainingQueue.Select(order => order.Element));
        Assert.Equal(new ulong[] { 2, 3 }, first.TrainingQueue.Select(order => order.Id));
        Assert.Equal(3, first.ReservedPopulation);
        Assert.Empty(other.TrainingQueue);
        Assert.Equal(0, other.ReservedPopulation);

        var writer = new NetDataWriter();
        Protocol.WriteCancelTraining(writer, 4, 2);
        byte[] packet = writer.CopyData();
        Assert.False(Protocol.TryReadCommand(new NetDataReader(packet[..^1]), rules, out _));
        Assert.True(Protocol.TryReadCommand(new NetDataReader(packet), rules, out var cancel));
        Assert.Equal(2UL, cancel.TrainingOrderId);
        world.TryEnqueue(1, cancel);
        world.Step();
        first = RoundTrip(world.GetView(1), rules);
        Assert.Equal(3UL, Assert.Single(first.TrainingQueue).Id);
        Assert.Contains(first.Events.ToArray(), entry => entry.Data.Kind == SimEventKind.CommandApplied &&
            entry.Data.Detail == (int)CommandKind.CancelTraining);
        Protocol.WriteCancelTraining(writer, 4, 4);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out _));
    }

    [Fact]
    public void BuildRequiresAnExactPayloadAndAValidPlacementIntent()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        writer.Put((byte)14);
        writer.Put(9UL);
        writer.Put((byte)1);
        writer.Put(25);
        writer.Put(30);
        writer.Put(byte.MaxValue);
        byte[] packet = writer.CopyData();

        Assert.True(Protocol.TryReadCommand(new NetDataReader(packet), rules, out var command));
        Assert.Equal(CommandKind.Build, command.Kind);
        Assert.Equal(9UL, command.Sequence);
        Assert.Equal(BuildingKind.NatureForge, command.BuildingKind);
        Assert.Equal(25, command.CellX);
        Assert.Equal(30, command.CellZ);
        Assert.Equal(-1, command.TowerElement);
        for (int length = 0; length < packet.Length; length++)
            Assert.False(Protocol.TryReadCommand(new NetDataReader(packet[..length]), rules, out _));
        Assert.False(Protocol.TryReadCommand(new NetDataReader(packet.Append((byte)0).ToArray()), rules, out _));

        packet[^1] = 0;
        Assert.False(Protocol.TryReadCommand(new NetDataReader(packet), rules, out _));
        packet[^1] = byte.MaxValue;
        packet[10] = packet[11] = packet[12] = packet[13] = byte.MaxValue;
        Assert.False(Protocol.TryReadCommand(new NetDataReader(packet), rules, out _));
    }

    [Fact]
    public void UpgradeAndEachTowerGroupHaveDistinctValidIntents()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        Protocol.WriteUpgradeBase(writer, 12);
        Assert.Equal(9, writer.Length);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var upgrade));
        Assert.Equal(CommandKind.UpgradeBase, upgrade.Kind);
        Assert.Equal(12UL, upgrade.Sequence);

        int energy = rules.ElementIndex("light");
        Protocol.WriteBuild(writer, 13, BuildingKind.EnergyTower, 10, 20, energy);
        Assert.Equal(19, writer.Length);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var build));
        Assert.Equal(energy, build.TowerElement);
        Protocol.WriteBuild(writer, 13, BuildingKind.EnergyTower, 10, 20, rules.StartingElements[0]);
        Assert.False(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out _));
        int nature = rules.ElementIndex("fire");
        Protocol.WriteBuild(writer, 14, BuildingKind.NatureTower, 10, 20, nature);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out build));
        Assert.Equal(nature, build.TowerElement);
        Protocol.WriteBuild(writer, 14, BuildingKind.NatureTower, 10, 20, energy);
        Assert.False(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out _));
        Protocol.WriteBuild(writer, 14, BuildingKind.NatureTower, 10, 20);
        Assert.False(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out _));
        Protocol.WriteBuild(writer, 13, BuildingKind.Dome, 10, 20);
        Assert.False(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out _));
    }

    [Fact]
    public void ReusedSlotSupportsHigherOwnerIdsAndGhostFieldsAcrossBothSnapshots()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        int ghost = rules.MaxPlayers + 1, observer = ghost + 1;
        var ghostPosition = new Vector2(100, 100);
        var observerPosition = new Vector2(100, 120);
        setup.Players.Add(new PlayerSpawn(ghost, "water", ghostPosition, ghostPosition));
        setup.Players.Add(new PlayerSpawn(observer, "fire", observerPosition, observerPosition));
        setup.Units.Add(new UnitSpawn(ghost, "water", ghostPosition, garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.True(world.BeginGhost(ghost));
        world.Step();
        WorldView view = world.GetView(observer);

        var writer = new NetDataWriter();
        var unitReplica = new UnitSnapshotAssembler(rules);
        Protocol.WriteUnits(writer, view, rules.MapSize, 0, 1);
        Assert.True(unitReplica.TryAccept(new NetDataReader(writer.CopyData())));
        Assert.Equal(observer, unitReplica.PlayerId);
        Assert.Equal(ghost, unitReplica.Units[0].OwnerId);
        Assert.True(unitReplica.Units[0].Ghost);
        byte[] invalidFlags = writer.CopyData();
        invalidFlags[42] |= 16;
        Assert.False(new UnitSnapshotAssembler(rules).TryAccept(new NetDataReader(invalidFlags)));

        GameplayFrame frame = RoundTrip(view, rules);
        var building = Assert.Single(frame.Buildings.ToArray(), item => item.OwnerId == ghost);
        Assert.Equal(rules.BuildingHealth(BuildingKind.Base), building.Health);
        Assert.Equal(building.Health, building.MaxHealth);
        Assert.True(building.GhostEndTick > frame.Tick);
        Assert.DoesNotContain(frame.Standings, row => row.PlayerId == ghost);
    }

    [Theory]
    [InlineData(122, true)]
    [InlineData(128, false)]
    public void TowerAttackUsesTypedObservationWhenAUnitHasTheSameId(int towerX, bool visible)
    {
        var json = GameRulesTests.Data().Root.DeepClone();
        json["towers"]!["range"] = 40;
        json["towers"]!["buildSeconds"] = 0;
        var rules = new GameRules(new GameDataDocument(json.ToString()));
        var setup = new WorldSetup();
        var observer = new Vector2(100, 100);
        var owner = new Vector2(143, 100);
        setup.Players.Add(new PlayerSpawn(1, "water", observer, observer));
        setup.Players.Add(new PlayerSpawn(2, "earth", owner, owner, cores: 100));
        for (int index = 0; index < 3; index++)
            setup.Units.Add(new UnitSpawn(1, "water", observer + Vector2.UnitY * (index * 2), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.Build(1, BuildingKind.NatureTower, towerX, 100, rules.ElementIndex("earth"))));
        world.Step();

        var frame = RoundTrip(world.GetView(1), rules);
        var attack = Assert.Single(frame.Events.ToArray(), entry => entry.Data.Kind == SimEventKind.Attack).Data;
        Assert.Equal(visible ? 2 : -1, attack.OwnerId);
        Assert.Equal(visible ? 3 : 0, attack.SourceId);
        Assert.Equal(visible ? ObservedEntityKind.Building : ObservedEntityKind.Unit, attack.SourceKind);
        Assert.Equal(visible, attack.HasSourcePosition);
        Assert.Equal(ObservedEntityKind.Unit, attack.TargetKind);
        Assert.Equal(1, attack.TargetId);
    }

    [Fact]
    public void DestroyedBuildingIsForgottenWithoutForgettingAUnitWithTheSameId()
    {
        var json = GameRulesTests.Data().Root.DeepClone();
        json["buildings"]!["base"]!["hp"] = 1;
        var rules = new GameRules(new GameDataDocument(json.ToString()));
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 100)));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(300, 300), new Vector2(107, 100)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(107, 100)));
        var world = new SimulationWorld(rules, setup, 1);
        var fog = new FogReplicationState(rules);
        fog.Prepare(world.GetView(2));
        fog.Commit();

        world.Step();
        var view = world.GetView(2);
        Assert.False(world.TryGetView(1, out _));
        Assert.Equal(1, Assert.Single(view.Units).Id);
        fog.Prepare(view);
        Assert.Equal(1, fog.ForgetChunkCount);
        var writer = new NetDataWriter();
        fog.WriteForget(writer, 0);
        var ids = new ObservedEntityId[FogProtocol.ForgetPerChunk];
        Assert.True(FogProtocol.TryReadEntityForget(new NetDataReader(writer.CopyData()), ids, out _, out _, out _, out int count));
        Assert.Equal(new ObservedEntityId(ObservedEntityKind.Building, 1), Assert.Single(ids.Take(count)));

        var attack = Assert.Single(RoundTrip(view, rules).Events.ToArray(), entry => entry.Data.Kind == SimEventKind.Attack).Data;
        Assert.Equal(0, attack.TargetId);
        Assert.Equal(0, attack.EntityId);
    }

    [Fact]
    public void UdpBuildAndUpgradeOnlyPublishResultsToTheOwner()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        using var host = new UdpGameServer(rules, settings, "m4-demo");
        host.Start(0);
        using var first = new ClientConnection(rules, settings);
        using var second = new ClientConnection(rules, settings);
        const string nickname = "별빛 군단_1";
        first.Connect("127.0.0.1", host.Port, rules.StartingElements[0], nickname);
        second.Connect("127.0.0.1", host.Port, rules.StartingElements[1], nickname);
        Pump(() => first.Gameplay.State.SpatialReady && second.Gameplay.State.SpatialReady, host, first, second);
        int firstOwner = first.PlayerId;
        var applied = new List<ulong>();
        var other = new List<ulong>();
        first.GameplayReceived += frame => AddApplied(frame, applied);
        second.GameplayReceived += frame => AddApplied(frame, other);
        Vector2 site = first.Gameplay.State.BasePosition + new Vector2(rules.BuildRadius-rules.NatureForgeRadius-rules.BuildGridSize, 0);
        first.SendBuild(1, BuildingKind.NatureForge,
            (int)MathF.Round(site.X / rules.BuildGridSize), (int)MathF.Round(site.Y / rules.BuildGridSize));
        first.SendUpgradeBase(2);
        Pump(() => applied.Count == 2 && second.Gameplay.State.Tick >= first.Gameplay.State.Tick, host, first, second);
        Assert.Equal(new ulong[] { 1, 2 }, applied);
        Assert.Empty(other);
        Assert.Equal(2, first.Gameplay.State.BaseLevel);
        Assert.Equal(1, second.Gameplay.State.BaseLevel);
        var pending = Assert.Single(first.Gameplay.State.Buildings.ToArray(), building => building.Kind == BuildingKind.NatureForge);
        Assert.True(pending.ConstructionEndTick > first.Gameplay.State.Tick);
        Assert.Equal(0, first.Gameplay.State.OwnBuildingCount);
        Assert.DoesNotContain(second.Gameplay.State.Buildings.ToArray(), building => building.OwnerId == firstOwner);
        Assert.Equal(new[] { firstOwner, second.PlayerId }, second.Gameplay.State.Standings.Select(row => row.PlayerId));
        Assert.All(second.Gameplay.State.Standings, row => Assert.Equal(nickname, row.Nickname));
        Assert.True(second.Gameplay.State.Standings[0].Score > second.Gameplay.State.Standings[1].Score);
    }

    [Fact]
    public void UdpBaseDefeatClosesOnlyTheLosingSessionAndAllowsFreshJoin()
    {
        var original = GameRulesTests.Data();
        var json = original.Root.DeepClone();
        int earth = new GameRules(original).ElementIndex("earth");
        json["buildings"]!["base"]!["hp"] = 1;
        json["elements"]![earth]!["attack"] = 1000;
        json["elements"]![earth]!["hp"] = 10000;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        using var host = new UdpGameServer(rules, settings, "m3-demo");
        host.Start(0);
        using var victim = new ClientConnection(rules, settings);
        using var attacker = new ClientConnection(rules, settings);
        victim.Connect("127.0.0.1", host.Port, rules.ElementIndex("smoke"));
        attacker.Connect("127.0.0.1", host.Port, earth);
        Pump(() => victim.Gameplay.State.SpatialReady && attacker.Gameplay.State.SpatialReady, host, victim, attacker);

        int victimOwner = victim.PlayerId;
        var attackerView = host.Simulation.World.GetView(attacker.PlayerId);
        var victimBase = victim.Gameplay.State.BasePosition;
        attacker.SendMove(1, Vector2.Normalize(victimBase - attackerView.LegionPosition));
        Pump(() => victim.Defeated, host, victim, attacker);
        Assert.Equal(ConnectionState.Disconnected, victim.State);
        Assert.Equal(JoinFailure.None, victim.JoinFailure);
        Assert.Equal(ConnectionState.Connected, attacker.State);
        Assert.Equal(1, host.Simulation.ConnectedCount);
        Assert.False(host.Simulation.World.TryGetView(victimOwner, out _));
        Assert.Equal(victimOwner, host.GetGameplayReport(victimOwner).PlayerId);
        attacker.SendMove(2, Vector2.Zero);

        victim.Connect("127.0.0.1", host.Port, rules.ElementIndex("water"));
        Pump(() => victim.Gameplay.State.SpatialReady, host, victim, attacker);
        Assert.False(victim.Defeated);
        Assert.True(victim.PlayerId > victimOwner);
        Assert.Equal(rules.ElementIndex("water"), victim.Gameplay.State.MainElement);
        Assert.NotEqual(victimBase, victim.Gameplay.State.BasePosition);
        Assert.True(Vector2.Distance(victim.Gameplay.State.BasePosition, attackerView.LegionPosition) >= rules.SpawnMinEnemyUnitDistance);
    }

    private static void AddApplied(GameplayFrame frame, List<ulong> results)
    {
        foreach (NetworkEvent item in frame.Events)
            if (item.Data.Kind == SimEventKind.CommandApplied) results.Add(item.Data.Sequence);
    }

    private static GameplayFrame RoundTrip(WorldView view, GameRules rules)
    {
        var writer = new NetDataWriter();
        var fog = new FogStateReplica(rules);
        var fogSender = new FogReplicationState(rules);
        fogSender.Prepare(view);
        for (int part = 0; part < fogSender.ChunkCount; part++)
        {
            fogSender.WriteChunk(writer, part);
            fog.TryAccept(new NetDataReader(writer.CopyData()));
        }
        var replica = new GameplayReplica(rules);
        Assert.True(replica.BindPlayer(view.PlayerId));
        var gameplay = new GameplayReplicationState(rules);
        gameplay.Capture(view);
        gameplay.Prepare(view, fog.Revision);
        for (int part = 0; part < gameplay.ChunkCount; part++)
        {
            gameplay.WriteChunk(writer, part);
            replica.TryAccept(new NetDataReader(writer.CopyData()), 0);
        }
        Assert.True(replica.TryPublish(fog, out var frame));
        Assert.Equal(0, replica.PendingBytes);
        return frame;
    }

    private static void Pump(Func<bool> complete, UdpGameServer host, params ClientConnection[] clients)
    {
        var timeout = Stopwatch.StartNew();
        while (!complete() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            host.Poll();
            for (int tick = 0; tick < 4; tick++) host.Step();
            foreach (ClientConnection client in clients) client.Poll();
            Thread.Sleep(1);
        }
        Assert.True(complete(), $"루프백 UDP에서 기대한 상태를 받지 못했습니다. tick={host.Simulation.World.Metrics.Tick}; " +
            string.Join(", ", clients.Select(client => $"{client.PlayerId}:{client.State}:{client.Error}:{client.Defeated}:" +
                (host.Simulation.World.TryGetView(client.PlayerId, out var view) ? $"{view.LegionPosition}/{view.Population}/{view.Buildings.Count}" : "removed"))));
    }
}
