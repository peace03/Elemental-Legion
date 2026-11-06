using ElementalLegion.Bots;
using ElementalLegion.Shared.Net;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>승계 부하 옵션과 기존 측정 옵션의 입력 경계를 확인한다.</summary>
public sealed class MigrationLoadOptionsTests
{
    [Fact]
    public void MigrationOptionsRequireInternetAndPreserveBaselineDefaults()
    {
        var settings = new NetworkSettings(GameRulesTests.Data());
        var baseline = LoadOptions.Parse(["--server-trace", "trace.ndjson"], 15, settings);
        Assert.Equal(0, baseline.MigrationCycles);
        Assert.Equal("graceful", baseline.MigrationMode);
        Assert.Throws<ArgumentException>(() => LoadOptions.Parse(["--server-trace", "trace.ndjson", "--migration-cycles", "1"], 15, settings));
        string executable = Path.GetTempFileName();
        try
        {
            var options = LoadOptions.Parse(["--relay", "https://relay.example", "--host-server", executable,
                "--migration-cycles", "10", "--migration-mode", "crash"], 15, settings);
            Assert.Equal(10, options.MigrationCycles);
            Assert.Equal("crash", options.MigrationMode);
        }
        finally { File.Delete(executable); }
    }

    [Theory]
    [InlineData("--migration-cycles", "101")]
    [InlineData("--migration-cycles", "-1")]
    [InlineData("--migration-mode", "invalid")]
    public void InvalidMigrationOptionsAreRejected(string option, string value)
    {
        var settings = new NetworkSettings(GameRulesTests.Data());
        Assert.Throws<ArgumentException>(() => LoadOptions.Parse(["--server-trace", "trace.ndjson", option, value], 15, settings));
    }
}
