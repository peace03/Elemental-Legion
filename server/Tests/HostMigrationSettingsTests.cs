using ElementalLegion.Shared.Net;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>승계 통신 버전과 운영 기본값의 단일 원본을 확인한다.</summary>
public sealed class HostMigrationSettingsTests
{
    [Fact]
    public void MigrationDefaultsAndProtocolArePublishedTogether()
    {
        Assert.Equal(21, Protocol.Version);
        var settings = GameRulesTests.Data().Root["server"]!["hostMigration"];
        Assert.NotNull(settings);
        Assert.Equal(1, settings!["backupSeconds"]!.Value<double>());
        Assert.Equal(0.5, settings["heartbeatSeconds"]!.Value<double>());
        Assert.Equal(3, settings["lossSeconds"]!.Value<double>());
        Assert.Equal(5, settings["candidateSeconds"]!.Value<double>());
        Assert.Equal(10, settings["deadlineSeconds"]!.Value<double>());
        Assert.Equal(1, settings["noticeSeconds"]!.Value<double>());
    }
}
