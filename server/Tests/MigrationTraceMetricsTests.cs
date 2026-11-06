using System.Text.Json;
using System.Diagnostics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>승계 진단은 틱 표본과 분리하고 오류의 비밀값을 기록하지 않는다.</summary>
public sealed class MigrationTraceMetricsTests
{
    [Fact]
    public void TickAllocationMeasuresOnlyTheCurrentTickThread()
    {
        string directory = Directory.CreateTempSubdirectory("migration-tick-allocation-").FullName;
        try
        {
            string path = Path.Combine(directory, "server.ticks.ndjson");
            var world = new NetworkWorld(GameRulesTests.Rules(), new NetworkSettings(GameRulesTests.Data()));
            using (var trace = NetworkLoadTrace.Create(path, GameRulesTests.Rules(), "wss"))
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                byte[] payload = new byte[4096];
                trace.Record(world, Stopwatch.GetTimestamp(), allocated);
                GC.KeepAlive(payload);
            }
            using JsonDocument row = JsonDocument.Parse(File.ReadLines(path).Last());
            Assert.InRange(row.RootElement.GetProperty("AllocatedBytes").GetInt64(), 4096, 8192);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task BackupMetricsRetainLatestSnapshotAndDoNotChangeTickRows()
    {
        string directory = Directory.CreateTempSubdirectory("migration-metrics-").FullName;
        try
        {
            string path = Path.Combine(directory, "server.ticks.ndjson");
            using (var trace = NetworkLoadTrace.Create(path, GameRulesTests.Rules(), "wss"))
            {
                await trace.RecordBackup(1, 1, 4, [1, 2], 3, 256, 5);
                await trace.RecordBackup(1, 2, 7, [3, 4, 5], 4, 512, 6);
                trace.RecordFault(new InvalidDataException("secret-must-not-be-logged"), 1, 7);
            }
            Assert.Single(File.ReadAllLines(path));
            Assert.Equal(new byte[] { 3, 4, 5 }, File.ReadAllBytes(path + ".snapshot.br"));
            string[] metrics = File.ReadAllLines(path + ".migration.ndjson");
            Assert.Equal(2, metrics.Length);
            using JsonDocument row = JsonDocument.Parse(metrics[1]);
            Assert.Equal(3, row.RootElement.GetProperty("CompressedBytes").GetInt32());
            Assert.Equal(512, row.RootElement.GetProperty("EncodeAllocatedBytes").GetInt64());
            string fault = File.ReadAllText(path + ".fault.json");
            Assert.Contains(nameof(InvalidDataException), fault);
            Assert.DoesNotContain("secret-must-not-be-logged", fault);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
