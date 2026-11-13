using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>생존 개체와 양성 예약이 같은 전역 용량을 초과하지 않는지 확인한다.</summary>
public sealed class UnitReservationTests
{
    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void EcologyCapacityFailurePreservesTheExistingWorld()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, Neutrals(rules, Protocol.MaxUnits - 1), 1);
        Assert.True(Reserve(world, 1));
        JObject before = JObject.FromObject(world.CaptureCheckpoint());
        Assert.Throws<ArgumentException>(() => world.EnableEcology(rules.RoomMinBaseDistance));
        Assert.True(JToken.DeepEquals(before, JObject.FromObject(world.CaptureCheckpoint())));
        Assert.Equal(1, Reservations(world));
        Assert.False(world.TryChooseSpawn(out _, out _));
    }

    [Fact]
    public void GlobalCapacityIsSharedAcrossSpawnTrainingAndRecovery()
    {
        var data = GameRulesTests.Data().Root;
        data["start"]!["unitCount"] = data["start"]!["fieldUnitCount"] = 1;
        data["start"]!["garrisonUnitCount"] = 0;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = Neutrals(rules, Protocol.MaxUnits - 1);
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220), 4, 100, true));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.True(Reserve(world, 1));
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.Step();
        Assert.Equal(0, world.GetView(1).ReservedPopulation);
        Assert.False(world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(620))));
        Release(world, 1);
        world.TryEnqueue(1, SimCommand.Train(2, "water"));
        world.Step();
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
        Assert.False(Reserve(world, 1));
        AssertBudget(world);
        Assert.True(world.BeginGhost(1));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(620))));
        Assert.False(Reserve(world, 1));
        AssertBudget(world);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidReservationCountsNeverChangeCapacity(int count)
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), new WorldSetup(), 1);
        Assert.IsType<ArgumentOutOfRangeException>(Assert.Throws<TargetInvocationException>(() => Reserve(world, count)).InnerException);
        Assert.True(Reserve(world, 1));
        Assert.IsType<ArgumentOutOfRangeException>(Assert.Throws<TargetInvocationException>(() => Release(world, count)).InnerException);
        Assert.IsType<ArgumentOutOfRangeException>(Assert.Throws<TargetInvocationException>(() => Release(world, 2)).InnerException);
        Release(world, 1);
        Assert.IsType<ArgumentOutOfRangeException>(Assert.Throws<TargetInvocationException>(() => Release(world, 1)).InnerException);
        AssertBudget(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeutralVacanciesReserveOnceAndSurviveRestore(bool converted)
    {
        var data = GameRulesTests.Data().Root;
        data["neutral"]!["conversionChance"] = converted ? 1 : 0;
        data["neutral"]!["respawnSeconds"] = 0.15;
        var rules = new GameRules(new GameDataDocument(data.ToString())).ForRoom(2);
        var world = EcologyWithEmptyPlayer(rules);
        KillFirstEcologyNeutral(world, converted ? 1 : -1);
        world.Step();
        Assert.Equal(1, Reservations(world));
        Assert.Equal(converted ? 1 : 0, world.GetView(1).Population);
        AssertBudget(world);
        var migrated = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        Assert.Equal(1, Reservations(migrated));
        var disk = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        Assert.Equal(1, Reservations(disk));
        world.Step();
        Assert.Equal(1, Reservations(world));
        AssertBudget(world);
        for (int tick = 0; tick < rules.TickRate * 10 && Reservations(world) != 0; tick++) world.Step();
        Assert.Equal(0, Reservations(world));
        AssertBudget(world);
    }

    [Fact]
    public void ConvertedNeutralWaitsWithoutReservationAtCapacityThenRetries()
    {
        var data = GameRulesTests.Data().Root;
        data["neutral"]!["conversionChance"] = 1;
        var rules = new GameRules(new GameDataDocument(data.ToString())).ForRoom(2);
        var world = EcologyWithEmptyPlayer(rules);
        int available = Protocol.MaxUnits - JObject.FromObject(world.CaptureCheckpoint())["Units"]!.Count();
        Assert.True(Reserve(world, available));
        KillFirstEcologyNeutral(world, 1);
        world.Step();
        Assert.Equal(1, world.GetView(1).Population);
        var slot = JObject.FromObject(world.CaptureCheckpoint())["Ecology"]!["Groups"]!.SelectMany(group => group["Slots"]!).Single(item => (int)item["Unit"]! == 0);
        Assert.False((bool)slot["Reserved"]!);
        AssertBudget(world);
        Release(world, 1);
        world.Step();
        Assert.Equal(available, Reservations(world));
        world.Step();
        Assert.Equal(available, Reservations(world));
        AssertBudget(world);
    }

    [Fact]
    public void FifteenPlayersCanReserveTheirThreeHundredthUnitWhileNeutralsRespawn()
    {
        var data = GameRulesTests.Data().Root;
        data["neutral"]!["respawnSeconds"] = 0.15;
        var rules = new GameRules(new GameDataDocument(data.ToString())).ForRoom(15);
        var setup = new WorldSetup();
        int owner = 0;
        for (int row = 1; row <= 4; row++)
        {
            int columns = row == 4 ? 3 : 4;
            for (int column = 1; column <= columns; column++)
            {
                var home = new Vector2(column * rules.MapSize / (columns + 1), row * rules.MapSize / 5);
                Vector2 legion = home + new Vector2(0, 45);
                setup.Players.Add(new PlayerSpawn(++owner, "water", home, legion, 9, 100, natureForge: true));
                for (int unit = 0; unit < 299; unit++)
                    setup.Units.Add(new UnitSpawn(owner, "water", legion + new Vector2(unit % 18 - 9, unit / 18 - 8) * rules.SlotSpacing));
            }
        }

        var world = new SimulationWorld(rules, setup, 1);
        world.EnableEcology(rules.RoomMinBaseDistance);
        KillFirstEcologyNeutral(world, -1);
        foreach (PlayerSpawn player in setup.Players)
        {
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(player.Id, SimCommand.Train(1, "water", 1, 1)));
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(player.Id, SimCommand.Train(2, "water", 1, 1)));
        }

        world.Step();
        foreach (PlayerSpawn player in setup.Players)
        {
            WorldView view = world.GetView(player.Id);
            Assert.Equal(300, view.Population + view.ReservedPopulation);
            Assert.Contains(view.Events, item => item.Sequence == 2 && item.Kind == SimEventKind.CommandRejected && item.Detail == (int)CommandFailure.PopulationCap);
        }
        Assert.Equal(16, Reservations(world));
        for (int tick = 0; tick < 10 && world.Metrics.EcologyRespawns == 0; tick++) world.Step();
        Assert.Equal(1, world.Metrics.EcologyRespawns);
        Assert.Equal(15, Reservations(world));
        AssertBudget(world);
    }

    private static SimulationWorld EcologyWithEmptyPlayer(GameRules rules)
    {
        var setup = new WorldSetup();
        var home = new Vector2(rules.MapSize / 3, rules.MapSize / 2);
        setup.Players.Add(new PlayerSpawn(1, "water", home, home + new Vector2(0, 20), 4));
        var world = new SimulationWorld(rules, setup, 1);
        world.EnableEcology(rules.RoomMinBaseDistance);
        return world;
    }

    private static object State(SimulationWorld world) => typeof(SimulationWorld).GetField("state", Internal)!.GetValue(world)!;
    private static bool Reserve(SimulationWorld world, int count) => (bool)State(world).GetType().GetMethod("TryReserveUnitSlots", Internal)!.Invoke(State(world), new object[] { count })!;
    private static void Release(SimulationWorld world, int count) => State(world).GetType().GetMethod("ReleaseUnitSlots", Internal)!.Invoke(State(world), new object[] { count });
    private static int Reservations(SimulationWorld world) => (int)State(world).GetType().GetProperty("ReservedUnitSlots", Internal)!.GetValue(State(world))!;
    private static void AssertBudget(SimulationWorld world)
    {
        int live = JObject.FromObject(world.CaptureCheckpoint())["Units"]!.Count();
        Assert.True(live + Reservations(world) <= Protocol.MaxUnits);
    }

    private static void KillFirstEcologyNeutral(SimulationWorld world, int owner)
    {
        var units = (System.Collections.IEnumerable)State(world).GetType().GetField("Units", Internal)!.GetValue(State(world))!;
        object unit = units.Cast<object>().First(item => (bool)item.GetType().GetField("EcologyNeutral", Internal)!.GetValue(item)!);
        unit.GetType().GetField("Dead", Internal)!.SetValue(unit, true);
        unit.GetType().GetField("Health", Internal)!.SetValue(unit, 0f);
        unit.GetType().GetField("KillerOwner", Internal)!.SetValue(unit, owner);
    }

    [Fact]
    public void TrainingReservesTheGarrisonBudgetBeforeCharging()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(cores: 1000, level: 1), 1);
        int cap = rules.GarrisonCap(1);
        for (int index = 1; index <= cap + 1; index++)
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train((ulong)index, "water")));
        world.Step();
        Assert.Equal(cap, world.GetView(1).ReservedPopulation);
        Assert.Equal(1000 - cap * rules.TrainingCost(rules.ElementIndex("water"), rules.ElementIndex("water")), world.GetView(1).CoreBalance);
        Assert.Contains(world.GetView(1).Events, item => item.Sequence == (ulong)cap + 1 && item.Kind == SimEventKind.CommandRejected);
        Assert.Equal(cap, Reservations(SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState())));
        var disk = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        Assert.Equal(cap, Reservations(disk));
        Assert.True(disk.TryResumeGhost(1));
        while (world.GetView(1).IsTraining) world.Step();
        world.TryEnqueue(1, SimCommand.Train((ulong)cap + 2, "water"));
        world.Step();
        Assert.Equal(0, world.GetView(1).ReservedPopulation);
        Assert.Equal(cap, world.GetView(1).Population);
    }

    [Fact]
    public void RallyReleasesTheGarrisonAssignmentForNewTraining()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup(cores: 100, level: 1);
        for (int index = 0; index < rules.GarrisonCap(1); index++)
            setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(200 + index * 2, 215), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.Step();
        Assert.Equal(0, world.GetView(1).ReservedPopulation);
        world.TryEnqueue(1, SimCommand.Rally(2));
        world.TryEnqueue(1, SimCommand.Train(3, "water"));
        world.Step();
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefeatOrForgeLossReleasesEveryOwnedOrderOnlyOnce(bool loseForge)
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.TryEnqueue(1, SimCommand.Train(2, "water"));
        world.Step();
        Assert.Equal(2, Reservations(world));
        if (loseForge)
        {
            var buildings = (System.Collections.IEnumerable)State(world).GetType().GetField("Buildings", Internal)!.GetValue(State(world))!;
            var forge = buildings.Cast<object>().Single(item => (BuildingKind)item.GetType().GetField("Kind", Internal)!.GetValue(item)! == BuildingKind.NatureForge);
            forge.GetType().GetField("Health", Internal)!.SetValue(forge, 0f);
        }
        else
        {
            var players = (System.Collections.IEnumerable)State(world).GetType().GetField("Players", Internal)!.GetValue(State(world))!;
            object player = Assert.Single(players.Cast<object>());
            player.GetType().GetField("BaseDestroyed", Internal)!.SetValue(player, true);
        }
        world.Step();
        Assert.Equal(0, Reservations(world));
        world.Step();
        Assert.Equal(0, Reservations(world));
        AssertBudget(world);
    }

    [Fact]
    public void RestoreRebuildsTrainingReservationsAndRejectsAnOversubscribedSave()
    {
        var rules = GameRulesTests.Rules();
        var setup = Neutrals(rules, Protocol.MaxUnits - 1);
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220), 4, 100, true));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.Step();
        JObject json = JObject.FromObject(world.CaptureCheckpoint());
        json["ReservedUnitSlots"] = 0;
        var restored = SimulationWorld.RestoreCheckpoint(rules, json.ToObject<WorldCheckpoint>()!);
        Assert.Equal(1, Reservations(restored));
        AssertBudget(restored);
        var player = json["Players"]![0]!;
        JToken extra = player["Training"]![0]!.DeepClone();
        extra["Id"] = 2;
        ((JArray)player["Training"]!).Add(extra);
        player["NextTrainingOrderId"] = 3;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, json.ToObject<WorldCheckpoint>()!));
        AssertBudget(world);
    }

    [Fact]
    public void InitialPopulationCannotExceedTheGlobalUnitBudget()
    {
        var rules = GameRulesTests.Rules();
        Assert.Throws<ArgumentException>(() => new SimulationWorld(rules, Neutrals(rules, Protocol.MaxUnits + 1), 1));
    }

    [Fact]
    public void JoiningCannotPartiallyConsumeTheLastGlobalSlots()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, Neutrals(rules, Protocol.MaxUnits - rules.StartUnitCount + 1), 1);
        Assert.False(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220))));
        Assert.Equal(0, world.PlayerCount);
        Assert.Equal(0, world.CaptureCheckpoint().MaxPlayerId + 1);
    }

    [Fact]
    public void GlobalCapacityIsSharedAcrossSpawnAndTraining()
    {
        var rules = GameRulesTests.Rules();
        var setup = Neutrals(rules, Protocol.MaxUnits - 1);
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220), 4, 100, true));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.TryEnqueue(1, SimCommand.Train(2, "water"));
        world.Step();
        var view = world.GetView(1);
        Assert.Equal(1, view.ReservedPopulation);
        Assert.Equal(100 - rules.TrainingCost(view.MainElement, view.MainElement), view.CoreBalance);
        Assert.Contains(view.Events, item => item.Kind == SimEventKind.CommandRejected && item.Sequence == 2);
        Assert.False(world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(600), new Vector2(620))));
        Assert.Equal(1, world.PlayerCount);
    }

    [Fact]
    public void WaitingCancellationReleasesExactlyOneReservation()
    {
        var rules = GameRulesTests.Rules();
        var setup = Neutrals(rules, Protocol.MaxUnits - 2);
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220), 4, 100, true));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.TryEnqueue(1, SimCommand.Train(2, "water"));
        world.Step();
        var view = world.GetView(1);
        ulong order = Assert.Single(view.TrainingQueue).Id;
        world.TryEnqueue(1, SimCommand.CancelTraining(3, order));
        world.Step();
        Assert.Equal(1, view.ReservedPopulation);
        world.TryEnqueue(1, SimCommand.Train(4, "water"));
        world.Step();
        Assert.Equal(2, view.ReservedPopulation);
        long balance = view.CoreBalance;
        world.TryEnqueue(1, SimCommand.CancelTraining(5, order));
        world.Step();
        Assert.Equal(2, view.ReservedPopulation);
        Assert.Equal(balance, view.CoreBalance);
        Assert.Contains(view.Events, item => item.Sequence == 5 && item.Detail == (int)CommandFailure.TrainingNotWaiting);
    }

    private static WorldSetup Neutrals(GameRules rules, int count)
    {
        var setup = new WorldSetup();
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        float spacing = rules.MapSize / (columns + 1);
        for (int index = 0; index < count; index++)
            setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(index % columns + 1, index / columns + 1) * spacing));
        return setup;
    }
}
