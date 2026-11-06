using System.Text.Json;
using ElementalLegion.Bots;
using ElementalLegion.Shared.Net;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>틱 되돌림은 세대로 구분하고 같은 세대의 누락은 실패시킨다.</summary>
public sealed class MigrationTraceTests
{
    [Fact]
    public void RetiredEpochIncludesFinalFrozenCaptureBeyondLastClientPose()
    {
        string directory = Directory.CreateTempSubdirectory("migration-final-capture-").FullName;
        string prefix = Path.Combine(directory, "run");
        var rules = GameRulesTests.Rules();
        try
        {
            var files = new Dictionary<ulong, string> { [1] = prefix + ".bot01.1.ticks.ndjson", [2] = prefix + ".bot02.2.ticks.ndjson" };
            foreach (var file in files)
            {
                int[] ticks = file.Key == 1 ? [1, 2, 3] : [4, 5, 6];
                File.WriteAllLines(file.Value, new[] { JsonSerializer.Serialize(new { Protocol.Version, rules.DataHash, rules.TickRate, Transport = "wss" }) }
                    .Concat(ticks.Select(tick => JsonSerializer.Serialize(new { Tick = tick, Milliseconds = tick == 3 ? 25d : 1d,
                        Connected = 2, PlayerUnits = 6, NeutralUnits = 10, Attacks = 0, AllocatedBytes = 128 }))));
                File.WriteAllText(file.Value + ".migration.ndjson", JsonSerializer.Serialize(new
                {
                    Generation = file.Key, BackupNumber = 1UL, Tick = ticks[^1], CompressedBytes = 16,
                    EncodeMilliseconds = 1d, EncodeAllocatedBytes = 128L, TransferMilliseconds = 2d
                }) + Environment.NewLine);
            }
            var summary = MigrationTraceSummary.Read(prefix, rules, 0, [(1UL, 2L), (2UL, 5L)], files, files.Values.ToArray());
            Assert.Equal(5, summary.Samples);
            Assert.Equal(25, summary.MaxMs);
            Assert.Equal(16, summary.Backups.CompressedBytes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingObservedAndForeignGenerationFilesAreRejected(bool missing)
    {
        string directory = Directory.CreateTempSubdirectory("migration-trace-manifest-").FullName;
        string prefix = Path.Combine(directory, "run");
        var rules = GameRulesTests.Rules();
        try
        {
            File.WriteAllLines(prefix + ".bot01.1.ticks.ndjson", [
                JsonSerializer.Serialize(new { Protocol.Version, rules.DataHash, rules.TickRate, Transport = "wss" }),
                JsonSerializer.Serialize(new { Tick = 1, Milliseconds = 1d, Connected = 2, PlayerUnits = 6, NeutralUnits = 10, Attacks = 1 })]);
            if (!missing) File.Copy(prefix + ".bot01.1.ticks.ndjson", prefix + ".bot03.99.ticks.ndjson");
            var files = new Dictionary<ulong, string> { [1] = prefix + ".bot01.1.ticks.ndjson", [2] = prefix + ".bot02.2.ticks.ndjson" };
            Assert.Throws<InvalidDataException>(() => MigrationTraceSummary.Read(prefix, rules, 0,
                missing ? [(1UL, 1L), (2UL, 1L)] : [(1UL, 1L)], files, files.Values.ToArray()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DifferentGenerationsMayReuseTicksButGapsWithinOneFileAreRejected()
    {
        string directory = Directory.CreateTempSubdirectory("migration-trace-").FullName;
        string prefix = Path.Combine(directory, "run");
        var rules = GameRulesTests.Rules();
        try
        {
            Write(prefix + ".bot01.1.ticks.ndjson", [1, 2]);
            Write(prefix + ".bot02.2.ticks.ndjson", [1, 2]);
            var files = new Dictionary<ulong, string> { [1] = prefix + ".bot01.1.ticks.ndjson", [2] = prefix + ".bot02.2.ticks.ndjson" };
            var summary = MigrationTraceSummary.Read(prefix, rules, 0, [(1UL, 2L), (2UL, 2L)], files, files.Values.ToArray());
            Assert.Equal(4, summary.Samples);
            Assert.Equal(4, summary.AllocationSamples);
            Assert.Equal(128, summary.MeanAllocatedBytes);
            File.WriteAllText(files[1] + ".migration.ndjson", JsonSerializer.Serialize(new
            {
                Generation = 1UL, BackupNumber = 1UL, Tick = 2L, CompressedBytes = 1024,
                EncodeMilliseconds = 2d, EncodeAllocatedBytes = 2048L, TransferMilliseconds = 3d
            }) + Environment.NewLine);
            summary = MigrationTraceSummary.Read(prefix, rules, 0, [(1UL, 2L), (2UL, 2L)], files, files.Values.ToArray());
            Assert.Equal(1, summary.Backups.Samples);
            Assert.Equal(1024, summary.Backups.CompressedBytes);
            Assert.Equal(2048, summary.Backups.EncodeAllocatedBytes);
            Assert.Throws<InvalidDataException>(() => MigrationTraceSummary.Read(prefix, rules, 0,
                [(1UL, 2L)], files, files.Values.ToArray()));
            string standby = prefix + ".bot03.3.ticks.ndjson";
            Write(standby, []);
            summary = MigrationTraceSummary.Read(prefix, rules, 0, [(1UL, 2L), (2UL, 2L)], files,
                files.Values.Append(standby).ToArray());
            Assert.Equal(4, summary.Samples);
            File.Delete(standby);
            File.Delete(files[2]);
            Assert.Throws<InvalidDataException>(() => MigrationTraceSummary.Read(prefix, rules, 0,
                [(1UL, 2L), (2UL, 2L)], files, files.Values.ToArray()));
            Write(prefix + ".bot03.99.ticks.ndjson", [1, 2]);
            Assert.Throws<InvalidDataException>(() => MigrationTraceSummary.Read(prefix, rules, 0,
                [(1UL, 2L)], files, files.Values.ToArray()));
            File.Delete(prefix + ".bot03.99.ticks.ndjson");
            Write(prefix + ".bot02.2.ticks.ndjson", [1, 3]);
            Assert.Throws<InvalidDataException>(() => MigrationTraceSummary.Read(prefix, rules, 0,
                [(1UL, 2L), (2UL, 3L)], files, files.Values.ToArray()));
        }
        finally { Directory.Delete(directory, recursive: true); }

        void Write(string path, int[] ticks)
        {
            File.WriteAllLines(path, new[] { JsonSerializer.Serialize(new { Protocol.Version, rules.DataHash, rules.TickRate, Transport = "wss" }) }
                .Concat(ticks.Select(tick => JsonSerializer.Serialize(new { Tick = tick, Milliseconds = 1d,
                    Connected = 15, PlayerUnits = 45, NeutralUnits = 10, Attacks = tick, AllocatedBytes = 128 }))));
        }
    }
}
