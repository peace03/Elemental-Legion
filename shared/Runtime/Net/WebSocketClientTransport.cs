using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ElementalLegion.Shared.Net
{
    internal sealed class WebSocketClientTransport : IDisposable
    {
        internal const int MaxPacketBytes = 1024;
        internal const int MaxPendingBytes = 1024 * 1024;
        private const int MaxPendingPackets = 4096;
        private const double QueueTimeoutSeconds = 2;
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly SemaphoreSlim sendSignal = new SemaphoreSlim(0);
        private readonly object gate = new object();
        private readonly Queue<(byte[] Data, long Queued)> incoming = new Queue<(byte[], long)>();
        private readonly Queue<(byte[] Data, long Queued)> outgoing = new Queue<(byte[], long)>();
        private int incomingBytes, outgoingBytes;
        private string failure;
        private bool disposed, finished;

        internal WebSocketClientTransport(Uri endpoint, byte[] ticket) => _ = Run(endpoint, (byte[])ticket.Clone());

        internal string Failure
        {
            get { lock (gate) return failure; }
        }

        internal void Send(byte[] data)
        {
            lock (gate)
            {
                if (disposed || finished || failure != null) return;
                if (data.Length == 0 || data.Length > MaxPacketBytes ||
                    outgoingBytes + data.Length > MaxPendingBytes || outgoing.Count >= MaxPendingPackets)
                {
                    failure = "전송 대기 한도를 초과했습니다.";
                    stop.Cancel();
                    return;
                }
                outgoing.Enqueue((data, Stopwatch.GetTimestamp()));
                outgoingBytes += data.Length;
                sendSignal.Release();
            }
        }

        internal bool TryReceive(out byte[] data)
        {
            lock (gate)
            {
                data = null;
                if (incoming.Count == 0) return false;
                var packet = incoming.Dequeue();
                incomingBytes -= packet.Data.Length;
                if (Expired(packet.Queued))
                {
                    failure = "수신 대기 시간이 초과됐습니다.";
                    if (!finished) stop.Cancel();
                    return false;
                }
                data = packet.Data;
                return true;
            }
        }

        private async Task Run(Uri endpoint, byte[] ticket)
        {
            try
            {
                await socket.ConnectAsync(endpoint, stop.Token).ConfigureAwait(false);
                await socket.SendAsync(new ArraySegment<byte>(ticket), WebSocketMessageType.Binary, true, stop.Token).ConfigureAwait(false);
                Array.Clear(ticket, 0, ticket.Length);
                Task sender = SendLoop();
                Task receiver = ReceiveLoop();
                try { await (await Task.WhenAny(sender, receiver).ConfigureAwait(false)).ConfigureAwait(false); }
                finally
                {
                    stop.Cancel();
                    await Task.WhenAll(sender, receiver).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Fail("보안 연결을 유지하지 못했습니다."); }
            finally
            {
                Array.Clear(ticket, 0, ticket.Length);
                lock (gate)
                {
                    failure ??= "온라인 연결이 종료됐습니다.";
                    finished = true;
                    socket.Dispose();
                    stop.Dispose();
                    sendSignal.Dispose();
                }
            }
        }

        private async Task SendLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                await sendSignal.WaitAsync(stop.Token).ConfigureAwait(false);
                (byte[] Data, long Queued) packet;
                lock (gate)
                {
                    packet = outgoing.Dequeue();
                    outgoingBytes -= packet.Data.Length;
                }
                if (Expired(packet.Queued)) { Fail("전송 대기 시간이 초과됐습니다."); return; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(QueueTimeoutSeconds));
                await socket.SendAsync(new ArraySegment<byte>(packet.Data), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[MaxPacketBytes];
            while (!stop.IsCancellationRequested)
            {
                int length = 0;
                WebSocketReceiveResult part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(QueueTimeoutSeconds));
                while (true)
                {
                    if (part.MessageType == WebSocketMessageType.Close)
                    {
                        Fail(CloseMessage(part.CloseStatusDescription));
                        return;
                    }
                    length += part.Count;
                    if (part.MessageType != WebSocketMessageType.Binary ||
                        (part.EndOfMessage ? length == 0 : length == buffer.Length))
                    {
                        Fail("잘못된 온라인 패킷을 받았습니다.");
                        return;
                    }
                    if (part.EndOfMessage) break;
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token).ConfigureAwait(false);
                }
                lock (gate)
                {
                    if (incomingBytes + length > MaxPendingBytes || incoming.Count >= MaxPendingPackets)
                    {
                        failure = "수신 대기 한도를 초과했습니다.";
                        return;
                    }
                    incoming.Enqueue((new ArraySegment<byte>(buffer, 0, length).ToArray(), Stopwatch.GetTimestamp()));
                    incomingBytes += length;
                }
            }
        }

        private static bool Expired(long queued) => Stopwatch.GetTimestamp() - queued > QueueTimeoutSeconds * Stopwatch.Frequency;

        private static string CloseMessage(string code) => code switch
        {
            "invalidTicket" => "입장권이 만료됐습니다. 다시 입장하세요.",
            "roomNotFound" => "방이 닫혔습니다.",
            "roomFull" => "방의 정원이 찼습니다.",
            "alreadyInRoom" => "이미 접속한 계정입니다.",
            "reentryUnavailable" => "이 방에 복귀할 수 없습니다.",
            _ => "온라인 연결이 종료됐습니다."
        };

        private void Fail(string message)
        {
            lock (gate) failure ??= message;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (!finished) stop.Cancel();
            }
        }
    }
}
