using System.Numerics;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>원소 효과·공격 주기·오라와 중복 피해 방지를 확인한다.</summary>
public sealed class CombatTests
{
    internal static WorldSetup Battle(params UnitSpawn[] units)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(300, 300), new Vector2(50, 50), 4, 100, true, true));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(800, 800), new Vector2(800, 800), 4));
        setup.Units.AddRange(units);
        return setup;
    }
    internal static UnitSpawn Unit(int owner, string element, float x, float y = 50, float? hp = null, bool converted = false, bool garrison = true) => new(owner, element, new Vector2(x, y), hp, converted, owner >= 0 && garrison);
    internal static UnitView Find(SimulationWorld world, int id, int observer = 1) => world.GetView(observer).Units.Single(unit => unit.Id == id);

    [Fact]
    public void NearestTargetUsesIdForEqualDistances()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "water", 50), Unit(2, "earth", 52, 49), Unit(2, "earth", 52, 51)), 1);
        world.Step();
        Assert.True(Find(world, 2).Health < 160);
        Assert.Equal(160, Find(world, 3).Health);
    }

    [Theory]
    [InlineData("lightning", 9.6f, 4.8f)]
    [InlineData("gravity", 11.2f, 11.2f)]
    public void ChainAndSplashHitEachVictimOnlyOnce(string element, float primary, float secondary)
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, element, 50), Unit(2, "earth", 52), Unit(2, "earth", 54)), 1);
        world.Step();
        Assert.Equal(160 - primary, Find(world, 2).Health, 3);
        Assert.Equal(160 - secondary, Find(world, 3).Health, 3);
    }

    [Fact]
    public void PierceIncludesPrimaryInLimitAndRejectsOffAxisTargets()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "light", 50), Unit(2, "earth", 52), Unit(2, "earth", 54), Unit(2, "earth", 56), Unit(2, "earth", 58), Unit(2, "earth", 54, 52)), 1);
        world.Step();
        Assert.Equal(145, Find(world, 2).Health);
        Assert.Equal(145, Find(world, 3).Health);
        Assert.Equal(145, Find(world, 4).Health);
        Assert.Equal(160, Find(world, 5).Health);
        Assert.Equal(160, Find(world, 6).Health);
    }

    [Fact]
    public void BurnUsesFixedDeltaAndDoesNotDoubleOnReapplication()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "fire", 50), Unit(2, "earth", 51)), 1);
        world.Step();
        float health = Find(world, 2).Health;
        world.Step();
        Assert.Equal(health - 2f * 0.05f * 0.8f, Find(world, 2).Health, 3);
    }

    [Fact]
    public void RadiationContinuesAfterItsSourceDiesAndExpires()
    {
        var setup = Battle(Unit(1, "decay", 50, hp: 1), Unit(2, "earth", 51));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        float health = Find(world, 2, 2).Health;
        for (int i = 0; i < 100; i++) world.Step();
        float after = Find(world, 2, 2).Health;
        Assert.True(after < health);
        for (int i = 0; i < 10; i++) world.Step();
        Assert.Equal(after, Find(world, 2, 2).Health, 3);
    }

    [Fact]
    public void LifestealUsesActualHealthDamageRatherThanOverkill()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "dark_matter", 50, hp: 50), Unit(2, "earth", 51, hp: 1, converted: true)), 1);
        world.Step();
        Assert.Equal(50.2f, Find(world, 1).Health, 3);
    }

    [Fact]
    public void ShieldsRefreshOnIntervalAndDuplicateAurasDoNotStack()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "magnetism", 50), Unit(1, "magnetism", 50, 51), Unit(2, "water", 54)), 1);
        world.Step();
        Assert.Equal(150, Find(world, 1).Health);
        Assert.Equal(11, Find(world, 1).Shield);
        world.Step();
        Assert.Equal(11, Find(world, 1).Shield);
        Assert.Equal(20, Find(world, 2).Shield);
    }

    [Fact]
    public void SmokeAndHasteAurasUseStrongestSingleContribution()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "crystal", 50), Unit(1, "crystal", 50, 51), Unit(1, "smoke", 50, 49), Unit(1, "smoke", 50, 49), Unit(2, "water", 53)), 1);
        world.Step();
        Assert.Equal(1.1f, Find(world, 1).AttackSpeedMultiplier, 3);
        Assert.Equal(0.8f, Find(world, 5).RangeMultiplier, 3);
    }

    [Fact]
    public void ReapplyingBurnRefreshesDurationWithoutAddingAnotherDot()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "fire", 50), Unit(2, "life", 51)), 1);
        for (int tick = 0; tick < 25; tick++) world.Step();
        float before = Find(world, 2).Health;
        world.Step();
        Assert.Equal(before - 0.1f, Find(world, 2).Health, 3);
    }

    [Fact]
    public void AuraBonusesAreRemovedAfterSourceDiesOrChangesOwner()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "crystal", 50, hp: 1), Unit(1, "wind", 50, 51), Unit(2, "water", 54)), 1);
        world.Step();
        Assert.Equal(1.1f, Find(world, 2).AttackSpeedMultiplier, 3);
        world.Step();
        Assert.Equal(1, Find(world, 2).AttackSpeedMultiplier);
        world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "smoke", 50, hp: 1), Unit(2, "water", 53)), 1);
        world.Step(); world.Step();
        Assert.Equal(1, Find(world, 2, 2).RangeMultiplier);
    }

    [Fact]
    public void LifeHealsItselfAndCapsAtMaxHealth()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "life", 50, hp: 65)), 1);
        world.Step();
        Assert.Equal(70, Find(world, 1).Health);
    }

    [Fact]
    public void NonIntegralAttackIntervalKeepsFractionalRemainder()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), Battle(Unit(1, "wind", 50), Unit(2, "life", 52)), 1);
        for (int i = 0; i < 400; i++) world.Step();
        Assert.InRange(world.Metrics.Attacks, 36, 37);
    }
}
