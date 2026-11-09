using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;

namespace ElementalLegion.Server;

/// <summary>소켓 작업과 방 틱 사이의 입력, 신뢰 출력, 최신 위치 묶음을 제한한다.</summary>
internal sealed class OnlinePeer(AccountIdentity account, int element)
{
    private const int MaxPendingBytes = 1024 * 1024;
    private static readonly TimeSpan MaxPendingAge = TimeSpan.FromSeconds(2);
    private readonly object outputGate = new();
    private readonly Queue<(byte[] Data, long At)> reliable = new();
    private readonly Channel<byte> outputReady = Channel.CreateBounded<byte>(1);
    private readonly CancellationTokenSource abort = new();
    private byte[]? pose;
    private byte[][]? units;
    private int pendingBytes;
    private bool finishing;
    private long sendingAt;

    public AccountIdentity Account { get; } = account;
    public Channel<byte[]> Input { get; } = Channel.CreateBounded<byte[]>(32);
    public bool Disconnected => abort.IsCancellationRequested;
    public int Element { get; set; } = element;
    public bool Ready { get; set; }
    public int ColorId { get; set; }
    public bool JoinFresh { get; init; }
    public bool UseOnlineFrames { get; set; }
    public int Owner { get; set; }
    public FogReplicationState? Fog { get; set; }
    public GameplayReplicationState? Gameplay { get; set; }
    public WorldView? LastView { get; set; }

    public void Send(NetDataWriter writer)
    {
        lock (outputGate)
        {
            if (finishing || Disconnected) return;
            if (pendingBytes + writer.Length > MaxPendingBytes)
            {
                OnlineGameServer.RecordRejected("slowConnection");
                Abort();
                return;
            }
            reliable.Enqueue((writer.CopyData(), Stopwatch.GetTimestamp()));
            pendingBytes += writer.Length;
            outputReady.Writer.TryWrite(0);
        }
    }

    public void ReplaceSnapshot(byte[]? latestPose, byte[][]? latestUnits)
    {
        lock (outputGate)
        {
            if (finishing || Disconnected) return;
            if (latestPose != null) pose = latestPose;
            if (latestUnits != null) units = latestUnits;
            outputReady.Writer.TryWrite(0);
        }
    }

    public bool IsSlow()
    {
        lock (outputGate)
            return (sendingAt != 0 && Stopwatch.GetElapsedTime(sendingAt) > MaxPendingAge) ||
                (reliable.TryPeek(out var next) && Stopwatch.GetElapsedTime(next.At) > MaxPendingAge);
    }

    public void Finish()
    {
        lock (outputGate)
        {
            finishing = true;
            pose = null;
            units = null;
            outputReady.Writer.TryWrite(0);
        }
    }

    public void Abort() => abort.Cancel();

    public async Task Run(WebSocket socket, int maxMessagesPerSecond, CancellationToken requestAborted)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(abort.Token, requestAborted);
        Task receive = Receive(socket, maxMessagesPerSecond, connection.Token);
        Task send = Send(socket, connection.Token);
        try { await Task.WhenAny(receive, send); }
        finally
        {
            await connection.CancelAsync();
            try { await Task.WhenAll(receive, send); }
            finally { Abort(); }
        }
    }

    private async Task Receive(WebSocket socket, int limit, CancellationToken cancellation)
    {
        long interval = Stopwatch.GetTimestamp();
        int received = 0;
        var buffer = new byte[Protocol.MaxDatagramBytes + (UseOnlineFrames ? OnlineFrame.HeaderBytes : 0) + 1];
        while (!cancellation.IsCancellationRequested)
        {
            int length = await ReceiveBinary(socket, buffer, cancellation);
            if (length <= 0) return;
            if (Stopwatch.GetElapsedTime(interval) >= TimeSpan.FromSeconds(1))
            {
                interval = Stopwatch.GetTimestamp();
                received = 0;
            }
            byte[] packet;
            if (UseOnlineFrames)
            {
                if (!OnlineFrame.TryRead(buffer, length, out ulong generation, out packet) || generation != 0)
                    throw new InvalidDataException("invalidPacket");
            }
            else packet = buffer[..length];
            if (++received > limit || !Input.Writer.TryWrite(packet))
                throw new InvalidDataException("rateLimited");
        }
    }

    private async Task Send(WebSocket socket, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            byte[]? packet = null;
            byte[][]? batch = null;
            bool isReliable = false;
            bool close = false;
            lock (outputGate)
            {
                if (reliable.TryDequeue(out var next))
                {
                    packet = next.Data;
                    sendingAt = next.At;
                    isReliable = true;
                }
                else if (pose != null)
                {
                    packet = pose;
                    pose = null;
                    sendingAt = Stopwatch.GetTimestamp();
                }
                else if (units != null)
                {
                    batch = units;
                    units = null;
                    sendingAt = Stopwatch.GetTimestamp();
                }
                else if (finishing)
                {
                    close = true;
                    sendingAt = Stopwatch.GetTimestamp();
                }
            }
            if (close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellation);
                return;
            }
            if (packet != null)
            {
                byte[] sent = UseOnlineFrames ? OnlineFrame.Wrap(0, packet) : packet;
                await socket.SendAsync(sent.AsMemory(), WebSocketMessageType.Binary, true, cancellation);
                if (isReliable) lock (outputGate) pendingBytes -= packet.Length;
            }
            else if (batch != null)
            {
                foreach (byte[] part in batch)
                    await socket.SendAsync((UseOnlineFrames ? OnlineFrame.Wrap(0, part) : part).AsMemory(), WebSocketMessageType.Binary, true, cancellation);
            }
            else { await outputReady.Reader.ReadAsync(cancellation); continue; }
            lock (outputGate) sendingAt = 0;
        }
    }

    public static async ValueTask<int> ReceiveBinary(WebSocket socket, Memory<byte> buffer, CancellationToken cancellation)
    {
        int length = 0;
        for (int fragments = 0; fragments < buffer.Length; fragments++)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer[length..], cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return 0;
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("invalidPacket");
            length += result.Count;
            if (length == buffer.Length) throw new InvalidDataException("packetTooLarge");
            if (result.EndOfMessage) return length;
        }
        throw new InvalidDataException("invalidPacket");
    }
}
