using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>명시적인 데모 옵션만 배치·양성 준비를 바꾸며 실제 비용과 명령 권한을 유지하는지 검사한다.</summary>
public sealed class M3ServerTests
{
    [Fact]
    public void ExplicitNormalScenarioMatchesTheLauncherAndStillRequiresOneListenScenario()
    {
        Assert.Equal("normal",ServerOptions.Parse(new[]{"--listen","--scenario","normal"},15).Scenario);
        Assert.Throws<ArgumentException>(()=>ServerOptions.Parse(new[]{"--scenario","normal"},15));
        Assert.Throws<ArgumentException>(()=>ServerOptions.Parse(new[]{"--listen","--scenario","normal","--scenario","m3-demo"},15));
    }
    [Fact]
    public void DemoStartsAllTrainingFacilitiesWithoutChangingNormalSpawn()
    {
        var data=GameRulesTests.Data();var rules=GameRulesTests.Rules();var settings=new NetworkSettings(data);
        var normal=new NetworkWorld(rules,settings);normal.TryJoin(rules.StartingElements[0],out int normalId);
        Assert.Equal(0,normal.World.GetView(normalId).CoreBalance);Assert.False(normal.World.GetView(normalId).NatureForge);
        var demo=new NetworkWorld(rules,settings,"m3-demo");Assert.Equal(JoinFailure.None,demo.TryJoin(rules.StartingElements[0],out int player));
        var view=demo.World.GetView(player);Assert.True(view.NatureForge&&view.EnergyForge);
        Assert.True(rules.IsUnlocked(view.BaseLevel,"forge_energy"));
        Assert.Equal(Enumerable.Range(0,rules.Elements.Count).Sum(element=>rules.TrainingCost(element,view.MainElement)),view.CoreBalance);
        Assert.Equal(3,view.Buildings.Count);
        Assert.Equal(CommandAdmission.Accepted,demo.Command(player,SimCommand.Train(1,"life")));
        Assert.Equal(CommandAdmission.StaleSequence,demo.Move(player,1,System.Numerics.Vector2.UnitX));
        Assert.Equal(CommandAdmission.Accepted,demo.Command(player,SimCommand.Rally(2)));
        demo.Step();Assert.True(view.IsTraining);
    }

    [Fact]
    public void DemoAndGameplayReportsRequireListeningAndRejectUnknownOrDuplicateOptions()
    {
        var options=ServerOptions.Parse(new[]{"--listen","--scenario","m3-demo","--report-gameplay"},15);
        Assert.Equal("m3-demo",options.Scenario);Assert.True(options.ReportGameplay);
        Assert.Throws<ArgumentException>(()=>ServerOptions.Parse(new[]{"--scenario","m3-demo"},15));
        Assert.Throws<ArgumentException>(()=>ServerOptions.Parse(new[]{"--listen","--scenario","unknown"},15));
        Assert.Throws<ArgumentException>(()=>ServerOptions.Parse(new[]{"--report-gameplay"},15));
        Assert.Throws<ArgumentException>(()=>ServerOptions.Parse(new[]{"--listen","--report-gameplay","--report-gameplay"},15));
    }
}
