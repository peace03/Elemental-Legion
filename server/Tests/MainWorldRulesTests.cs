using ElementalLegion.Shared.Data;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>기존 기본 수치와 이후 적용할 시험 수치의 로딩 계약을 구분한다.</summary>
public sealed class MainWorldRulesTests
{
    [Fact]
    public void MainWorldRulesValidateBudgets()
    {
        var root = GameRulesTests.Data().Root;
        root["start"]!["unitCount"] = 12;
        root["start"]!["fieldUnitCount"] = 8;
        root["start"]!["garrisonUnitCount"] = 4;
        root["population"]!["max"] = 100;
        int[] caps = { 30, 50, 75, 100 }, guards = { 8, 12, 16, 20 };
        for (int i = 0; i < caps.Length; i++)
        {
            root["baseLevels"]![i]!["populationCap"] = caps[i];
            root["baseLevels"]![i]!["garrisonCap"] = guards[i];
        }
        var rules = new GameRules(new GameDataDocument(root.ToString()));
        Assert.Equal(12, rules.StartUnitCount);
        Assert.Equal(8, rules.StartFieldUnitCount);
        Assert.Equal(4, rules.StartGarrisonUnitCount);
        Assert.Equal(3, rules.FieldSquadCount);
        Assert.Equal(4, rules.MaxTrainingBatchCount);
        for (int i = 0; i < caps.Length; i++)
        {
            Assert.Equal(caps[i], rules.PopulationCap(i + 1));
            Assert.Equal(guards[i], rules.GarrisonCap(i + 1));
            Assert.True(rules.GarrisonCap(i + 1) <= rules.PopulationCap(i + 1));
        }
        Assert.Equal(25f, rules.GarrisonRadius);
        Assert.Equal(60, rules.RecallCastTicks);
        Assert.Equal(1200, rules.RecallCooldownTicks);
        Assert.True(rules.RecallCancelOnHit);
        Assert.Equal(100, rules.DefenseAlarmRepeatTicks);
        Assert.Equal(250f, rules.BaseIncomingDamagePerSecond);
        Assert.Equal(400, rules.PvpRecoveryLifetimeTicks);
        Assert.Equal(60, rules.PvpRecoverySecureTicks);
        Assert.Equal(5f, rules.PvpRecoveryRadius);
        Assert.Equal(0.5f, rules.PvpRecoveryConvertedHealthRatio);
        Assert.Equal(0.25f, rules.PvpRecoveryRescuedHealthRatio);
        Assert.Equal(0.6, rules.ConversionChance);
        Assert.Equal(0.8, rules.NeutralConversionChance);
        Assert.Equal(35f, rules.OpeningFirstNatureMinDistance);
        Assert.Equal(70f, rules.OpeningFirstNatureMaxDistance);
        Assert.Equal(70f, rules.OpeningNextGroupMinDistance);
        Assert.Equal(140f, rules.OpeningNextGroupMaxDistance);
        Assert.Equal(20f, rules.OpeningFirstCoreMinDistance);
        Assert.Equal(35f, rules.OpeningFirstCoreMaxDistance);
        Assert.Equal(20, rules.OpeningCoreBundleSize * (rules.OpeningNearbyBundlesPerMinute + rules.OpeningSharedBundlesPerMinute));
    }

    [Fact]
    public void DefaultDataKeepsThreeUnitsAndExistingPopulationUntilTaskSeven()
    {
        var rules = GameRulesTests.Rules();
        Assert.Equal(3, rules.StartUnitCount);
        Assert.Equal(new[] { 20, 30, 40, 50 }, Enumerable.Range(1, 4).Select(rules.PopulationCap));
        Assert.Equal(3, (int?)GameRulesTests.Data().Root.SelectToken("start.fieldUnitCount"));
        Assert.Equal(0, (int?)GameRulesTests.Data().Root.SelectToken("start.garrisonUnitCount"));
    }

    [Theory]
    [InlineData("start.fieldUnitCount", -1)]
    [InlineData("start.garrisonUnitCount", 4)]
    [InlineData("baseLevels[0].garrisonCap", 21)]
    [InlineData("legion.squads.fieldCount", 4)]
    [InlineData("training.maxBatchCount", 5)]
    [InlineData("legion.garrisonRadius", double.PositiveInfinity)]
    [InlineData("legion.recall.cooldownSeconds", 1e18)]
    [InlineData("economy.pvpRecovery.convertedHealthRatio", 2)]
    [InlineData("economy.pvpRecovery.secureSeconds", 21)]
    [InlineData("neutral.opening.firstNatureMinDistance", 71)]
    [InlineData("economy.opening.firstCoreMinDistance", 36)]
    [InlineData("legion.recall.castSeconds", 0)]
    [InlineData("legion.defenseAlarm.repeatSeconds", 1e18)]
    [InlineData("economy.pvpRecovery.rescuedHealthRatio", 0)]
    [InlineData("economy.pvpRecovery.radius", double.NaN)]
    [InlineData("economy.opening.bundleSize", 5)]
    [InlineData("economy.opening.nearbyBundlesPerMinute", -1)]
    [InlineData("neutral.opening.nextGroupMinDistance", 141)]
    public void InvalidTrialValuesAreRejected(string path, double value)
    {
        var root = GameRulesTests.Data().Root;
        var token = root.SelectToken(path);
        Assert.NotNull(token);
        token!.Replace(value);
        Assert.Throws<ArgumentException>(() => new GameRules(new GameDataDocument(root.ToString())));
    }

    [Theory]
    [InlineData("start.fieldUnitCount")]
    [InlineData("training.maxBatchCount")]
    [InlineData("legion.recall.cooldownSeconds")]
    [InlineData("baseLevels[0].garrisonCap")]
    public void MissingTrialKeysAreRejected(string path)
    {
        var root = GameRulesTests.Data().Root;
        (root.SelectToken(path)?.Parent as JProperty)?.Remove();
        Assert.Throws<ArgumentException>(() => new GameRules(new GameDataDocument(root.ToString())));
    }

    [Fact]
    public void NewTrialPathsAreMarkedAsPlaceholders()
    {
        var paths = GameRulesTests.Data().Root["_meta"]!["placeholderPaths"]!.Values<string>().ToArray();
        foreach (string path in new[] { "start.fieldUnitCount", "start.garrisonUnitCount", "baseLevels[].garrisonCap",
            "legion.squads.fieldCount", "legion.garrisonRadius", "training.maxBatchCount", "legion.recall.cooldownSeconds",
            "legion.defenseAlarm.repeatSeconds", "buildings.base.incomingDamagePerSecond", "economy.pvpRecovery",
            "neutral.opening", "economy.opening" })
            Assert.Contains(path, paths);
    }
}
