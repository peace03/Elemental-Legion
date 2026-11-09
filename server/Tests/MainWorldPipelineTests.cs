using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class MainWorldPipelineTests
{
    [Fact]
    public void FifteenOwnersUseActualBoundedQueuesAndDueSnapshotCaptures()
    {
        var json = GameRulesTests.Data().Root;
        json["server"]!["hostMigration"]!["backupSeconds"] = 0.1;
        json["server"]!["saveIntervalSeconds"] = 1;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        var setup = ScenarioFactory.Create(rules, 15, ScenarioKind.MainWorldDense);
        var world = new SimulationWorld(rules, setup, 1);
        var result = new MainWorldPipelineResult { BudgetMs = 50 };
        using var pipeline = new MainWorldPipeline(rules, settings, setup, result);
        var brains = setup.Players.Select(player => new BotBrain(rules, player.Id, true)).ToArray();
        world.Step();
        pipeline.Tick(world, brains, 20);
        Assert.Equal(15, result.OwnersMeasured);
        Assert.Equal(15, result.OwnerFrames);
        Assert.True(result.ReliablePackets > 0);
        Assert.True(result.SnapshotChunks > 0);
        Assert.InRange(result.MaxPacketBytes, 1, 1024);
        Assert.InRange(result.MaxPendingBytes, 1, 1024 * 1024);
        Assert.Equal(0, result.QueueFailures);
        Assert.Equal(1, result.MigrationCaptures);
        Assert.Equal(1, result.CheckpointCaptures);
        Assert.True(result.CaptureAllocatedBytes > 0);
        Assert.Equal(result.QueuedBytes, result.DrainedBytes);
        pipeline.Drain();
        Assert.Equal(result.QueuedBytes, result.DrainedBytes);
    }

    [Fact]
    public void ServerBudgetCannotPassWhenTheSameSampleExceedsFiftyMilliseconds()
    {
        var result = new MainWorldPipelineResult { BudgetMs = 50, Statistics = TickStatistics.Calculate([51], 1000, 50) };
        Assert.False(result.Passed);
        result.Statistics = TickStatistics.Calculate([49], 1000, 50);
        Assert.True(result.Passed);
        result.QueueFailures = 1;
        Assert.False(result.Passed);
    }

    [Fact]
    public void SelectedRunnerMeasuresPipelineOnTheSameSamplesWithoutChangingM0()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        using var output = new StringWriter();
        var results = BenchmarkRunner.Run(rules, new NetworkSettings(data), 2, ScenarioKind.MainWorldDense, output, CancellationToken.None, 0, 4);
        Assert.Equal(3, results.Count);
        Assert.All(results, result =>
        {
            Assert.NotNull(result.Pipeline);
            Assert.Equal(50, result.Pipeline.BudgetMs);
            Assert.True(result.Pipeline.Statistics!.P99Ms >= result.Statistics.P99Ms);
            Assert.Equal(result.Pipeline.QueuedBytes, result.Pipeline.DrainedBytes);
            Assert.Equal(0, result.Pipeline.QueueFailures);
        });
        Assert.Contains("server tick-boundary pipeline", output.ToString());
        Assert.Contains("measured=4/4", output.ToString());
        Assert.DoesNotContain("full-server replication/send-queue=not measured", output.ToString());
    }
}
