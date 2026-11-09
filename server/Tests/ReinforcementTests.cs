using System.Numerics;
using System.Collections;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>묶음 양성의 원자 예약과 본진에서 출발하는 실제 증원을 확인한다.</summary>
public sealed class ReinforcementTests
{
    [Fact]
    public void BatchTrainingIsAtomicAndSequential()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var view = world.GetView(1);
        var command = SimCommand.Train(1, "water", 4, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, command));
        world.Step();
        Assert.Equal(68, view.CoreBalance);
        Assert.Equal(4, view.ReservedPopulation);
        Assert.Equal(3, view.TrainingQueue.Count);
        Assert.Equal(1, view.TrainingTargetSquad);
        Assert.All(view.TrainingQueue, order => Assert.Equal(1, order.TargetSquad));
        AssertCounts(view);
        long firstDue = view.TrainingEndTick;
        for (int count = 1; count <= 4; count++)
        {
            long due = firstDue + (count - 1) * 6 * rules.TickRate;
            while (view.Tick < due - 1) world.Step();
            Assert.Equal(count - 1, view.Population);
            world.Step();
            Assert.Equal(count, view.Population);
            Assert.Equal(4 - count, view.ReservedPopulation);
            AssertCounts(view);
        }
        Assert.False(view.IsTraining);
        Assert.Equal(0, view.TrainingTargetSquad);
    }

    [Fact]
    public void LegacyTrainingDefaultsToOneGuardAndDefaultStartsTwelve()
    {
        var command = SimCommand.Train(1, "water");
        Assert.Equal(1, command.Count);
        Assert.Equal(4, command.TargetSquad);
        var rules = GameRulesTests.Rules();
        Assert.Equal(12, rules.StartUnitCount);
        Assert.Equal(new[] { 30, 50, 75, 100 }, Enumerable.Range(1, 4).Select(rules.PopulationCap));
    }

    [Theory]
    [InlineData("cores")]
    [InlineData("population")]
    [InlineData("guard")]
    [InlineData("global")]
    [InlineData("ids")]
    public void FailedBatchDoesNotPartiallyChargeReserveOrAllocateIds(string boundary)
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup(cores: boundary == "cores" ? 31 : 100, level: 1);
        if (boundary == "population")
            for (int i = 0; i < rules.PopulationCap(1) - 3; i++) setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(400 + i * 2, 400)));
        if (boundary == "guard")
            for (int i = 0; i < 5; i++) setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(200 + i * 2, 215), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        object state = State(world), player = ((IList)Field(state, "Players"))[0]!;
        if (boundary == "global") state.GetType().GetMethod("TryReserveUnitSlots", Internal)!.Invoke(state, new object[] { Protocol.MaxUnits - 3 - setup.Units.Count });
        if (boundary == "ids") player.GetType().GetField("NextTrainingOrderId", Internal)!.SetValue(player, ulong.MaxValue - 2);
        ulong next = (ulong)Field(player, "NextTrainingOrderId");
        int reserved = Reservations(world);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 4, boundary == "guard" ? (byte)4 : (byte)1));
        world.Step();
        Assert.Equal(boundary == "cores" ? 31 : 100, world.GetView(1).CoreBalance);
        Assert.Equal(0, world.GetView(1).ReservedPopulation);
        Assert.Equal(next, (ulong)Field(player, "NextTrainingOrderId"));
        Assert.Equal(reserved, Reservations(world));
        Assert.Contains(world.GetView(1).Events, e => e.Kind == SimEventKind.CommandRejected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 5)]
    public void InvalidBatchDoesNotConsumeSequence(int count, byte target)
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(), 1);
        Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.Train(1, "water", count, target)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1)));
    }

    [Fact]
    public void BatchOrderIdsAreIndependentFromCommandSequence()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 4, 1));
        world.TryEnqueue(1, SimCommand.Train(2, "water", 4, 2));
        world.Step();
        Assert.Equal(Enumerable.Range(2, 7).Select(i => (ulong)i), world.GetView(1).TrainingQueue.Select(o => o.Id));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.CancelTraining(3, 8)));
        world.Step();
        Assert.Equal(7, world.GetView(1).ReservedPopulation);
        Assert.Equal(44, world.GetView(1).CoreBalance);
        AssertCounts(world.GetView(1));
        world.TryEnqueue(1, SimCommand.CancelTraining(4, 1));
        world.Step();
        Assert.Equal(7, world.GetView(1).ReservedPopulation);
        Assert.Contains(world.GetView(1).Events, e => e.Detail == (int)CommandFailure.TrainingNotWaiting);
    }

    [Fact]
    public void ReinforcementCountsOnceAndFightsWhileTravelling()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Units.Add(new UnitSpawn(1, "wind", new Vector2(400), garrison: false));
        setup.Players[0] = new PlayerSpawn(1, "water", new Vector2(200), new Vector2(400), 4, 100, true);
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(700), new Vector2(650), 4));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(650), garrison: false));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1));
        world.Step();
        while (world.GetView(1).IsTraining) world.Step();
        var view = world.GetView(1);
        UnitView newborn = Assert.Single(view.Units, u => u.OwnerId == 1 && u.Element == rules.ElementIndex("water"));
        Assert.True(newborn.IsReinforcing);
        Assert.False(newborn.Garrison);
        Assert.Equal(0, newborn.SquadId);
        Assert.Equal(1, newborn.DestinationSquadId);
        Assert.InRange(Vector2.Distance(newborn.Position, view.BasePosition), rules.BaseRadius, rules.GarrisonRadius);
        Assert.Equal(2, view.Population);
        AssertCounts(view);
        SetUnit(world, 2, "Position", newborn.Position + new Vector2(2, 0));
        world.Step();
        Assert.Contains(view.Events, e => e.Kind == SimEventKind.Attack && e.SourceId == newborn.Id);
        Assert.True(view.Units.Single(u => u.Id == newborn.Id).IsReinforcing);
        Assert.Equal(2, view.Population);
        SetUnit(world, 2, "Position", new Vector2(650));
        for (int i = 0; i < 1000 && view.Squads[0].ReinforcingCount > 0; i++) world.Step();
        Assert.Equal(0, view.Squads[0].ReinforcingCount);
        Assert.Equal(2, view.Squads[0].JoinedCount);
        AssertCounts(view);
    }

    [Fact]
    public void TrainingFailureReleasesEachReservationOnce()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 4, 1));
        world.Step();
        world.TryEnqueue(1, SimCommand.CancelTraining(2, 4));
        world.Step();
        Assert.Equal(3, Reservations(world));
        Assert.Equal(76, world.GetView(1).CoreBalance);
        object forge = ((IEnumerable)Field(State(world), "Buildings")).Cast<object>().Single(b => (BuildingKind)Field(b, "Kind") == BuildingKind.NatureForge);
        forge.GetType().GetField("Health", Internal)!.SetValue(forge, 0f);
        world.Step();
        Assert.Equal(0, Reservations(world));
        Assert.Equal(92, world.GetView(1).CoreBalance);
        world.Step();
        Assert.Equal(92, world.GetView(1).CoreBalance);
        Assert.False(world.GetView(1).IsTraining);
        AssertCounts(world.GetView(1));
    }

    [Fact]
    public void BatchTargetsAndPendingCommandsSurviveDiskPauseAndExactMigration()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 2, 2));
        world.Step();
        for (int i = 0; i < 17; i++) world.Step();
        long remaining = world.GetView(1).TrainingEndTick - world.GetView(1).Tick;
        var disk = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        for (int i = 0; i < 20; i++) disk.Step();
        Assert.True(disk.TryResumeGhost(1));
        Assert.Equal(remaining, disk.GetView(1).TrainingEndTick - disk.GetView(1).Tick);
        Assert.Equal(2, disk.GetView(1).TrainingTargetSquad);
        Assert.Equal(2, Assert.Single(disk.GetView(1).TrainingQueue).TargetSquad);
        world.TryEnqueue(1, SimCommand.Train(2, "water", 2, 3));
        var migrated = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        world.Step(); migrated.Step();
        Assert.Equal(world.GetView(1).TrainingEndTick, migrated.GetView(1).TrainingEndTick);
        Assert.Equal(new byte[] { 2, 3, 3 }, migrated.GetView(1).TrainingQueue.Select(o => o.TargetSquad));
        AssertCounts(migrated.GetView(1));
        while (world.GetView(1).IsTraining) world.Step();
        var travelling = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        Assert.Equal(world.GetView(1).Population, travelling.GetView(1).Population);
        AssertCounts(travelling.GetView(1));
    }

    [Fact]
    public void NewPlayersSplitTwelveFreeUnitsAndKeepAuthoredSetups()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220))));
        var view = world.GetView(1);
        Assert.Equal(8, view.Squads[0].JoinedCount);
        Assert.Equal(4, view.Squads[3].JoinedCount);
        Assert.Equal(100, view.RecallArrivalSlots.Count);
        Assert.All(view.Units, u => Assert.InRange(Vector2.Distance(u.Position, u.Garrison ? view.BasePosition : view.LegionPosition), 0, rules.GarrisonRadius));
        JObject snapshot = JObject.FromObject(world.CaptureCheckpoint());
        Assert.All(snapshot["Units"]!, u => Assert.True((bool)u["FreeOrigin"]!));
        Assert.True((bool)snapshot["Buildings"]![0]!["FreeOrigin"]!);
        AssertCounts(view);
        var authored = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        Assert.Equal(0, authored.GetView(1).Population);
    }

    [Fact]
    public void TravellingDeathKeepsWaitingReservationAndDoesNotRefund()
    {
        var rules = GameRulesTests.Rules();
        var world = DistantWorld(rules);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 2, 1));
        world.Step();
        while (world.GetView(1).TrainedCount == 0) world.Step();
        var view = world.GetView(1);
        int newborn = Assert.Single(view.Units, u => u.IsReinforcing).Id;
        SetUnit(world, newborn, "Health", 0f);
        SetUnit(world, newborn, "Dead", true);
        world.Step();
        Assert.Equal(1, view.Population);
        Assert.Equal(1, view.ReservedPopulation);
        Assert.Equal(1, Reservations(world));
        Assert.Equal(84, view.CoreBalance);
        while (view.IsTraining) world.Step();
        Assert.Equal(2, view.Population);
        Assert.Equal(0, Reservations(world));
        Assert.Equal(84 + rules.DropAmount(rules.ElementIndex("water")), view.CoreBalance);
        Assert.Contains(view.Events, e => e.Kind == SimEventKind.CorePickedUp);
        AssertCounts(view);
    }

    [Fact]
    public void TravellersRoundTripWithoutGarrisonAndFollowRecalledDestination()
    {
        var rules = GameRulesTests.Rules();
        var world = DistantWorld(rules);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1));
        world.Step();
        while (world.GetView(1).IsTraining) world.Step();
        var view = world.GetView(1);
        UnitView traveller = Assert.Single(view.Units, u => u.IsReinforcing);
        Assert.False(traveller.Garrison);
        var migration = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        Assert.False(Assert.Single(migration.GetView(1).Units, u => u.IsReinforcing).Garrison);
        var disk = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        Assert.True(disk.TryResumeGhost(1));
        Assert.Equal(traveller.Id, Assert.Single(disk.GetView(1).Units, u => u.IsReinforcing).Id);
        world.TryEnqueue(1, SimCommand.Recall(2, 1));
        world.Step();
        long due = view.RecallEndTick;
        while (view.Tick < due)
        {
            Vector2 before = view.Units.Single(u => u.Id == traveller.Id).Position;
            world.Step();
            Assert.InRange(Vector2.Distance(before, view.Units.Single(u => u.Id == traveller.Id).Position), 0,
                rules.LegionSpeed * rules.RallySpeedMultiplier / rules.TickRate + 0.001f);
        }
        Assert.InRange(Vector2.Distance(view.Squads[0].Position, view.BasePosition), 0, rules.GarrisonRadius);
        while (view.Squads[0].ReinforcingCount > 0) world.Step();
        Assert.Equal(2, view.Squads[0].JoinedCount);
        AssertCounts(view);
    }

    [Fact]
    public void EmptyDestinationKeepsItsIdentityWhenOtherSquadSurvives()
    {
        var rules = GameRulesTests.Rules();
        var world = DistantWorld(rules);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 2));
        world.Step();
        while (world.GetView(1).IsTraining) world.Step();
        var view = world.GetView(1);
        var newborn = Assert.Single(view.Units, u => u.IsReinforcing);
        Assert.Equal(2, newborn.DestinationSquadId);
        Assert.InRange(Vector2.Distance(view.Squads[1].Position, view.BasePosition), rules.BaseRadius, rules.GarrisonRadius);
        while (view.Squads[1].ReinforcingCount > 0) world.Step();
        Assert.Equal(1, view.Squads[0].JoinedCount);
        Assert.Equal(1, view.Squads[1].JoinedCount);
        AssertCounts(view);
    }

    [Fact]
    public void OnlyGuardTargetReservesGuardCapacityAndTransferUsesThatReservation()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup(level: 1);
        for (int i = 0; i < 4; i++) setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(200 + i * 2, 215), garrison: true));
        setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(200, 210)));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 4, 2));
        world.TryEnqueue(1, SimCommand.Train(2, "water", 3, 4));
        world.TryEnqueue(1, SimCommand.Transfer(3, 1, 4, 1));
        world.Step();
        var view = world.GetView(1);
        Assert.Equal(7, view.ReservedPopulation);
        Assert.Equal(5, view.Squads[3].JoinedCount);
        Assert.Equal(3, view.Squads[3].ReservedPopulation);
        Assert.Equal(4, view.Squads[1].ReservedPopulation);
        world.TryEnqueue(1, SimCommand.Train(4, "water", 1, 4));
        world.Step();
        Assert.Equal(7, view.ReservedPopulation);
        Assert.Contains(view.Events, e => e.Sequence == 4 && e.Detail == (int)CommandFailure.PopulationCap);
        AssertCounts(view);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void StoredAndPendingTrainingRejectInvalidDestinations(byte target)
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 2, 1));
        world.Step();
        JObject saved = JObject.FromObject(world.CaptureCheckpoint());
        saved["Players"]![0]!["Training"]![0]!["TargetSquad"] = target;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, saved.ToObject<WorldCheckpoint>()!));
        world.TryEnqueue(1, SimCommand.Train(2, "water", 2, 2));
        JObject pending = JObject.FromObject(world.CaptureMigrationState());
        pending["Commands"]![0]!["TargetSquad"] = target;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(rules, pending.ToObject<MigrationWorldState>()!));
    }

    [Fact]
    public void SchemaThreeRequiresTrainingDestinationAndRejectsPreviousDataHash()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1));
        world.Step();
        JObject saved = JObject.FromObject(world.CaptureCheckpoint());
        ((JObject)saved["Players"]![0]!["Training"]![0]!).Remove("TargetSquad");
        Assert.Throws<JsonSerializationException>(() => saved.ToObject<WorldCheckpoint>());
        saved = JObject.FromObject(world.CaptureCheckpoint());
        saved["DataHash"] = new string('0', 64);
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, saved.ToObject<WorldCheckpoint>()!));
    }

    [Fact]
    public void EliminatedDestinationResetsAtBaseWithoutReassigningTraveller()
    {
        var rules = GameRulesTests.Rules();
        var world = DistantWorld(rules);
        world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1));
        world.Step();
        while (world.GetView(1).IsTraining) world.Step();
        SetUnit(world, 1, "Health", 0f);
        SetUnit(world, 1, "Dead", true);
        world.Step();
        world.Step();
        var view = world.GetView(1);
        Assert.InRange(Vector2.Distance(view.Squads[0].Position, view.BasePosition), rules.BaseRadius, rules.GarrisonRadius);
        UnitView unit = Assert.Single(view.Units, u => u.OwnerId == 1);
        Assert.Equal(unit.IsReinforcing ? (byte)0 : (byte)1, unit.SquadId);
        Assert.Equal(unit.IsReinforcing ? (byte)1 : (byte)0, unit.DestinationSquadId);
        for (int i = 0; i < 100 && view.Squads[0].ReinforcingCount > 0; i++) world.Step();
        Assert.Equal(1, view.Squads[0].JoinedCount);
        AssertCounts(view);
    }

    private static SimulationWorld DistantWorld(GameRules rules)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(400), 4, 100, true));
        setup.Units.Add(new UnitSpawn(1, "wind", new Vector2(400)));
        return new SimulationWorld(rules, setup, 1);
    }

    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void SpawnSafetyChecksStartingGuardsAsWellAsFieldFormation()
    {
        var rules = GameRulesTests.Rules();
        Vector2 home = new Vector2(200), legion = new Vector2(200, 260);
        var preview = new SimulationWorld(rules, new WorldSetup(), 1);
        Assert.True(preview.TryAddPlayer(new PlayerSpawn(1, "water", home, legion)));
        Vector2 guard = preview.GetView(1).Units.First(u => u.Garrison).Position;
        Vector2 enemy = guard + Vector2.Normalize(guard - home) * (rules.SpawnMinEnemyUnitDistance - 0.1f);
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(700), enemy));
        setup.Units.Add(new UnitSpawn(2, "earth", enemy));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.True(Vector2.Distance(enemy, home) > rules.SpawnMinEnemyUnitDistance);
        Assert.False(world.IsSafePlayerSpawn(home, legion, rules.RoomMinBaseDistance));
    }
    private static object Field(object value, string name) => value.GetType().GetField(name, Internal)!.GetValue(value)!;
    private static object State(SimulationWorld world) => Field(world, "state");
    private static int Reservations(SimulationWorld world) => (int)State(world).GetType().GetProperty("ReservedUnitSlots", Internal)!.GetValue(State(world))!;
    private static void SetUnit(SimulationWorld world, int id, string name, object value)
    {
        object unit = ((IEnumerable)Field(State(world), "Units")).Cast<object>().Single(u => (int)Field(u, "Id") == id);
        unit.GetType().GetField(name, Internal)!.SetValue(unit, value);
    }
    private static void AssertCounts(WorldView view)
    {
        Assert.Equal(view.ReservedPopulation, view.Squads.Sum(s => s.ReservedPopulation));
        Assert.Equal(view.Population, view.Squads.Sum(s => s.JoinedCount + s.ReinforcingCount));
    }
}
