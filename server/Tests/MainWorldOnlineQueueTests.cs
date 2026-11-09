using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading.Channels;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>실제 WSS 작업자가 사용하는 제한 큐와 전송 중 수명을 검사한다.</summary>
public sealed class MainWorldOnlineQueueTests
{
    [Fact]
    public void ReliableOverflowAbortsAtOneMiBInsteadOfSilentlyAcceptingMore()
    {
        var peer = Peer();
        var writer = new NetDataWriter();
        writer.Put(new byte[Protocol.MaxDatagramBytes]);
        for (int index = 0; index < GameplayCodec.MaxBytes / writer.Length; index++) peer.Send(writer);
        Assert.False(peer.Disconnected);
        Assert.Equal(GameplayCodec.MaxBytes, peer.PendingBytes);
        peer.Send(writer);
        Assert.True(peer.Disconnected);
        peer.ReplaceSnapshot(new byte[] { 9 }, new[] { new byte[] { 10 } });
        peer.Send(writer);
        Assert.Equal(GameplayCodec.MaxBytes, peer.PendingBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WorkerKeepsReliableOrderAndOnlyTheLatestSnapshot(bool onlineFrames, bool finishWhileSending)
    {
        var peer = Peer();
        peer.UseOnlineFrames = onlineFrames;
        using var socket = new GatedSocket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writer = new NetDataWriter();
        writer.Put((byte)1); peer.Send(writer);
        Task worker = peer.Run(socket, 100, deadline.Token);
        try
        {
            await socket.Entered.Task.WaitAsync(deadline.Token);
            peer.ReplaceSnapshot(new byte[] { 2 }, new[] { new byte[] { 3 } });
            writer.Reset(); writer.Put((byte)6); peer.Send(writer);
            peer.ReplaceSnapshot(new byte[] { 4 }, new[] { new byte[] { 5 } });
            Assert.False(peer.IsSlow());
            typeof(OnlinePeer).GetField("sendingAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(peer, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3);
            Assert.True(peer.IsSlow(), "전송 중인 메시지도 지연 제한에 포함됩니다.");
            if (finishWhileSending) peer.Finish();
            socket.Release.TrySetResult();
            foreach (byte expected in finishWhileSending ? new byte[] { 1, 6 } : new byte[] { 1, 6, 4, 5 })
            {
                byte[] packet = await socket.Frames.Reader.ReadAsync(deadline.Token);
                if (onlineFrames)
                {
                    Assert.True(OnlineFrame.TryRead(packet, packet.Length, out ulong generation, out byte[] body));
                    Assert.Equal(0UL, generation); packet = body;
                }
                Assert.Equal(new[] { expected }, packet);
            }
            if (!finishWhileSending) peer.Finish();
            try { await worker.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            Assert.False(socket.Frames.Reader.TryRead(out _));
            Assert.Equal(WebSocketState.CloseSent, socket.State);
            Assert.True(peer.Disconnected);
            Assert.False(peer.IsSlow());
        }
        finally
        {
            peer.Abort(); socket.Release.TrySetResult();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    private static OnlinePeer Peer() => new(new AccountIdentity(Guid.NewGuid().ToString("N"), "큐 검사"), 0);

    private sealed class GatedSocket : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;
        private int sends;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(8);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellation)
        { state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellation)
        { state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellation)
        { await Task.Delay(Timeout.Infinite, cancellation); throw new OperationCanceledException(cancellation); }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken cancellation)
        {
            Assert.Equal(WebSocketMessageType.Binary, type); Assert.True(end);
            if (++sends == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellation); }
            Assert.True(Frames.Writer.TryWrite(buffer.ToArray()));
        }
    }
}
