using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>콘솔 인수·통계와 실제 부하 시나리오의 실행 경계를 확인한다.</summary>
public sealed class ServerTests
{
    [Theory]
    [InlineData("--bots", "0")]
    [InlineData("--bots", "16")]
    [InlineData("--bots", "bad")]
    [InlineData("--bots")]
    [InlineData("--report-tick")]
    [InlineData("--unknown")]
    [InlineData("--bots", "1", "--bots", "2")]
    [InlineData("--bots", "1", "--report-tick", "--report-tick")]
    public void InvalidOptionsAreRejected(params string[] args) => Assert.Throws<ArgumentException>(() => ServerOptions.Parse(args, 15));

    [Fact]
    public void OptionsAcceptBothOrdersAndNoArgumentsKeepEnvironmentCheck()
    {
        Assert.Equal(0, ServerOptions.Parse([], 15).Bots);
        Assert.True(ServerOptions.Parse(["--report-tick", "--bots", "15"], 15).ReportTick);
        Assert.Equal(15, ServerOptions.Parse(["--bots", "15", "--report-tick"], 15).Bots);
    }

    [Fact]
    public void StatisticsUseNearestRankAndRejectInvalidSamples()
    {
        var samples = Enumerable.Range(1, 100).Select(value => (long)value).Reverse().ToArray();
        var statistics = TickStatistics.Calculate(samples, 1000);
        Assert.Equal(50.5, statistics.MeanMs);
        Assert.Equal(99, statistics.P99Ms);
        Assert.Equal(100, statistics.MaxMs);
        Assert.Equal(50, statistics.OverBudgetCount);
        Assert.Throws<ArgumentException>(() => TickStatistics.Calculate([], 1000));
        Assert.Throws<ArgumentException>(() => TickStatistics.Calculate([-1], 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => TickStatistics.Calculate([1], double.NaN));
    }

    [Theory]
    [InlineData(ScenarioKind.Distributed)]
    [InlineData(ScenarioKind.Clustered)]
    [InlineData(ScenarioKind.Continuous)]
    public void MaximumScenarioHas750Players238NeutralsAndEveryElement(ScenarioKind kind)
    {
        var rules = GameRulesTests.Rules();
        WorldSetup setup = ScenarioFactory.Create(rules, 15, kind);
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(750, world.Metrics.PlayerUnits);
        Assert.Equal(238, world.Metrics.NeutralUnits);
        Assert.Equal(14, setup.Units.Select(unit => unit.Element).Distinct().Count());
        Assert.Equal(3, rules.StartUnitCount);
        Assert.All(setup.Units.Where(unit => unit.OwnerId < 0 && rules.Elements[rules.ElementIndex(unit.Element)].Group == ElementGroup.Energy), unit =>
            Assert.InRange(System.Numerics.Vector2.Distance(unit.Position, new System.Numerics.Vector2(rules.MapSize * 0.5f)), 0, rules.VisionRadius));
    }

    [Fact]
    public void LiveScenarioRetainsStartPopulationAndDataBalance()
    {
        var rules = GameRulesTests.Rules();
        var setup = ScenarioFactory.Create(rules, 2, ScenarioKind.Distributed, false);
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(6, world.Metrics.PlayerUnits);
        Assert.Equal(rules.StartingCores, world.GetView(1).CoreBalance);
    }

    [Fact]
    public void ContinuousBotsKeepPopulationBalancesAndVisibleEntityIdsConsistent()
    {
        var rules = GameRulesTests.Rules();
        var setup = ScenarioFactory.Create(rules, 2, ScenarioKind.Continuous);
        var world = new SimulationWorld(rules, setup, 1);
        var bots = setup.Players.Select(player => new BotBrain(rules, player.Id)).ToArray();
        for (int tick = 0; tick < 1200; tick++)
        {
            for (int index = 0; index < bots.Length; index++)
            {
                SimCommand? command = bots[index].Decide(world.GetView(setup.Players[index].Id));
                if (command.HasValue) Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(setup.Players[index].Id, command.Value));
            }
            world.Step();
            int total = 0;
            foreach (var player in setup.Players)
            {
                var view = world.GetView(player.Id);
                Assert.InRange(view.Population + view.ReservedPopulation, 0, view.PopulationCap);
                Assert.InRange(view.CoreBalance, 0, long.MaxValue);
                Assert.Equal(view.Population, view.Units.Count(unit => unit.OwnerId == player.Id));
                Assert.Equal(view.Units.Count, view.Units.Select(unit => unit.Id).Distinct().Count());
                Assert.All(view.Units, unit => Assert.InRange(unit.Health, float.Epsilon, unit.MaxHealth));
                total += view.Population;
            }
            Assert.Equal(total, world.Metrics.PlayerUnits);
        }
        Assert.True(world.Metrics.Deaths > 0);
        Assert.True(world.Metrics.Trained > 0);
    }

    [Fact]
    public void SmallReportUsesRealTicksAndPublishesLoadAndActivity()
    {
        var writer = new StringWriter();
        var results = BenchmarkRunner.Run(GameRulesTests.Rules(), 2, writer, CancellationToken.None, 2, 10);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal(10, result.Samples));
        Assert.Equal(100, results[0].MinStartPlayerUnits);
        Assert.Equal(238, results[0].MinStartNeutralUnits);
        Assert.True(results[1].Attacks > 0);
        Assert.Contains("p99", writer.ToString());
        Assert.Contains("SHA-256", writer.ToString());
    }
}
