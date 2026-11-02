using ElementalLegion.Shared.Net;

namespace ElementalLegion.Server;

/// <summary>한 연결의 페이로드와 LiteNetLib 전송 통계를 분리한 읽기 전용 표본이다.</summary>
public sealed class NetworkTrafficReport
{
    private readonly long[] sentByKind;
    public int PlayerId { get; }
    public double ElapsedSeconds { get; }
    public long AppSentBytes { get; }
    public long AppReceivedBytes { get; }
    public long AppPacketsSent { get; }
    public long AppPacketsReceived { get; }
    public long InitialSyncSentBytes { get; }
    public long SteadySentBytes => AppSentBytes - InitialSyncSentBytes;
    public double AverageSteadySentKiBPerSecond => SteadySentBytes / Math.Max(ElapsedSeconds, double.Epsilon) / 1024;
    public double PeakSteadySentKiBPerSecond { get; }
    public long TransportSentBytes { get; }
    public long TransportReceivedBytes { get; }
    public long TransportPacketsSent { get; }
    public long TransportPacketsReceived { get; }
    public double AverageSentKiBPerSecond => AppSentBytes / Math.Max(ElapsedSeconds, double.Epsilon) / 1024;
    public double AverageReceivedKiBPerSecond => AppReceivedBytes / Math.Max(ElapsedSeconds, double.Epsilon) / 1024;
    public double AverageTransportSentKiBPerSecond => TransportSentBytes / Math.Max(ElapsedSeconds, double.Epsilon) / 1024;
    public double AverageTransportReceivedKiBPerSecond => TransportReceivedBytes / Math.Max(ElapsedSeconds, double.Epsilon) / 1024;
    public double PeakSentKiBPerSecond { get; }
    public double PeakReceivedKiBPerSecond { get; }
    public double PeakTransportSentKiBPerSecond { get; }
    public double PeakTransportReceivedKiBPerSecond { get; }
    public long SentPayload(PacketKind kind) => sentByKind[(byte)kind];

    internal NetworkTrafficReport(int id, double seconds, long sent, long received, long packetsSent, long packetsReceived, long initial,
        long wireSent, long wireReceived, long wirePacketsSent, long wirePacketsReceived, long peakSent, long peakReceived, long peakWireSent, long peakWireReceived, long peakSteady, long[] kinds)
    {
        PlayerId=id; ElapsedSeconds=seconds; AppSentBytes=sent; AppReceivedBytes=received; AppPacketsSent=packetsSent; AppPacketsReceived=packetsReceived;
        InitialSyncSentBytes=initial; TransportSentBytes=wireSent; TransportReceivedBytes=wireReceived; TransportPacketsSent=wirePacketsSent; TransportPacketsReceived=wirePacketsReceived;
        PeakSentKiBPerSecond=peakSent/1024.0; PeakReceivedKiBPerSecond=peakReceived/1024.0; PeakTransportSentKiBPerSecond=peakWireSent/1024.0; PeakTransportReceivedKiBPerSecond=peakWireReceived/1024.0;
        PeakSteadySentKiBPerSecond=peakSteady/1024.0;
        sentByKind=(long[])kinds.Clone();
    }

    public void Write(TextWriter output)
    {
        output.WriteLine(FormattableString.Invariant(
        $"network player={PlayerId} seconds={ElapsedSeconds:F2}; app tx/rx={AppSentBytes}/{AppReceivedBytes}B packets={AppPacketsSent}/{AppPacketsReceived}; initialSyncTx={InitialSyncSentBytes}B; steadyTx={SteadySentBytes}B avg/peak1s={AverageSteadySentKiBPerSecond:F3}/{PeakSteadySentKiBPerSecond:F3}KiB/s; avg tx/rx={AverageSentKiBPerSecond:F3}/{AverageReceivedKiBPerSecond:F3}KiB/s; peak1s tx/rx={PeakSentKiBPerSecond:F3}/{PeakReceivedKiBPerSecond:F3}KiB/s; LiteNetLib tx/rx={TransportSentBytes}/{TransportReceivedBytes}B packets={TransportPacketsSent}/{TransportPacketsReceived}; wireAvg={AverageTransportSentKiBPerSecond:F3}/{AverageTransportReceivedKiBPerSecond:F3}KiB/s wirePeak1s={PeakTransportSentKiBPerSecond:F3}/{PeakTransportReceivedKiBPerSecond:F3}KiB/s (UDP/IP headers excluded, retransmissions included)"));
        foreach(PacketKind kind in Enum.GetValues<PacketKind>())if(SentPayload(kind)>0)output.WriteLine($"  payload {kind}={SentPayload(kind)}B");
    }
}

/// <summary>명시한 경과 시간의 연결 시작 기준 1초 버킷을 집계한다. 패킷 경로에서는 출력·표본 객체를 만들지 않는다.</summary>
public sealed class NetworkTrafficCounter
{
    private readonly int playerId;
    private readonly long[] kinds = new long[256];
    private long sent, received, sentPackets, receivedPackets, initial;
    private long wireSent, wireReceived, wireSentPackets, wireReceivedPackets;
    private long bucket=-1, bucketSent, bucketReceived, bucketWireSent, bucketWireReceived, bucketSteady;
    private long peakSent, peakReceived, peakWireSent, peakWireReceived, peakSteady;
    private double lastTime;
    public NetworkTrafficCounter(int playerId) { this.playerId=playerId; }

    public void RecordSent(int bytes, PacketKind kind, double elapsed,bool initialSync=false)
    {
        if(bytes<0) throw new ArgumentOutOfRangeException(nameof(bytes)); Advance(elapsed);
        sent+=bytes; sentPackets++; kinds[(byte)kind]+=bytes;
        if(initialSync||kind==PacketKind.Welcome || kind==PacketKind.FogBaseline) initial+=bytes;
        else { bucketSteady+=bytes; peakSteady=Math.Max(peakSteady,bucketSteady); }
        bucketSent+=bytes; peakSent=Math.Max(peakSent,bucketSent);
    }

    public void RecordReceived(int bytes, double elapsed)
    {
        if(bytes<0) throw new ArgumentOutOfRangeException(nameof(bytes)); Advance(elapsed);
        received+=bytes; receivedPackets++; bucketReceived+=bytes; peakReceived=Math.Max(peakReceived,bucketReceived);
    }

    public void SampleTransport(long bytesSent, long bytesReceived, long packetsSent, long packetsReceived, double elapsed)
    {
        Advance(elapsed);
        bucketWireSent+=Math.Max(0,bytesSent-wireSent); bucketWireReceived+=Math.Max(0,bytesReceived-wireReceived);
        wireSent=bytesSent; wireReceived=bytesReceived; wireSentPackets=packetsSent; wireReceivedPackets=packetsReceived;
        peakWireSent=Math.Max(peakWireSent,bucketWireSent); peakWireReceived=Math.Max(peakWireReceived,bucketWireReceived);
    }

    public NetworkTrafficReport Snapshot(double elapsed)
    {
        Advance(elapsed);
        return new NetworkTrafficReport(playerId,elapsed,sent,received,sentPackets,receivedPackets,initial,wireSent,wireReceived,wireSentPackets,wireReceivedPackets,peakSent,peakReceived,peakWireSent,peakWireReceived,peakSteady,kinds);
    }

    private void Advance(double elapsed)
    {
        if(!double.IsFinite(elapsed) || elapsed<lastTime) throw new ArgumentOutOfRangeException(nameof(elapsed));
        lastTime=elapsed; long next=(long)Math.Floor(elapsed);
        if(next==bucket) return;
        bucket=next; bucketSent=0; bucketReceived=0; bucketWireSent=0; bucketWireReceived=0; bucketSteady=0;
    }
}
