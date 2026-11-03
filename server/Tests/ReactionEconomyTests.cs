using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>반응 출처·전향 경계·사망 순서와 핵 지급의 정합성을 확인한다.</summary>
public sealed class ReactionEconomyTests
{
    internal static GameRules CertainConversion()
    {
        var root = GameRulesTests.Data().Root;
        root["economy"]!["conversionChance"] = 1;
        root["neutral"]!["conversionChance"] = 1;
        return new GameRules(new GameDataDocument(root.ToString()));
    }

    [Fact]
    public void ElectrocuteAmplifiesOnlyPrimaryExtendsChainAndRetainsWet()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), CombatTests.Battle(CombatTests.Unit(1, "water", 50, garrison: false), CombatTests.Unit(1, "lightning", 50, garrison: false), CombatTests.Unit(2, "earth", 53), CombatTests.Unit(2, "earth", 57.8f)), 1);
        world.Step();
        Assert.Equal(138.4f, CombatTests.Find(world, 3).Health, 3);
        Assert.Equal(155.2f, CombatTests.Find(world, 4).Health, 3);
        Assert.Equal(1, world.Metrics.Reactions[(int)ReactionKind.Electrocute]);
        for (int i = 0; i < 17; i++) world.Step();
        Assert.True(world.Metrics.Reactions[(int)ReactionKind.Electrocute] >= 2);
    }

    [Fact]
    public void FreezeMultipliesChillWithoutStackingTheSameEffect()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), CombatTests.Battle(CombatTests.Unit(1, "water", 50, garrison: false), CombatTests.Unit(1, "ice", 50, garrison: false), CombatTests.Unit(2, "earth", 51)), 1);
        world.Step();
        Assert.Equal(0.425f, CombatTests.Find(world, 3).AttackSpeedMultiplier, 3);
        Assert.Equal(1, world.Metrics.Reactions[(int)ReactionKind.Freeze]);
    }

    [Theory]
    [InlineData("water", "fire", "Steam", 0.7f)]
    [InlineData("earth", "wind", "Sandstorm", 0.8f)]
    public void FixedAreasApplyTheirRangeDebuff(string first, string second, string kind, float multiplier)
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), CombatTests.Battle(CombatTests.Unit(1, first, 50, garrison: false), CombatTests.Unit(1, second, 50, garrison: false), CombatTests.Unit(2, "earth", 51)), 1);
        world.Step();
        Assert.Equal(1, world.Metrics.Reactions[(int)Enum.Parse<ReactionKind>(kind)]);
        Assert.Equal(multiplier, CombatTests.Find(world, 3).RangeMultiplier, 3);
    }

    [Fact]
    public void BurnSpreadCopiesRemainingBurnAndPlasmaHitsTheArea()
    {
        var spread = new SimulationWorld(GameRulesTests.Rules(), CombatTests.Battle(CombatTests.Unit(1, "fire", 50, garrison: false), CombatTests.Unit(1, "wind", 50, garrison: false), CombatTests.Unit(2, "earth", 51.5f), CombatTests.Unit(2, "earth", 54)), 1);
        spread.Step(); spread.Step();
        Assert.Equal(1, spread.Metrics.Reactions[(int)ReactionKind.BurnSpread]);
        Assert.True(CombatTests.Find(spread, 4).Health < 160);
        var plasma = new SimulationWorld(GameRulesTests.Rules(), CombatTests.Battle(CombatTests.Unit(1, "smoke", 50, garrison: false), CombatTests.Unit(1, "lightning", 50, garrison: false), CombatTests.Unit(2, "earth", 53.5f), CombatTests.Unit(2, "earth", 55)), 1);
        plasma.Step();
        Assert.Equal(1, plasma.Metrics.Reactions[(int)ReactionKind.PlasmaBurst]);
        Assert.True(CombatTests.Find(plasma, 4).Health < 155.2f);
    }

    [Fact]
    public void StatusFromAnotherLegionDoesNotTriggerReaction()
    {
        var setup = CombatTests.Battle(CombatTests.Unit(3, "water", 55, 55, garrison: false), CombatTests.Unit(1, "water", 50, 80, garrison: false), CombatTests.Unit(1, "lightning", 47, garrison: false), CombatTests.Unit(-1, "earth", 53));
        setup.Players[0] = new PlayerSpawn(1, "water", new Vector2(300, 300), new Vector2(47, 50), 4);
        setup.Players.Add(new PlayerSpawn(3, "water", new Vector2(1000, 1000), new Vector2(55, 55), 4));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        Assert.Equal(143.2f, CombatTests.Find(world, 4).Health, 3);
        Assert.Equal(0, world.Metrics.Reactions[(int)ReactionKind.Electrocute]);
    }

    [Theory]
    [InlineData("water", "lightning", ReactionKind.Electrocute, 53)]
    [InlineData("smoke", "lightning", ReactionKind.PlasmaBurst, 53)]
    [InlineData("earth", "wind", ReactionKind.Sandstorm, 51)]
    public void GarrisonEffectsDoNotBecomeLegionReactionSources(string first, string trigger, ReactionKind reaction, float targetX)
    {
        var setup = CombatTests.Battle(CombatTests.Unit(1, first, 50), CombatTests.Unit(1, first, 50, 80, garrison: false), CombatTests.Unit(1, trigger, 50, garrison: false), CombatTests.Unit(2, "earth", targetX));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        Assert.Equal(0, world.Metrics.Reactions[(int)reaction]);
    }

    [Fact]
    public void JoiningLaterDoesNotRetroactivelyQualifyAnExistingWetStatus()
    {
        var setup = CombatTests.Battle(CombatTests.Unit(1, "water", 50), CombatTests.Unit(1, "water", 50, 80, garrison: false), CombatTests.Unit(1, "lightning", 50, garrison: false), CombatTests.Unit(2, "earth", 53));
        setup.Players[0] = new PlayerSpawn(1, "water", new Vector2(50, 60), new Vector2(50, 50), 4);
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        Assert.True(CombatTests.Find(world, 1).Garrison);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Rally(1)));
        world.Step();
        Assert.False(CombatTests.Find(world, 1).Garrison);
        for (int tick = 0; tick < 16; tick++) world.Step();
        Assert.Equal(0, world.Metrics.Reactions[(int)ReactionKind.Electrocute]);
    }

    [Fact]
    public void GarrisonEarthHitDoesNotOverwriteAValidLegionHitMark()
    {
        var setup = CombatTests.Battle(CombatTests.Unit(1, "earth", 50, garrison: false), CombatTests.Unit(1, "earth", 50), CombatTests.Unit(1, "wind", 50, garrison: false), CombatTests.Unit(2, "earth", 51));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        Assert.Equal(1, world.Metrics.Reactions[(int)ReactionKind.Sandstorm]);
    }

    [Theory]
    [InlineData(false, false, false, 1, 0.599999, true)]
    [InlineData(false, false, false, 1, 0.6, false)]
    [InlineData(false, false, true, 1, 0.799999, true)]
    [InlineData(false, false, true, 1, 0.8, false)]
    [InlineData(true, false, false, 1, 0, false)]
    [InlineData(false, true, false, 1, 0, false)]
    [InlineData(false, false, false, 0, 0, false)]
    public void ConversionBoundaryAndForbiddenCases(bool converted, bool tower, bool neutral, int space, double roll, bool expected)
    {
        Assert.Equal(expected, ConversionRules.ShouldConvert(GameRulesTests.Rules(), converted, tower, neutral, space, roll));
    }

    [Fact]
    public void ConversionRestoresHealthKeepsElementAndActsOnNextTick()
    {
        var world = new SimulationWorld(CertainConversion(), CombatTests.Battle(CombatTests.Unit(1, "water", 50), CombatTests.Unit(2, "earth", 51, hp: 1), CombatTests.Unit(2, "life", 52)), 1);
        world.Step();
        var converted = CombatTests.Find(world, 2);
        Assert.Equal(1, converted.OwnerId);
        Assert.Equal(GameRulesTests.Rules().ElementIndex("earth"), converted.Element);
        Assert.Equal(160, converted.Health);
        Assert.True(converted.Converted);
        Assert.Equal(1, world.Metrics.Attacks);
        world.Step();
        Assert.True(world.Metrics.Attacks >= 2);
    }

    [Fact]
    public void ConvertedVictimDropsUndiscountedFlooredCoresOnlyOnce()
    {
        var world = new SimulationWorld(CertainConversion(), CombatTests.Battle(CombatTests.Unit(1, "water", 100), CombatTests.Unit(2, "decay", 101, hp: 1, converted: true)), 1);
        world.Step();
        Assert.Equal(1, world.Metrics.Deaths);
        Assert.Equal(1, world.Metrics.Drops);
        Assert.Equal(0, world.Metrics.Conversions);
        Assert.Equal(8, Assert.Single(world.GetView(1).Cores).Amount);
        world.Step();
        Assert.Equal(1, world.Metrics.Drops);
    }

    [Fact]
    public void CorePickupUsesDistanceThenPlayerIdAndExpiryPreventsLatePickup()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(800, 800), new Vector2(50, 50)));
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200, 200), new Vector2(50, 50)));
        setup.Cores.Add(new CoreSpawn(new Vector2(50, 50), 8));
        setup.Cores.Add(new CoreSpawn(new Vector2(1500, 1500), 5));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        world.Step();
        Assert.Equal(8, world.GetView(1).CoreBalance);
        Assert.Equal(0, world.GetView(2).CoreBalance);
        for (int i = 0; i < 1200; i++) world.Step();
        Assert.Equal(0, world.Metrics.DroppedCores);
        Assert.Equal(1, world.Metrics.Pickups);
    }

    [Fact]
    public void LethalHitOwnsConversionAndLaterQueuedHitsCannotOverwriteIt()
    {
        var setup = CombatTests.Battle(CombatTests.Unit(1, "water", 50), CombatTests.Unit(2, "lightning", 60), CombatTests.Unit(3, "earth", 55, hp: 12), CombatTests.Unit(1, "wind", 50, 49));
        setup.Players.Add(new PlayerSpawn(3, "water", new Vector2(1200, 1200), new Vector2(1200, 1200), 4));
        var world = new SimulationWorld(CertainConversion(), setup, 1);
        world.Step();
        Assert.Equal(2, CombatTests.Find(world, 3, 2).OwnerId);
        Assert.Equal(2, world.Metrics.Attacks);
        Assert.Equal(1, world.Metrics.Conversions);
        Assert.Equal(1, world.Metrics.Deaths);
    }

    [Fact]
    public void OwnedDotDoesNotRevealADeathOutsideCurrentVision()
    {
        var world = new SimulationWorld(GameRulesTests.Rules(), CombatTests.Battle(CombatTests.Unit(1, "fire", 50, hp: 1), CombatTests.Unit(2, "earth", 51, hp: 10, converted: true)), 1);
        for (int tick = 0; tick < 40; tick++)
        {
            world.Step();
            Assert.DoesNotContain(world.GetView(1).Events, item => item.Kind is SimEventKind.Death or SimEventKind.CoreDropped);
        }
        Assert.True(world.Metrics.Deaths >= 2);
    }

    [Fact]
    public void SameTickDeathsFreeCapacityBeforeConversion()
    {
        var setup = CombatTests.Battle(CombatTests.Unit(1, "fire", 50), CombatTests.Unit(1, "life", 50, 51.5f, hp: 1), CombatTests.Unit(2, "earth", 51, hp: 1), CombatTests.Unit(3, "gravity", 51, 51.5f));
        setup.Players[0] = new PlayerSpawn(1, "water", new Vector2(300, 300), new Vector2(50, 50), 1);
        setup.Players.Add(new PlayerSpawn(3, "water", new Vector2(1200, 1200), new Vector2(1200, 1200), 4));
        for (int i = 0; i < 18; i++) setup.Units.Add(CombatTests.Unit(1, "earth", 500 + i, 500));
        var world = new SimulationWorld(CertainConversion(), setup, 1);
        world.Step();
        Assert.Equal(20, world.GetView(1).Population);
        Assert.Equal(1, CombatTests.Find(world, 3).OwnerId);
    }
}
