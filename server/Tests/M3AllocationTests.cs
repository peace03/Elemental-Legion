using ElementalLegion.Shared.Sim;
using ElementalLegion.Server;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class M3AllocationTests
{
    [Fact]
    public void FirstDenseCombatTickDoesNotGrowObservationHistoryForEveryPlayer()
    {
        var rules=GameRulesTests.Rules();var setup=ScenarioFactory.Create(rules,rules.MaxPlayers,ScenarioKind.Clustered);
        var world=new SimulationWorld(rules,setup,1);long before=GC.GetAllocatedBytesForCurrentThread();world.Step();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(world.Metrics.Attacks>0);Assert.InRange(allocated,0,128*1024);
    }
}
