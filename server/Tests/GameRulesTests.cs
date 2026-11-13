using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>공유 데이터 검증과 상성·경제 계산의 관찰 가능한 결과를 확인한다.</summary>
public sealed class GameRulesTests
{
    internal static GameDataDocument Data() => new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
    internal static GameRules Rules() => new(Data());

    [Fact]
    public void QuickTransferLimitComesFromRules()
    {
        Assert.Equal(3, Rules().QuickTransferCount);
        JObject root = Data().Root;
        root["legion"]!["squads"]!["quickTransferCount"] = 2;
        Assert.Equal(2, new GameRules(new GameDataDocument(root.ToString())).QuickTransferCount);
    }

    [Fact]
    public void MatchupsWrapAndCrossCountersAreDirectional()
    {
        var rules = Rules();
        Assert.Equal(14, rules.Elements.Count);
        Assert.Equal(1.25f, rules.Matchup(rules.ElementIndex("lightning"), rules.ElementIndex("water")));
        Assert.Equal(0.8f, rules.Matchup(rules.ElementIndex("water"), rules.ElementIndex("lightning")));
        Assert.Equal(0.8f, rules.Matchup(rules.ElementIndex("light"), rules.ElementIndex("smoke")));
        Assert.Equal(1f, rules.Matchup(rules.ElementIndex("smoke"), rules.ElementIndex("light")));
        Assert.Equal(1f, rules.Matchup(0, 0));
    }

    [Fact]
    public void CostsUseNatureDiscountAndDropsFloorTheUndiscountedCost()
    {
        var rules = Rules();
        int water = rules.ElementIndex("water"), light = rules.ElementIndex("light");
        Assert.Equal(8, rules.TrainingCost(water, water));
        Assert.Equal(10, rules.TrainingCost(water, rules.ElementIndex("fire")));
        Assert.Equal(17, rules.TrainingCost(light, light));
        Assert.Equal(5, rules.DropAmount(water));
        Assert.Equal(8, rules.DropAmount(light));
    }

    [Fact]
    public void FacilityRanksAndBasePopulationComeFromSharedRules()
    {
        GameRules rules = Rules();
        Assert.Equal(9, rules.MaxBaseLevel);
        Assert.Equal(300, rules.MaxPopulation);
        Assert.Equal(new[] { 30, 50, 75, 100, 140, 180, 220, 260, 300 },
            Enumerable.Range(1, rules.MaxBaseLevel).Select(rules.PopulationCap));
        for (int rank = 1; rank <= GameRules.MaxFacilityUpgradeRank; rank++)
            Assert.Equal(rank + 1, GameRules.FacilityUpgradeRequiredLevel(rank));
        Assert.True(rules.TryGetFacilityUpgrade(BuildingKind.Collector, FacilityUpgradeBranch.A, 4, out FacilityUpgrade interval));
        Assert.Equal(36, interval.Cost);
        Assert.Equal(2d, interval.Effect);
        Assert.True(rules.TryGetFacilityUpgrade(BuildingKind.EnergyTower, FacilityUpgradeBranch.B, 4, out FacilityUpgrade energy));
        Assert.True(rules.TryGetFacilityUpgrade(BuildingKind.NatureTower, FacilityUpgradeBranch.B, 4, out FacilityUpgrade nature));
        Assert.Equal(energy, nature);
        Assert.False(rules.TryGetFacilityUpgrade(BuildingKind.Dome, FacilityUpgradeBranch.A, 1, out _));
        Assert.False(rules.TryGetFacilityUpgrade(BuildingKind.Collector, FacilityUpgradeBranch.None, 1, out _));
        Assert.False(rules.TryGetFacilityUpgrade(BuildingKind.Collector, FacilityUpgradeBranch.A, 5, out _));
        Assert.Equal(120, rules.CollectorIntervalTicks);
        Assert.Equal(1, rules.CollectorCoresPerCycle);
        Assert.Equal(210d, rules.NeutralWalkSpacingSeconds * rules.LegionSpeed);
        Assert.Equal(5, rules.ExplorationBundlesPerMinute);
        Assert.Equal(120d, rules.ExplorationCoreLifetime);
    }

    [Fact]
    public void FacilityTrainingDiscountRoundsUpAfterTheMainElementDiscount()
    {
        GameRules rules = Rules();
        int water = rules.ElementIndex("water"), fire = rules.ElementIndex("fire"), light = rules.ElementIndex("light");
        Assert.Equal(7, rules.TrainingCost(water, water, FacilityUpgradeBranch.A, 1));
        Assert.Equal(8, rules.TrainingCost(water, fire, FacilityUpgradeBranch.A, 1));
        Assert.Equal(9, rules.TrainingCost(light, water, FacilityUpgradeBranch.A, 4));
        Assert.Equal(17, rules.TrainingCost(light, water, FacilityUpgradeBranch.B, 4));
    }

    [Fact]
    public void SavedRoomGeometryCanBeRecreatedWithoutChangingTheDataHash()
    {
        GameRules rules = Rules();
        GameRules currentRoom = rules.ForRoom(2);
        GameRules oldRoom = currentRoom.ForRoom(2, 240);
        Assert.Equal(720, currentRoom.MapSize);
        Assert.Equal(240, oldRoom.MapSize);
        Assert.Equal(2, oldRoom.RoomPlayers);
        Assert.Equal(rules.DataHash, oldRoom.DataHash);
        Assert.Same(oldRoom, oldRoom.ForRoom(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => rules.ForRoom(2, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => rules.ForRoom(2, rules.MapSize + 1));
    }

    [Theory]
    [InlineData("world.tickRate", 0)]
    [InlineData("elements[0].hp", -1)]
    [InlineData("economy.conversionChance", 2)]
    [InlineData("economy.pvpRecovery.instant", 1)]
    [InlineData("reactions[0].requiresInLegion[0]", "missing")]
    [InlineData("elements[1].effect.type", "missing")]
    [InlineData("elements[1].effect", 42)]
    [InlineData("economy.coreDropRounding", "ceil")]
    [InlineData("legion.formation.minimumSeparation", 2)]
    [InlineData("legion.formation.catchUpDistance", 0)]
    [InlineData("legion.formation.catchUpSpeedMultiplier", 1)]
    [InlineData("legion.squads.quickTransferCount", 0)]
    [InlineData("legion.squads.quickTransferCount", 1.5)]
    [InlineData("legion.squads.quickTransferCount", int.MaxValue)]
    [InlineData("buildings.collector.production.intervalSeconds", 0)]
    [InlineData("buildings.collector.production.coresPerCycle", 16_777_217)]
    [InlineData("facilityUpgrades.collector.a[0].cost", -1)]
    [InlineData("facilityUpgrades.collector.a[1].effect", 4)]
    [InlineData("facilityUpgrades.forge_nature.a[3].effect", 100)]
    [InlineData("facilityUpgrades.forge_energy.b[3].effect", 101)]
    [InlineData("neutral.walkSpacingSeconds", 0)]
    [InlineData("economy.opening.explorationBundlesPerMinute", 1200)]
    [InlineData("economy.opening.explorationCoreLifetimeSeconds", 0)]
    [InlineData("economy.opening.explorationCoreLifetimeSeconds", 0.05)]
    [InlineData("population.max", 299)]
    [InlineData("population.max", 1000)]
    [InlineData("baseLevels[5].towerSlots", 64)]
    [InlineData("towers.dpsInUnitsOfSameElement", float.MaxValue)]
    [InlineData("facilityUpgrades.tower.b[3].effect", float.MaxValue)]
    public void InvalidRulesFailAtLoadingBoundary(string path, object value)
    {
        JObject root = Data().Root;
        root.SelectToken(path)!.Replace(JToken.FromObject(value));
        Assert.Throws<ArgumentException>(() => new GameRules(new GameDataDocument(root.ToString())));
    }

    [Fact]
    public void DuplicateElementsAndIncompleteRingsAreRejected()
    {
        JObject root = Data().Root;
        root["elements"]![0]!["id"] = "water";
        Assert.Throws<ArgumentException>(() => new GameRules(new GameDataDocument(root.ToString())));
        root = Data().Root;
        ((JArray)root["rings"]!["nature"]!).RemoveAt(0);
        Assert.Throws<ArgumentException>(() => new GameRules(new GameDataDocument(root.ToString())));
    }

    [Fact]
    public void FacilityBranchesRequireFourRanks()
    {
        JObject root = Data().Root;
        ((JArray)root.SelectToken("facilityUpgrades.tower.a")!).RemoveAt(3);
        Assert.Throws<ArgumentException>(() => new GameRules(new GameDataDocument(root.ToString())));
    }
}
