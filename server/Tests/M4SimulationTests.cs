using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>M4 건설, 공성과 접속 종료 시 서버 판정을 검사한다.</summary>
public sealed class M4SimulationTests
{
    [Fact]
    public void PendingForgeReservesItsSiteAndOnlyTrainsOnCompletionTick()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 225), cores: 300));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.NatureForge, 215, 200));
        world.TryEnqueue(1, SimCommand.Build(2, BuildingKind.NatureForge, 185, 200));
        world.TryEnqueue(1, SimCommand.Train(3, "water"));
        world.Step();
        var view = world.GetView(1);
        var forge = Assert.Single(view.Buildings, b => b.Kind == BuildingKind.NatureForge);
        Assert.True(forge.IsUnderConstruction);
        Assert.False(view.NatureForge);
        Assert.Equal(300 - rules.BuildingCost(BuildingKind.NatureForge), view.CoreBalance);
        Assert.Contains(view.Events, e => e.Sequence == 2 && e.Detail == (int)CommandFailure.BuildingLimit);
        Assert.Contains(view.Events, e => e.Sequence == 3 && e.Detail == (int)CommandFailure.NotUnlocked);
        Assert.Equal(0, view.OwnBuildingCount);

        while (view.Tick < forge.ConstructionEndTick - 1) world.Step();
        Assert.False(view.NatureForge);
        world.TryEnqueue(1, SimCommand.Train(4, "water"));
        world.Step();
        Assert.True(view.NatureForge);
        Assert.True(view.IsTraining);
        Assert.Equal(1, view.OwnBuildingCount);
        Assert.False(Assert.Single(view.Buildings, b => b.Kind == BuildingKind.NatureForge).IsUnderConstruction);
    }

    [Fact]
    public void DisconnectCancelsAndRefundsOnlyPendingConstruction()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 225), cores: 100));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 215, 200));
        world.Step();
        var view = world.GetView(1);
        Assert.Equal(80, view.CoreBalance);
        Assert.True(world.BeginGhost(1));
        Assert.Equal(100, view.CoreBalance);
        Assert.Single(view.Buildings);
        Assert.Empty(view.Standings);
    }

    [Fact]
    public void DestroyedConstructionNeverActivatesAndFreesItsSlotWithoutRefund()
    {
        var data = GameRulesTests.Data().Root;
        data["buildings"]!["forge_nature"]!["hp"] = 1;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        var attacker = new Vector2(214, 200);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(400), attacker));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200), new Vector2(200, 225), cores: 200));
        setup.Units.Add(new UnitSpawn(1, "earth", attacker));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(2, SimCommand.Build(1, BuildingKind.NatureForge, 210, 200));
        world.Step();
        var view = world.GetView(2);
        Assert.DoesNotContain(view.Buildings, b => b.Kind == BuildingKind.NatureForge);
        Assert.Equal(200 - rules.BuildingCost(BuildingKind.NatureForge), view.CoreBalance);
        Assert.Equal(0, view.OwnBuildingCount);

        world.TryEnqueue(2, SimCommand.Build(2, BuildingKind.NatureForge, 190, 200));
        world.Step();
        Assert.True(Assert.Single(view.Buildings, b => b.Kind == BuildingKind.NatureForge).IsUnderConstruction);
        Assert.Equal(200 - 2 * rules.BuildingCost(BuildingKind.NatureForge), view.CoreBalance);
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    public void BuildingCostIsChargedOnlyOnSuccessfulConstruction(int cores, bool succeeds)
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), WorldCoreTests.Setup(cores: cores, level: 1), 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 200, 215)));
        Assert.Equal(CommandAdmission.StaleSequence, world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 200, 215)));
        world.Step();
        var view = world.GetView(1);
        Assert.Equal(succeeds ? 0 : cores, view.CoreBalance);
        Assert.Equal(succeeds, view.Buildings.Any(b => b.Kind == BuildingKind.Collector));
    }

    [Fact]
    public void GhostExpiryReleasesVisionCapacityWithoutReusingTheOwner()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        Assert.True(world.BeginGhost(1));
        Assert.True(world.HasGhosts);
        Assert.Equal(CommandAdmission.UnknownPlayer, world.TryEnqueue(1, SimCommand.Rally(1)));
        for (int tick = 0; tick < 600 * rules.TickRate; tick++) world.Step();
        Assert.False(world.HasGhosts);
        Assert.False(world.TryGetView(1, out _));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(16, "water", new Vector2(200), new Vector2(200, 212))));
        Assert.Equal(16, world.GetView(16).PlayerId);
    }

    [Fact]
    public void UpgradesUnlockEnergyConstructionAndRespectTheLevelCap()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        int budget = rules.UpgradeCost(2) + rules.UpgradeCost(3) + rules.UpgradeCost(4) + rules.BuildingCost(BuildingKind.EnergyForge);
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 212), 1, budget));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.EnergyForge, 215, 200));
        world.TryEnqueue(1, SimCommand.UpgradeBase(2));
        world.TryEnqueue(1, SimCommand.UpgradeBase(3));
        world.TryEnqueue(1, SimCommand.Build(4, BuildingKind.EnergyForge, 215, 200));
        world.TryEnqueue(1, SimCommand.UpgradeBase(5));
        world.TryEnqueue(1, SimCommand.UpgradeBase(6));
        world.Step();

        var view = world.GetView(1);
        Assert.Equal(4, view.BaseLevel);
        Assert.Equal(50, view.PopulationCap);
        Assert.Equal(0, view.CoreBalance);
        Assert.False(view.EnergyForge);
        Assert.Contains(view.Events, e => e.Sequence == 1 && e.Detail == (int)CommandFailure.NotUnlocked);
        Assert.Contains(view.Events, e => e.Sequence == 6 && e.Detail == (int)CommandFailure.MaxLevel);
        long completion = Assert.Single(view.Buildings, b => b.Kind == BuildingKind.EnergyForge).ConstructionEndTick;
        while (view.Tick < completion) world.Step();
        Assert.True(view.EnergyForge);
    }

    [Fact]
    public void OccupiedAndOutOfRadiusConstructionPreserveCores()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup(cores: 300, level: 1);
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 215), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 200, 215));
        world.TryEnqueue(1, SimCommand.Build(2, BuildingKind.Collector, 224, 200));
        world.Step();
        Assert.Equal(300, world.GetView(1).CoreBalance);
        Assert.Equal(2, world.GetView(1).Events.Count(e => e.Kind == SimEventKind.CommandRejected &&
            e.Detail == (int)CommandFailure.InvalidPlacement));
        world.TryEnqueue(1, SimCommand.Build(3, BuildingKind.Collector, 223, 200));
        world.Step();
        Assert.Equal(300 - rules.BuildingCost(BuildingKind.Collector), world.GetView(1).CoreBalance);
    }

    [Fact]
    public void TowerUsesItsSlotAndDoesNotConvertItsKill()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 215), 1, 300));
        setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(215, 200), health: 1));
        var world = new SimulationWorld(rules, setup, 1);
        int earth = rules.ElementIndex("earth");
        int water = rules.ElementIndex("water");
        world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.NatureTower, 210, 200, earth));
        world.TryEnqueue(1, SimCommand.Build(2, BuildingKind.NatureTower, 200, 210, water));
        world.TryEnqueue(1, SimCommand.Build(3, BuildingKind.NatureTower, 190, 200, earth));
        world.Step();
        var view = world.GetView(1);
        Assert.Equal(2, view.Buildings.Count(b => b.Kind == BuildingKind.NatureTower));
        Assert.Equal(300 - 2 * rules.BuildingCost(BuildingKind.NatureTower), view.CoreBalance);
        Assert.Equal(0, world.Metrics.Conversions);
        Assert.Equal(0, world.Metrics.Attacks);
        Assert.Contains(view.Events, e => e.Sequence == 3 && e.Detail == (int)CommandFailure.BuildingLimit);
        var tower = Assert.Single(view.Buildings, b => b.TowerElement == earth);
        while (view.Tick < tower.ConstructionEndTick) world.Step();
        Assert.Equal(1, world.Metrics.Drops);
        Assert.Contains(view.Events, e => e.Kind == SimEventKind.Attack && e.SourceKind == ObservedEntityKind.Building && e.Element == earth);
    }

    [Theory]
    [InlineData(BuildingKind.NatureTower, "water", 0f, true)]
    [InlineData(BuildingKind.EnergyTower, "light", 0f, true)]
    [InlineData(BuildingKind.NatureTower, "water", 0.01f, false)]
    [InlineData(BuildingKind.EnergyTower, "light", 0.01f, false)]
    public void TowerAttacksNeutralOnlyInsideItsDisplayedCenterRange(BuildingKind kind, string element, float offset, bool hits)
    {
        var data = GameRulesTests.Data().Root;
        data["towers"]!["buildSeconds"] = 0;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 225), 4, 100));
        setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(210 + rules.TowerRange + offset, 200)));
        var world = new SimulationWorld(rules, setup, 1);
        int target = Assert.Single(world.GetView(1).Units).Id;
        world.TryEnqueue(1, SimCommand.Build(1, kind, 210, 200, rules.ElementIndex(element)));
        world.Step();
        var view = world.GetView(1);
        Assert.Equal(hits, view.Events.Any(e => e.Kind == SimEventKind.Attack && e.SourceKind == ObservedEntityKind.Building &&
            e.EntityId == target && e.TargetId == target && e.HasSourcePosition));
        UnitView neutral = Assert.Single(view.Units);
        Assert.Equal(hits, neutral.Health < neutral.MaxHealth);
    }

    [Fact]
    public void MeleeBreaksTheDomeAndDefeatsOnlyTheTargetedBaseOwner()
    {
        var data = GameRulesTests.Data().Root;
        data["buildings"]!["base"]!["hp"] = 2;
        data["buildings"]!["dome"]!["hp"] = 3;
        data["buildings"]!["dome"]!["buildSeconds"] = 0;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        var edge = new Vector2(207, 200);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(400), edge));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200), new Vector2(200, 230), 4, 100));
        setup.Units.Add(new UnitSpawn(1, "earth", edge));
        setup.Units.Add(new UnitSpawn(2, "water", new Vector2(200, 230), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.Build(1, BuildingKind.Dome, 0, 0)));
        world.Step();
        Assert.False(world.TryGetView(2, out _));
        Assert.Equal(1, world.GetView(1).Population);
        Assert.Equal(0, world.Metrics.Drops);
        Assert.Equal(5, Assert.Single(world.GetView(1).Events,
            e => e.Kind == SimEventKind.Attack && e.TargetKind == ObservedEntityKind.Building).Amount);
        Assert.Equal(1, world.GetView(1).BattleWins);
        Assert.Equal(1, Assert.Single(world.GetView(1).Standings).PlayerId);
        world.Step();
        Assert.Equal(1, world.GetView(1).BattleWins);
    }

    [Fact]
    public void DestroyedForgeRefundsWaitingOrdersAndPreservesOtherGroup()
    {
        var data = GameRulesTests.Data().Root;
        data["buildings"]!["forge_nature"]!["hp"] = 1;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        var attacker = new Vector2(214, 200);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(400), attacker));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200), new Vector2(200, 225), 4, 100, natureForge: true, energyForge: true));
        setup.Units.Add(new UnitSpawn(1, "earth", attacker));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(2, SimCommand.Train(1, "water"));
        world.TryEnqueue(2, SimCommand.Train(2, "fire"));
        world.TryEnqueue(2, SimCommand.Train(3, "light"));
        world.Step();
        WorldView view = world.GetView(2);
        Assert.False(view.NatureForge);
        Assert.True(view.IsTraining);
        Assert.Equal(rules.ElementIndex("light"), view.TrainingElement);
        Assert.Equal(1, view.ReservedPopulation);
        Assert.Empty(view.TrainingQueue);
        Assert.Equal(75, view.CoreBalance);
    }

    [Fact]
    public void EqualDistanceCollectorsCreditOnlyTheLowerOwnerId()
    {
        var rules = GameRulesTests.Rules();
        int cost = rules.BuildingCost(BuildingKind.Collector);
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 225), 1, cost));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(230, 200), new Vector2(230, 225), 1, cost));
        setup.Cores.Add(new CoreSpawn(new Vector2(215, 210), 5));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 210, 200));
        world.TryEnqueue(2, SimCommand.Build(1, BuildingKind.Collector, 220, 200));
        world.Step();
        Assert.Equal(0, world.Metrics.Pickups);
        WorldView view = world.GetView(1);
        long completion = Assert.Single(view.Buildings, b => b.Kind == BuildingKind.Collector && b.OwnerId == 1).ConstructionEndTick;
        while (view.Tick < completion) world.Step();
        Assert.Equal(5, world.GetView(1).CoreBalance);
        Assert.Equal(0, world.GetView(2).CoreBalance);
        Assert.Equal(1, world.Metrics.Pickups);
    }

    [Fact]
    public void GhostDefeatConvertsSurvivingUnitsAndDropsRemainingBuildingsOnce()
    {
        var data = GameRulesTests.Data().Root;
        data["buildings"]!["base"]!["hp"] = 1;
        data["economy"]!["conversionChance"] = 1;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        var attacker = new Vector2(193, 200);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(400), attacker));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200), new Vector2(200, 225), 1, 100, natureForge: true));
        setup.Units.Add(new UnitSpawn(1, "earth", attacker));
        setup.Units.Add(new UnitSpawn(2, "water", new Vector2(200, 224), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        world.BeginGhost(2);
        world.Step();
        world.Step();
        Assert.False(world.TryGetView(2, out _));
        Assert.Equal(2, world.GetView(1).Population);
        Assert.Equal(1, world.Metrics.Conversions);
        Assert.Equal(0, world.GetView(1).BattleWins);
        Assert.Equal(1, world.Metrics.Drops);
        Assert.Contains(world.GetView(1).Cores, core => core.Amount == rules.GhostBuildingCoreFlat);
    }

    [Fact]
    public void SimultaneousBaseLossRemovesBothOwnersAtTheEndOfCombat()
    {
        var data = GameRulesTests.Data().Root;
        data["buildings"]!["base"]!["hp"] = 1;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        var first = new Vector2(393, 400);
        var second = new Vector2(207, 200);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(200), first));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(400), second));
        setup.Units.Add(new UnitSpawn(1, "earth", first));
        setup.Units.Add(new UnitSpawn(2, "earth", second));
        var world = new SimulationWorld(rules, setup, 1);
        world.Step();
        Assert.False(world.TryGetView(1, out _));
        Assert.False(world.TryGetView(2, out _));
        Assert.Equal(0, world.Metrics.PlayerUnits);
        Assert.Equal(0, world.Metrics.Drops);
    }

    [Fact]
    public void MeleeDoesNotStallAtARoundedAttackBoundary()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        var basePosition = new Vector2(1337, 1289);
        float radians = MathF.PI / 180;
        Vector2 start = basePosition + new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * 9;
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(400), start));
        setup.Players.Add(new PlayerSpawn(2, "water", basePosition, basePosition - new Vector2(0, 25)));
        setup.Units.Add(new UnitSpawn(1, "earth", start));
        var world = new SimulationWorld(rules, setup, 1);

        for (int tick = 0; tick < rules.TickRate; tick++) world.Step();

        var victim = Assert.Single(world.GetView(1).Buildings, b => b.OwnerId == 2);
        Assert.True(victim.Health < victim.MaxHealth);
    }

    [Fact]
    public void ProtectedUnitsDoNotBlockOtherTargets()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        var center = new Vector2(100);
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(700), center));
        setup.Players.Add(new PlayerSpawn(3, "water", new Vector2(103, 120), new Vector2(103, 110)));
        setup.Units.Add(new UnitSpawn(1, "water", center));
        setup.Units.Add(new UnitSpawn(3, "life", center + new Vector2(3, 0), garrison: true));
        var world = new SimulationWorld(rules, setup, 1);
        world.EnableEcology(300);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "water", new Vector2(500), center + Vector2.UnitX)));
        world.Step();
        Assert.Contains(world.GetView(1).Events, e => e.Kind == SimEventKind.Attack && e.OwnerId == 1 && e.TargetId == 2);
    }
}
