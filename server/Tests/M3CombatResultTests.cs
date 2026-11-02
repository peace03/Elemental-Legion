using System.Numerics;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class M3CombatResultTests
{
    [Fact]
    public void SecondaryChainImpactIsConfirmedAtItsActualVictim()
    {
        var world=new SimulationWorld(GameRulesTests.Rules(),CombatTests.Battle(CombatTests.Unit(1,"lightning",50),CombatTests.Unit(2,"earth",52),CombatTests.Unit(2,"earth",54)),1);
        world.Step();var impact=Assert.Single(world.GetView(1).Events,e=>e.Kind==SimEventKind.Impact&&e.TargetId==3);
        Assert.Equal(new Vector2(54,50),impact.Position);Assert.Equal(4.8f,impact.Amount,3);Assert.Equal(1,impact.SourceId);
    }
    [Fact]
    public void LifestealReportsOnlyTheHealthActuallyRestored()
    {
        var world=new SimulationWorld(GameRulesTests.Rules(),CombatTests.Battle(CombatTests.Unit(1,"dark_matter",50,hp:50),CombatTests.Unit(2,"earth",51,hp:1,converted:true)),1);
        world.Step();var heal=Assert.Single(world.GetView(1).Events,e=>e.Kind==SimEventKind.Healed&&e.TargetId==1);
        Assert.Equal(0.2f,heal.Amount,3);Assert.Equal(new Vector2(50),heal.Position);
    }
}
