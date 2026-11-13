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
    public void MaximumPopulationBattleFitsAnObservationBatch()
    {
        var rules = GameRulesTests.Rules();
        var setup = ScenarioFactory.Create(rules, 15, ScenarioKind.MainWorldDense);
        var world = new SimulationWorld(rules, setup, 1);
        WorldView view = world.GetView(1);
        var sender = new GameplayReplicationState(rules);
        var settings = new NetworkSettings(GameRulesTests.Data());
        int expectedDeaths = 0, expectedPickups = 0, expectedCues = 0;
        for (int index = 0; index < settings.UnitsEveryTicks; index++)
        {
            world.Step();
            sender.Capture(view);
            var conversions = view.Events.Where(item => item.Kind is SimEventKind.Converted or SimEventKind.Rescued).Select(item => item.EntityId).ToHashSet();
            expectedDeaths += view.Events.Count(item => item.Kind == SimEventKind.Death && item.EntityId != 0 &&
                (item.EntityKind != ObservedEntityKind.Unit || !conversions.Contains(item.EntityId)));
            expectedPickups += view.Events.Count(item => item.Kind == SimEventKind.CorePickedUp && item.OwnerId == view.PlayerId);
            expectedCues += view.Events.Count(item => item.Kind is SimEventKind.Attack or SimEventKind.Impact or SimEventKind.Healed or SimEventKind.ShieldPulse);
        }
        const int available = 950 * 1024;
        GameplayFrame frame = RoundTrip(view, rules, sender, available);
        Assert.True(frame.Events.Length > 8192);
        var events = frame.Events.ToArray();
        Assert.Equal(expectedDeaths, events.Count(item => item.Data.Kind == SimEventKind.Death));
        Assert.Equal(expectedPickups, events.Count(item => item.Data.Kind == SimEventKind.CorePickedUp && item.Data.OwnerId == view.PlayerId));
        Assert.InRange(events.Count(item => item.Data.Kind is SimEventKind.Attack or SimEventKind.Impact or SimEventKind.Healed or SimEventKind.ShieldPulse), 1, expectedCues - 1);
    }

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
    public void WelcomeCarriesActualMapSizeForMigratedRooms()
    {
        var writer = new NetDataWriter();
        Protocol.WriteWelcome(writer, 7, 240, roomPlayers: 2);
        byte[] bytes = writer.CopyData();
        Assert.Equal(10, bytes.Length);
        Assert.True(Protocol.TryReadWelcome(new NetDataReader(bytes), out int owner, out _, out _, out int players, out float mapSize));
        Assert.Equal(7, owner);
        Assert.Equal(2, players);
        Assert.Equal(240f, mapSize);
        Assert.False(Protocol.TryReadWelcome(new NetDataReader(bytes[..6]), out _, out _, out _, out _, out _));
        foreach (float invalid in new[] { 0, -1, float.NaN, float.PositiveInfinity })
        {
            var packet = (byte[])bytes.Clone();
            BitConverter.GetBytes(invalid).CopyTo(packet, 6);
            Assert.False(Protocol.TryReadWelcome(new NetDataReader(packet), out _, out _, out _, out _, out _));
        }
    }

    [Fact]
    public void FacilityUpgradeIntentRequiresExactlyAnIdBranchAndNextRank()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        Protocol.WriteUpgradeFacility(writer, 17, 9, FacilityUpgradeBranch.B, 4);
        byte[] bytes = writer.CopyData();
        Assert.Equal(15, bytes.Length);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(bytes), rules, out SimCommand command));
        Assert.Equal(CommandKind.UpgradeFacility, command.Kind);
        Assert.Equal(17UL, command.Sequence);
        Assert.Equal(9, command.BuildingId);
        Assert.Equal(FacilityUpgradeBranch.B, command.UpgradeBranch);
        Assert.Equal(4, command.TargetRank);
        for (int length = 0; length < bytes.Length; length++)
            Assert.False(Protocol.TryReadCommand(new NetDataReader(bytes[..length]), rules, out _));
        Assert.False(Protocol.TryReadCommand(new NetDataReader(bytes.Append((byte)0).ToArray()), rules, out _));
        foreach ((ulong sequence, int id, byte branch, byte rank) in new (ulong, int, byte, byte)[]
            { (0, 9, 2, 4), (17, 0, 2, 4), (17, -1, 2, 4), (17, 9, 0, 4), (17, 9, 3, 4), (17, 9, 2, 0), (17, 9, 2, 5) })
        {
            Protocol.WriteUpgradeFacility(writer, sequence, id, (FacilityUpgradeBranch)branch, rank);
            Assert.False(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out _));
        }
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
        for (int rank = 1; rank <= GameRules.MaxFacilityUpgradeRank; rank++)
            json["facilityUpgrades"]!["tower"]!["a"]![rank - 1]!["effect"] = 40 + rank;
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

    [Theory]
    [InlineData(122, true)]
    [InlineData(128, false)]
    public void CollectorUpgradeAndProductionArePrivateEvenWhenBuildingIsObserved(int collectorX, bool visible)
    {
        var rules = InstantCollectorRules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 100)));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(143, 100), new Vector2(143, 100), baseLevel: 2, cores: 100));
        var world = new SimulationWorld(rules, setup, 1);
        WorldView observer = world.GetView(1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.Build(1, BuildingKind.Collector, collectorX, 100)));
        world.Step();

        WorldView owner = world.GetView(2);
        int collectorId = Assert.Single(owner.Buildings, building => building.Kind == BuildingKind.Collector).Id;
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.UpgradeFacility(2, collectorId, FacilityUpgradeBranch.B, 1)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.UpgradeFacility(1, collectorId, FacilityUpgradeBranch.B, 1)));
        world.Step();
        GameplayFrame ownerFrame = RoundTrip(owner, rules);
        GameplayFrame observerFrame = RoundTrip(observer, rules);
        var upgraded = Assert.Single(ownerFrame.Buildings.ToArray(), item => item.Id == collectorId);
        Assert.Equal(FacilityUpgradeBranch.B, upgraded.UpgradeBranch);
        Assert.Equal(1, upgraded.UpgradeRank);
        Assert.True(upgraded.NextProductionTick > ownerFrame.Tick);
        Assert.Contains(ownerFrame.Events.ToArray(), item => item.Data.Kind == SimEventKind.CommandApplied && item.Data.Detail == (int)CommandKind.UpgradeFacility);
        var rejected = Assert.Single(observerFrame.Events.ToArray(), item => item.Data.Kind == SimEventKind.CommandRejected);
        Assert.Equal((int)CommandFailure.InvalidPlacement, rejected.Data.Detail);
        Assert.DoesNotContain(observerFrame.Events.ToArray(), item => item.Data.Kind == SimEventKind.CommandApplied);
        Assert.Equal(visible, observer.Buildings.Any(building => building.Id == collectorId));
        var seen = observerFrame.Buildings.ToArray().SingleOrDefault(item => item.Id == collectorId);
        Assert.Equal(visible ? collectorId : 0, seen.Id);
        Assert.Equal(FacilityUpgradeBranch.None, seen.UpgradeBranch);
        Assert.Equal(0, seen.UpgradeRank);
        Assert.Equal(0, seen.NextProductionTick);
        Assert.All(observer.Buildings, item => Assert.Equal(0, item.UpgradeRank));

        while (owner.Tick < upgraded.NextProductionTick) world.Step();
        var receipt = Assert.Single(RoundTrip(owner, rules).Events.ToArray(), item => item.Data.Kind == SimEventKind.CoresProduced);
        Assert.Equal(collectorId, receipt.Data.EntityId);
        Assert.Equal(2f, receipt.Data.Amount);
        Assert.Equal(Vector2.Zero, receipt.Data.Position);
        Assert.DoesNotContain(observer.Events, item => item.Kind == SimEventKind.CoresProduced);
        Assert.DoesNotContain(RoundTrip(observer, rules).Events.ToArray(), item => item.Data.Kind == SimEventKind.CoresProduced);
    }

    [Fact]
    public void ProductionReceiptRetainsQuantityWithoutACurrentlyObservedCollector()
    {
        var rules = InstantCollectorRules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 100), cores: 100));
        var world = new SimulationWorld(rules, setup, 1);
        WorldView owner = world.GetView(1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 100, 120)));
        world.Step();
        BuildingView collector = Assert.Single(owner.Buildings, item => item.Kind == BuildingKind.Collector);
        while (owner.Tick < collector.NextProductionTick) world.Step();
        Assert.Contains(owner.Events, item => item.Kind == SimEventKind.CoresProduced && item.EntityId == collector.Id);
        var sender = new GameplayReplicationState(rules);
        sender.Capture(owner);
        long productionTick = owner.Tick;
        world.Step();
        owner.BuildingBuffer.RemoveAll(item => item.Id == collector.Id);

        NetworkEvent receipt = Assert.Single(RoundTrip(owner, rules, sender).Events.ToArray(), item => item.Data.Kind == SimEventKind.CoresProduced);
        Assert.Equal(productionTick, receipt.Tick);
        Assert.Equal(1UL, receipt.Serial);
        Assert.Equal(1, receipt.Data.OwnerId);
        Assert.Equal((float)rules.CollectorCoresPerCycle, receipt.Data.Amount);
        Assert.Equal(ObservedEntityKind.Building, receipt.Data.EntityKind);
        Assert.Equal(0, receipt.Data.EntityId);
        Assert.Equal(Vector2.Zero, receipt.Data.Position);
        Assert.Equal(0, receipt.Data.SourceId);
        Assert.Equal(0, receipt.Data.TargetId);
        Assert.Equal(ObservedEntityKind.Unit, receipt.Data.TargetKind);
        Assert.Equal(0, receipt.Data.Detail);
        Assert.Equal(0UL, receipt.Data.Sequence);
        Assert.Equal(-1, receipt.Data.Element);
    }

    [Fact]
    public void NonSpatialPickupRequiresOwnerAndCanonicalFields()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        WorldView view = world.GetView(1);
        var body = new NetDataWriter();
        var frame = new GameplayFrame(rules);

        Assert.True(AcceptsReceipt());
        Assert.True(AcceptsReceipt(amount: int.MaxValue));
        Assert.False(AcceptsReceipt(owner: 2));
        Assert.False(AcceptsReceipt(owner: -1));
        Assert.False(AcceptsReceipt(source: 1));
        Assert.False(AcceptsReceipt(target: 1));
        Assert.False(AcceptsReceipt(position: Vector2.UnitX));
        Assert.False(AcceptsReceipt(sourcePosition: Vector2.UnitY));
        Assert.False(AcceptsReceipt(hasSourcePosition: true));
        Assert.False(AcceptsReceipt(detail: 1));
        Assert.False(AcceptsReceipt(sequence: 1));
        Assert.False(AcceptsReceipt(element: 0));
        Assert.False(AcceptsReceipt(entityKind: ObservedEntityKind.Unit));
        Assert.False(AcceptsReceipt(sourceKind: ObservedEntityKind.Building));
        Assert.False(AcceptsReceipt(targetKind: ObservedEntityKind.Building));
        Assert.False(AcceptsReceipt(amount: 0));
        Assert.False(AcceptsReceipt(amount: 1.5f));
        Assert.False(AcceptsReceipt(amount: MathF.BitIncrement(int.MaxValue)));

        bool AcceptsReceipt(int owner = 1, int source = 0, int target = 0, Vector2 position = default, float amount = 8f,
            int detail = 0, ulong sequence = 0, int element = -1, Vector2 sourcePosition = default, bool hasSourcePosition = false,
            ObservedEntityKind entityKind = ObservedEntityKind.Core, ObservedEntityKind sourceKind = ObservedEntityKind.Unit,
            ObservedEntityKind targetKind = ObservedEntityKind.Unit)
        {
            var receipt = new SimEvent(SimEventKind.CorePickedUp, owner, source, target, position, amount, detail, sequence,
                element, sourcePosition, hasSourcePosition, entityKind: entityKind, sourceKind: sourceKind, targetKind: targetKind);
            GameplayCodec.WriteBody(body, view, new[] { new NetworkEvent(1, view.Tick, receipt) }, 1);
            return GameplayCodec.ReadBody(new NetDataReader(body.CopyData()), rules, frame, view.Tick, 1, 1);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CloserAlliedSquadOrUnitPickupKeepsTheExistingSource(bool squadWins)
    {
        var rules = InstantCollectorRules();
        var setup = new WorldSetup();
        var corePosition = new Vector2(124, 100);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(143, 100), squadWins ? corePosition : new Vector2(143, 110), cores: 100));
        setup.Units.Add(new UnitSpawn(1, "earth", squadWins ? new Vector2(124, 106) : corePosition));
        setup.Cores.Add(new CoreSpawn(corePosition, 8));
        var world = new SimulationWorld(rules, setup, 1);
        WorldView view = world.GetView(1);
        int coreId = Assert.Single(view.Cores).Id;
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 128, 100)));

        world.Step();

        Assert.Contains(view.Buildings, building => building.Kind == BuildingKind.Collector);
        Assert.Equal(squadWins, Vector2.Distance(view.Squads[0].Position, corePosition) <= rules.CorePickupRadius);
        Assert.Equal(!squadWins, Vector2.Distance(Assert.Single(view.Units).Position, corePosition) <= rules.CorePickupRadius);
        Assert.Equal(100 - rules.BuildingCost(BuildingKind.Collector) + 8, view.CoreBalance);
        AssertPickup(Assert.Single(view.Events, item => item.Kind == SimEventKind.CorePickedUp), coreId, corePosition, 1, 0);
    }

    [Fact]
    public void WireRejectsForeignUpgradeFieldsAndProductionMetadata()
    {
        var rules = InstantCollectorRules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 100), baseLevel: 2));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(143, 100), new Vector2(143, 100), cores: 100, baseLevel: 2));
        var world = new SimulationWorld(rules, setup, 1);
        WorldView observer = world.GetView(1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.Build(1, BuildingKind.Collector, 122, 100)));
        world.Step();
        var collector = Assert.Single(observer.Buildings, building => building.Kind == BuildingKind.Collector);
        var body = new NetDataWriter();
        var frame = new GameplayFrame(rules);
        GameplayCodec.WriteBody(body, observer, Array.Empty<NetworkEvent>(), 0);
        byte[] intact = body.CopyData();
        Assert.True(Reads(intact));
        // 마지막 시설 뒤에는 빈 핵·영역·전향 후보·사건 개수만 있다.
        int productionOffset = intact.Length - 16 - sizeof(long);
        byte[] forged = (byte[])intact.Clone();
        forged[productionOffset - 2] = (byte)FacilityUpgradeBranch.A;
        forged[productionOffset - 1] = 1;
        Assert.False(Reads(forged));
        forged = (byte[])intact.Clone();
        BitConverter.GetBytes(observer.Tick + 1).CopyTo(forged, productionOffset);
        Assert.False(Reads(forged));

        Assert.True(AcceptsReceipt());
        Assert.False(AcceptsReceipt(owner: 2));
        Assert.False(AcceptsReceipt(collectorId: collector.Id));
        Assert.False(AcceptsReceipt(collectorId: Assert.Single(observer.Buildings, item => item.OwnerId == 1).Id));
        Assert.False(AcceptsReceipt(kind: ObservedEntityKind.Core));
        Assert.False(AcceptsReceipt(position: Vector2.UnitX));
        Assert.False(AcceptsReceipt(amount: 0));
        Assert.False(AcceptsReceipt(amount: 1.5f));

        bool Reads(byte[] bytes) => GameplayCodec.ReadBody(new NetDataReader(bytes), rules, frame, observer.Tick, 1, 1);
        bool AcceptsReceipt(int owner = 1, int collectorId = 0, ObservedEntityKind kind = ObservedEntityKind.Building,
            Vector2 position = default, float amount = 1f)
        {
            var receipt = new SimEvent(SimEventKind.CoresProduced, owner, 0, 0, position, amount, entityId: collectorId, entityKind: kind);
            GameplayCodec.WriteBody(body, observer, new[] { new NetworkEvent(1, observer.Tick, receipt) }, 1);
            return Reads(body.CopyData());
        }
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

        Pump(() => first.Gameplay.State.Tick >= pending.ConstructionEndTick, host, first, second);
        Assert.True(rules.TryGetFacilityUpgrade(BuildingKind.NatureForge, FacilityUpgradeBranch.B, 1, out FacilityUpgrade upgrade));
        long before = first.Gameplay.State.CoreBalance;
        first.SendUpgradeFacility(3, pending.Id, FacilityUpgradeBranch.B, 1);
        Pump(() => applied.Count == 3 && second.Gameplay.State.Tick >= first.Gameplay.State.Tick, host, first, second);
        Assert.Equal(3UL, applied[^1]);
        var upgraded = Assert.Single(first.Gameplay.State.Buildings.ToArray(), building => building.Id == pending.Id);
        Assert.Equal(FacilityUpgradeBranch.B, upgraded.UpgradeBranch);
        Assert.Equal(1, upgraded.UpgradeRank);
        Assert.Equal(before - upgrade.Cost, first.Gameplay.State.CoreBalance);
        Assert.Empty(other);
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

    private static GameRules InstantCollectorRules()
    {
        var json = GameRulesTests.Data().Root.DeepClone();
        json["buildings"]!["collector"]!["buildSeconds"] = 0;
        return new GameRules(new GameDataDocument(json.ToString()));
    }

    private static void AssertPickup(SimEvent pickup, int coreId, Vector2 position, int ownerId, int collectorId)
    {
        Assert.Equal(ownerId, pickup.OwnerId);
        Assert.Equal(collectorId, pickup.SourceId);
        Assert.Equal(collectorId > 0 ? ObservedEntityKind.Building : ObservedEntityKind.Unit, pickup.SourceKind);
        Assert.False(pickup.HasSourcePosition);
        Assert.Equal(Vector2.Zero, pickup.SourcePosition);
        Assert.Equal(position, pickup.Position);
        Assert.Equal(8f, pickup.Amount);
        Assert.Equal(ObservedEntityKind.Core, pickup.EntityKind);
        Assert.Equal(coreId, pickup.EntityId);
    }

    private static GameplayFrame RoundTrip(WorldView view, GameRules rules, GameplayReplicationState? gameplay = null,
        int wireBudget = GameplayCodec.MaxBytes)
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
        gameplay ??= new GameplayReplicationState(rules);
        gameplay.Capture(view);
        gameplay.Prepare(view, fog.Revision, wireBudget);
        int totalWireBytes = 0;
        for (int part = 0; part < gameplay.ChunkCount; part++)
        {
            gameplay.WriteChunk(writer, part);
            totalWireBytes += writer.Length + OnlineFrame.HeaderBytes;
            replica.TryAccept(new NetDataReader(writer.CopyData()), 0);
        }
        Assert.InRange(totalWireBytes, 1, wireBudget);
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
