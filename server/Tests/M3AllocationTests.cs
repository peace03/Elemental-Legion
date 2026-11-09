using ElementalLegion.Shared.Sim;
using ElementalLegion.Server;
using Xunit;
using Xunit.Abstractions;

namespace ElementalLegion.Tests;

public sealed class M3AllocationTests
{
    private readonly ITestOutputHelper output;
    public M3AllocationTests(ITestOutputHelper output) { this.output = output; }
    [Fact]
    public void FirstDenseCombatTickDoesNotGrowObservationHistoryForEveryPlayer()
    {
        var rules=GameRulesTests.Rules();var setup=ScenarioFactory.Create(rules,rules.MaxPlayers,ScenarioKind.Clustered);
        var world=new SimulationWorld(rules,setup,1);long before=GC.GetAllocatedBytesForCurrentThread();world.Step();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        output.WriteLine($"Dense seed=1 first tick: {allocated} B, attacks={world.Metrics.Attacks}, deaths={world.Metrics.Deaths}");
        Assert.True(world.Metrics.Attacks>0);Assert.InRange(allocated,0,128*1024);
    }
}
