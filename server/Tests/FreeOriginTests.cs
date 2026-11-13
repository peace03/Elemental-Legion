using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>신규 무료 병력과 본진을 반복 입장 보상으로 수확할 수 없어야 한다.</summary>
public sealed class FreeOriginTests
{
    [Fact]
    public void StartingAssetsRemainFreeAcrossDiskAndHostMigration()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220))));
        AssertFree(world);
        var disk = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        AssertFree(disk);
        Assert.True(disk.TryResumeGhost(1));
        var migrated = SimulationWorld.RestoreMigrationState(rules, disk.CaptureMigrationState());
        AssertFree(migrated);

        static void AssertFree(SimulationWorld value)
        {
            JObject state = JObject.FromObject(value.CaptureCheckpoint());
            Assert.All(state["Units"]!, unit => Assert.True((bool?)unit["FreeOrigin"] ?? false));
            Assert.True((bool?)Assert.Single(state["Buildings"]!)["FreeOrigin"] ?? false);
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    public void StartingCombatDeathsResolveTheRewardOnTheDeathTick(int killerOwner, bool converts)
    {
        JObject data = GameRulesTests.Data().Root;
        data["economy"]!["conversionChance"] = converts ? 1 : 0;
        data["neutral"]!["conversionChance"] = 1;
        var water = data["elements"]!.Single(element => (string?)element["id"] == "water");
        water["hp"] = 1;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        if (killerOwner >= 0) setup.Players.Add(new PlayerSpawn(killerOwner, "fire", new Vector2(80, 102), new Vector2(80, 82)));
        setup.Units.Add(new UnitSpawn(killerOwner, "light", new Vector2(80, 82), garrison: killerOwner >= 0));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220))));
        var state = (WorldState)typeof(SimulationWorld).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        var attacker = state.Units[0];
        var victim = state.Units.First(unit => unit.OwnerId == 1);
        victim.MovementRevision = 7;
        victim.TrainingAttackBonus = .2f;
        var contact = new Vector2(80, 95);
        attacker.Position = contact;
        victim.Position = contact;
        var guard = killerOwner >= 0 ? state.PlayerById[killerOwner].Squads[3] : null;
        if (guard != null) guard.Position = guard.Destination = contact;
        CombatSystem.Damage(state, victim, victim.Health, attacker.OwnerId, attacker.Id, attacker.Element);
        world.Step();
        var view = world.GetView(1);
        Assert.True(view.Population < rules.StartUnitCount);
        JObject checkpoint = JObject.FromObject(world.CaptureCheckpoint());
        Assert.Empty(checkpoint["Candidates"]!);
        if (killerOwner < 0)
        {
            Assert.Empty(checkpoint["Cores"]!);
        }
        else if (converts)
        {
            Assert.Empty(checkpoint["Cores"]!);
            WorldView winner = world.GetView(killerOwner);
            Assert.Equal(1, winner.Journey.Total.Converted);
            Assert.Equal(0, winner.ReservedPopulation);
            var converted = state.UnitById[victim.Id];
            Assert.Equal(killerOwner, converted.OwnerId);
            Assert.Equal(4, SquadSystem.DamageSquad(converted));
            Assert.Equal(rules.Elements[victim.Element].Hp * rules.PvpRecoveryConvertedHealthRatio, converted.Health);
            Assert.Equal(8u, converted.MovementRevision);
            Assert.Equal(0, converted.TrainingAttackBonus);
            Assert.False(converted.FreeOrigin);
            Assert.Contains(winner.Events, item => item.Kind == SimEventKind.Converted && item.TargetId == victim.Id);
        }
        else Assert.Contains(state.Events, item => item.Kind == SimEventKind.CoreDropped && item.TargetId == victim.Id);
    }

    [Fact]
    public void AttackingDisconnectedStartingUnitsDoesNotCreateFreeRewards()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220))));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(300), new Vector2(320))));
        Assert.True(world.BeginGhost(1));
        var state = (WorldState)typeof(SimulationWorld).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        var victim = state.Units.First(unit => unit.OwnerId == 1);
        var attacker = state.Units.First(unit => unit.OwnerId == 2 && unit.SquadId == 1);
        CombatSystem.Damage(state, victim, victim.Health, attacker.OwnerId, attacker.Id, attacker.Element);
        world.Step();
        Assert.Empty(JObject.FromObject(world.CaptureCheckpoint())["Cores"]!);
        Assert.False(world.HasConversionCandidates);
    }

    [Fact]
    public void FullCoreFieldCoalescesDropsWithoutLosingAmountOrBreakingRestore()
    {
        GameRules rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        var point = new Vector2(300, 300);
        for (int index = 0; index < ElementalLegion.Shared.Net.GameplayCodec.MaxObjects; index++)
            setup.Cores.Add(new CoreSpawn(point, 1));
        var world = new SimulationWorld(rules, setup, 1);
        var state = (WorldState)typeof(SimulationWorld).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;

        int coreId = EconomySystem.DropCore(state, point, 5);

        Assert.Equal(setup.Cores.Count, state.Cores.Count);
        Assert.Equal(setup.Cores.Count + 5, state.Cores.Sum(core => core.Amount));
        Assert.Equal(6, state.Cores.Single(core => core.Id == coreId).Amount);
        SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
    }

    [Fact]
    public void FreeStartingBaseDoesNotDropGhostBuildingCores()
    {
        JObject data = GameRulesTests.Data().Root;
        data["ghost"]!["decaySeconds"] = 0.05;
        data["ghost"]!["reconnectWindowSeconds"] = 0.05;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220))));
        Assert.True(world.BeginGhost(1));
        world.Step();
        JObject state = JObject.FromObject(world.CaptureCheckpoint());
        Assert.Empty(state["Cores"]!);
        Assert.Equal(0, world.PlayerCount);
    }

    [Fact]
    public void ExistingAssetsWithoutTheNewFlagsRemainRegularAfterRestore()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 215)));
        var world = new SimulationWorld(rules, setup, 1);
        JObject old = JObject.FromObject(world.CaptureCheckpoint());
        foreach (JObject item in old["Units"]!.Concat(old["Buildings"]!)) item.Remove("FreeOrigin");
        var restored = SimulationWorld.RestoreCheckpoint(rules, old.ToObject<WorldCheckpoint>()!);
        JObject saved = JObject.FromObject(restored.CaptureCheckpoint());
        Assert.All(saved["Units"]!.Concat(saved["Buildings"]!), item => Assert.False((bool)item["FreeOrigin"]!));
        Assert.True(restored.TryResumeGhost(1));
        Assert.True(restored.BeginGhost(1));
        JObject migrated = JObject.FromObject(SimulationWorld.RestoreMigrationState(rules, restored.CaptureMigrationState()).CaptureCheckpoint());
        Assert.All(migrated["Units"]!.Concat(migrated["Buildings"]!), item => Assert.False((bool)item["FreeOrigin"]!));
    }

    [Fact]
    public void PaidTrainingDoesNotInheritFreeStartingOrigin()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220), 1, 100, true)));
        Assert.True(world.BeginGhost(1));
        Assert.True(world.TryResumeGhost(1));
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.Step();
        while (world.GetView(1).IsTraining) world.Step();
        var units = JObject.FromObject(world.CaptureCheckpoint())["Units"]!;
        Assert.Equal(rules.StartUnitCount, units.Count(unit => (bool)unit["FreeOrigin"]!));
        Assert.Single(units, unit => !(bool)unit["FreeOrigin"]!);
    }

    [Fact]
    public void OnlyPaidBuildingsAndRegularUnitsDropOnGhostExpiry()
    {
        var data = GameRulesTests.Data().Root;
        data["ghost"]!["decaySeconds"] = 0.05;
        data["ghost"]!["reconnectWindowSeconds"] = 0.05;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = WorldCoreTests.Setup();
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 215)));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.True(world.BeginGhost(1));
        world.Step();
        var cores = JObject.FromObject(world.CaptureCheckpoint())["Cores"]!;
        Assert.Equal(3 * rules.GhostBuildingCoreFlat + (int)decimal.Floor(rules.BaseCost(rules.ElementIndex("water")) * rules.GhostUnitCoreRatio),
            cores.Sum(core => (int)core["Amount"]!));
    }
}
