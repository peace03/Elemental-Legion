using ElementalLegion.Shared.Data;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>공유 데이터 검증과 상성·경제 계산의 관찰 가능한 결과를 확인한다.</summary>
public sealed class GameRulesTests
{
    internal static GameDataDocument Data() => new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
    internal static GameRules Rules() => new(Data());

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

    [Theory]
    [InlineData("world.tickRate", 0)]
    [InlineData("elements[0].hp", -1)]
    [InlineData("economy.conversionChance", 2)]
    [InlineData("reactions[0].requiresInLegion[0]", "missing")]
    [InlineData("elements[1].effect.type", "missing")]
    [InlineData("elements[1].effect", 42)]
    [InlineData("economy.coreDropRounding", "ceil")]
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
}
