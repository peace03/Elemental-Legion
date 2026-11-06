using System.Text.Json;
using ElementalLegion.Bots;
using ElementalLegion.Shared.Net;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>진단 자료가 잘못되어도 실패 사유를 JSON 보고서에 남긴다.</summary>
public sealed class MigrationReportTests
{
    [Fact]
    public async Task InvalidTraceAfterRunnerFailureStillProducesReport()
    {
        string directory = Directory.CreateTempSubdirectory("migration-report-").FullName;
        try
        {
            string reportPath = Path.Combine(directory, "failed.json");
            File.WriteAllText(Path.Combine(directory, "failed.migration.bot01.1.ticks.ndjson"), "{}");
            var options = new LoadOptions(2, "127.0.0.1", 1, new Uri("https://127.0.0.1"), "unused",
                1, 0, 1, "baseline", false, reportPath, "unused", 1);
            using var report = new MemoryStream();
            int result = await MigrationLoadRunner.Run(options, GameRulesTests.Rules(),
                new NetworkSettings(GameRulesTests.Data()), report, CancellationToken.None);
            Assert.Equal(3, result);
            using JsonDocument json = JsonDocument.Parse(report.ToArray());
            Assert.False(json.RootElement.GetProperty("Passed").GetBoolean());
            Assert.True(json.RootElement.GetProperty("Errors").GetArrayLength() >= 2);
            Assert.Equal(0, json.RootElement.GetProperty("CompletedCycles").GetInt32());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
