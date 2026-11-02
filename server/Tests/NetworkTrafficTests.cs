using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>명시적인 경과 시간으로 페이로드·전송량·초기 동기화·초당 최대 구간을 구분한다.</summary>
public sealed class NetworkTrafficTests
{
    [Fact]
    public void TrafficSeparatesWirePayloadAndInitialSyncUsingOneSecondBuckets()
    {
        var counter = new NetworkTrafficCounter(7);
        counter.RecordSent(1000, PacketKind.FogBaseline, 0.1);
        counter.RecordSent(24, PacketKind.Units, 0.2);
        counter.RecordReceived(17, 0.2);
        counter.SampleTransport(1100, 40, 3, 2, 0.2);
        counter.RecordSent(512, PacketKind.Units, 1.2);
        counter.SampleTransport(1700, 80, 5, 4, 1.2);
        var report = counter.Snapshot(2);
        Assert.Equal(7, report.PlayerId);
        Assert.Equal(1536, report.AppSentBytes); Assert.Equal(17, report.AppReceivedBytes);
        Assert.Equal(1000, report.InitialSyncSentBytes);
        Assert.Equal(536, report.SteadySentBytes);
        Assert.Equal(0.5, report.PeakSteadySentKiBPerSecond, 6);
        Assert.Equal(1700.0 / 2048, report.AverageTransportSentKiBPerSecond, 6);
        Assert.Equal(1700, report.TransportSentBytes); Assert.Equal(80, report.TransportReceivedBytes);
        Assert.Equal(0.75, report.AverageSentKiBPerSecond, 6);
        Assert.Equal(1, report.PeakSentKiBPerSecond, 6);
        Assert.Equal(536, report.SentPayload(PacketKind.Units));
        Assert.Equal(1100.0 / 1024, report.PeakTransportSentKiBPerSecond, 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => counter.RecordSent(-1, PacketKind.Units, 3));
    }
}
