using System.Numerics;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Data;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>M3가 전송할 자기 상태·관측 건물·전투 출처의 실제 시뮬레이션 계약을 검사한다.</summary>
public sealed class M3ObservationTests
{
    [Fact]
    public void OwnBuildingsTrainingAndCompositionAreReadOnlyObservations()
    {
        var rules=GameRulesTests.Rules();var setup=new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1,"water",new Vector2(100),new Vector2(120),3,100,true,true));
        setup.Players.Add(new PlayerSpawn(2,"fire",new Vector2(1400),new Vector2(1400)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(120),garrison:true));
        var world=new SimulationWorld(rules,setup,1);var view=world.GetView(1);
        Assert.Equal(3,view.Buildings.Count);Assert.All(view.Buildings,b=>Assert.Equal(1,b.OwnerId));
        Assert.Equal(3,view.Buildings.Select(b=>b.Id).Distinct().Count());
        Assert.Equal(1,view.GarrisonCount);Assert.Equal(1,view.ElementCounts[rules.ElementIndex("water")]);
        Assert.True(view.CanSeePoint(new Vector2(120)));Assert.False(view.CanSeePoint(new Vector2(1400)));
        world.TryEnqueue(1,SimCommand.Train(1,"water"));world.Step();
        Assert.Equal(rules.ElementIndex("water"),view.TrainingElement);Assert.Equal(1,view.TrainingStartTick);
        Assert.Equal(1+(long)(rules.TrainingSeconds(view.TrainingElement,view.MainElement)*rules.TickRate),view.TrainingEndTick);
        Assert.Equal(0,view.TrainedCount);Assert.Equal(0,view.ConvertedCount);
        for(int i=0;i<(int)(rules.NatureTrainSeconds*rules.TickRate);i++)world.Step();
        Assert.Equal(1,view.TrainedCount);Assert.Equal(2,view.Population);
    }

    [Fact]
    public void HealingHasConfirmedAmountAndObservedSourcePosition()
    {
        var rules=GameRulesTests.Rules();var setup=new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1,"water",new Vector2(1000),new Vector2(100)));
        setup.Units.Add(new UnitSpawn(1,"life",new Vector2(100)));
        setup.Units.Add(new UnitSpawn(1,"water",new Vector2(101,100),health:1));
        var world=new SimulationWorld(rules,setup,1);world.Step();var view=world.GetView(1);
        var heal=Assert.Single(view.Events,e=>e.Kind==SimEventKind.Healed);
        Assert.True(heal.Amount>0);Assert.True(heal.HasSourcePosition);
        Assert.Equal(rules.ElementIndex("life"),heal.Element);
        Assert.Equal(view.Units.Single(u=>u.Id==heal.SourceId).Position,heal.SourcePosition);
    }

    [Fact]
    public void DroppedCoreEventReferencesActualCoreRatherThanVictim()
    {
        var data=GameRulesTests.Data();data.Root["economy"]!["conversionChance"]=0;
        var rules=new GameRules(new GameDataDocument(data.Root.ToString()));var setup=CombatTests.Battle(
            CombatTests.Unit(1,"gravity",50),CombatTests.Unit(2,"fire",52,hp:1));
        var world=new SimulationWorld(rules,setup,1);world.Step();var view=world.GetView(1);
        var drop=Assert.Single(view.Events,e=>e.Kind==SimEventKind.CoreDropped);
        Assert.Equal(ObservedEntityKind.Core,drop.EntityKind);
        Assert.Contains(view.Cores,core=>core.Id==drop.EntityId);Assert.NotEqual(drop.TargetId,drop.EntityId);
    }
}
