using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using Microsoft.AspNetCore.Http;

namespace ElementalLegion.Server;

/// <summary>참가자의 연결을 유지하면서 세대별 서버 연결만 교체한다.</summary>
internal sealed partial class HostedRelay
{
    private sealed class HostSession(WebSocket socket, ulong generation, CancellationToken cancellation)
    {
        public WebSocket Socket { get; } = socket;
        public ulong Generation { get; } = generation;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        public bool Listed, Retiring;
        public long LastTick;
        public DateTimeOffset TickChangedAt;
        public long MessageWindow = Stopwatch.GetTimestamp();
        public int Messages;
    }

    private sealed class Candidate(Slot slot, byte[] proof, DateTimeOffset deadline, ulong generation)
    {
        public Slot Slot { get; } = slot;
        public byte[] Proof { get; } = proof;
        public DateTimeOffset Deadline { get; } = deadline;
        public ulong Generation { get; } = generation;
        public HostSession? Session;
        public bool DownloadAllowed;
        public TaskCompletionSource Connected { get; } = NewSignal();
        public TaskCompletionSource Restored = NewSignal();
        public TaskCompletionSource Activated = NewSignal();
    }

    private sealed class HostBridge(WebSocket socket, ulong generation)
    {
        public WebSocket Socket { get; } = socket;
        public ulong Generation { get; } = generation;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }

    private sealed record HostState(string? Kind, int? Count, bool? Running, long? Tick, ulong? Generation, bool? Final, ulong? BackupNumber);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task Play(OnlineTicket ticket, WebSocket player, int commandLimit, CancellationToken cancellation)
    {
        Room? room;
        Slot? slot = null;
        string? error;
        lock (gate)
        {
            error = rooms.TryGetValue(ticket.RoomId, out room) && IsLive(room, clock.GetUtcNow())
                ? CanPlay(room, ticket.Account.Id) : "roomNotFound";
            if (error == null)
            {
                string key = NewKey(out byte[] proof);
                slot = new Slot(ticket.Account, ticket.FreshElement, key, proof, clock.GetUtcNow() + PairTimeout)
                { Player = player, Order = ++room!.NextOrder };
                room.Players.Add(ticket.Account.Id, slot);
            }
        }
        if (error != null) { await OnlineGameServer.RejectSocket(player, error); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, room!.Ended.Token, slot!.Revoked.Token);
        try
        {
            await Invite(room, slot, lifetime.Token);
            await ReadPlayer(room, slot, commandLimit, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            Candidate? standby;
            bool hostLeaving, close;
            lock (gate)
            {
                room.Players.Remove(ticket.Account.Id);
                slot.Finished.TrySetResult();
                slot.Bridge?.Socket.Abort();
                standby = room.Standby?.Slot == slot ? room.Standby : null;
                if (standby != null) room.Standby = null;
                hostLeaving = room.Creator.Id == ticket.Account.Id && !room.Migrating && !room.Ended.IsCancellationRequested;
                close = room.CreatorPaired && room.Players.Count == 0;
            }
            standby?.Session?.Stop.Cancel();
            if (close) await CloseRoom(room, "roomClosed");
            else if (hostLeaving) await BeginMigration(room, true, ticket.Account.Id);
            else await PrepareCandidate(room);
        }
    }

    private async Task ReadPlayer(Room room, Slot slot, int limit, CancellationToken cancellation)
    {
        var buffer = new byte[OnlineFrame.MaxMessageBytes + 1];
        long window = Stopwatch.GetTimestamp();
        int received = 0;
        while (!cancellation.IsCancellationRequested)
        {
            int length = await OnlinePeer.ReceiveBinary(slot.Player!, buffer, cancellation);
            if (length == 0) return;
            if (Stopwatch.GetElapsedTime(window).TotalSeconds >= 1) { window = Stopwatch.GetTimestamp(); received = 0; }
            if (++received > limit) throw new InvalidDataException("rateLimited");
            if (!OnlineFrame.TryRead(buffer, length, out ulong generation, out byte[] payload)) throw new InvalidDataException("invalidPacket");
            var reader = new NetDataReader(payload);
            PacketKind kind = (PacketKind)payload[0];
            if (kind is PacketKind.HostMigration or PacketKind.HostStandbyOffer)
                throw new InvalidDataException("invalidPacket");
            bool current;
            lock (gate) current = generation == room.Generation;
            if (!current) continue;
            if (kind == PacketKind.HostCapability)
            {
                if (!HostMigrationProtocol.TryReadCapability(reader, out bool capable)) throw new InvalidDataException("invalidPacket");
                lock (gate) slot.Capable = capable;
                await PrepareCandidate(room);
                continue;
            }
            if (kind == PacketKind.HostLeave)
            {
                if (!HostMigrationProtocol.TryReadLeave(reader)) throw new InvalidDataException("invalidPacket");
                bool owner;
                lock (gate) { owner = room.Creator.Id == slot.Account.Id; if (owner) slot.Leaving = true; }
                if (owner) await BeginMigration(room, true, slot.Account.Id);
                continue;
            }
            HostBridge? bridge;
            lock (gate) bridge = room.Migrating ? null : slot.Bridge;
            if (bridge?.Generation != generation) continue;
            await bridge.SendGate.WaitAsync(cancellation);
            try { await bridge.Socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Binary, true, cancellation); }
            catch (WebSocketException) { await BeginMigration(room, false, null); }
            finally { bridge.SendGate.Release(); }
        }
    }

    public async Task Connect(HttpContext context, string id, bool bridge, CancellationToken stopping)
        => await AcceptServer(context, id, bridge ? "bridge" : "host", stopping);

    public async Task Standby(HttpContext context, string id, CancellationToken stopping)
        => await AcceptServer(context, id, "standby", stopping);

    private async Task AcceptServer(HttpContext context, string id, string role, CancellationToken stopping)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 426; return; }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = false });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        byte[] proof;
        try { proof = await Authenticate(socket, lifetime.Token); }
        catch (Exception error) when (error is InvalidDataException or OperationCanceledException or WebSocketException)
        { await OnlineGameServer.RejectSocket(socket, "invalidTicket"); return; }
        try
        {
            if (role == "bridge") await ConnectBridge(id, socket, proof, lifetime.Token);
            else await ConnectControl(id, socket, proof, role == "standby", lifetime.Token);
        }
        catch (InvalidDataException error) { await OnlineGameServer.RejectSocket(socket, SafeHostClose(error.Message)); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { OnlineGameServer.RecordRejected("socketClosed"); }
    }

    private static async Task<byte[]> Authenticate(WebSocket socket, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(PairTimeout);
        byte[] key = new byte[KeyBytes + 1];
        try
        {
            int length = await OnlinePeer.ReceiveBinary(socket, key, timeout.Token);
            if (length != KeyBytes) throw new InvalidDataException("invalidTicket");
            return SHA256.HashData(key.AsSpan(0, length));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task ConnectControl(string id, WebSocket socket, byte[] proof, bool standby, CancellationToken cancellation)
    {
        Room? room;
        Candidate? candidate = null;
        HostSession? session = null;
        lock (gate)
        {
            if (rooms.TryGetValue(id, out room) && !room.Ended.IsCancellationRequested)
            {
                if (standby)
                {
                    candidate = room.Standby;
                    if (candidate != null && candidate.Session == null && candidate.Deadline > clock.GetUtcNow() &&
                        !candidate.Slot.Finished.Task.IsCompleted && accounts.IsActive(candidate.Slot.Account.Id) &&
                        CryptographicOperations.FixedTimeEquals(proof, candidate.Proof))
                        candidate.Session = session = new HostSession(socket, candidate.Generation, cancellation);
                }
                else if (room.HostProof != null && room.Deadline > clock.GetUtcNow() && accounts.IsActive(room.Creator.Id) &&
                    CryptographicOperations.FixedTimeEquals(proof, room.HostProof))
                {
                    room.HostProof = null;
                    room.Active = session = new HostSession(socket, room.Generation, cancellation);
                }
            }
        }
        if (room == null || session == null) { await OnlineGameServer.RejectSocket(socket, "invalidTicket"); return; }
        session.TickChangedAt = clock.GetUtcNow();
        Task monitor = WatchRoom(room, session);
        try
        {
            AccountIdentity owner = candidate?.Slot.Account ?? room.Creator;
            await Control(session, new { kind = "ready", room.Info.Title, room.Info.MinimumPlayers, room.Info.Capacity,
                creatorId = owner.Id, creatorNickname = owner.Nickname, version = Protocol.Version,
                dataHash = rules.DataHash, generation = session.Generation, standby }, session.Stop.Token);
            while (!session.Stop.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.MigrationLossSeconds));
                byte[] bytes = await ReadText(socket, 512, timeout.Token);
                if (Stopwatch.GetElapsedTime(session.MessageWindow).TotalSeconds >= 1)
                { session.MessageWindow = Stopwatch.GetTimestamp(); session.Messages = 0; }
                if (++session.Messages > 20) throw new InvalidDataException("rateLimited");
                HostState state;
                try { state = JsonSerializer.Deserialize<HostState>(bytes, StateJson) ?? throw new InvalidDataException("invalidPacket"); }
                catch (JsonException) { throw new InvalidDataException("invalidPacket"); }
                if (state.Generation.HasValue && state.Generation != session.Generation) throw new InvalidDataException("invalidPacket");
                if (state.Kind == "standbyReady") { candidate?.Connected.TrySetResult(); continue; }
                if (state.Kind == "readyForActivation") { candidate?.Restored.TrySetResult(); continue; }
                if (state.Kind == "checkpoint")
                {
                    lock (gate)
                        if (state.Final == true && room.BackupState?.Generation == session.Generation &&
                            room.BackupState.BackupNumber == state.BackupNumber && room.BackupState.Tick == state.Tick)
                            room.FinalBackup.TrySetResult();
                    continue;
                }
                if (state.Count is not int count || state.Running is not bool running || count < 0 || count > room.Info.Capacity)
                    throw new InvalidDataException("invalidPacket");
                bool active;
                lock (gate)
                {
                    active = room.Active == session && room.Generation == session.Generation;
                    if (active)
                    {
                        room.LastState = clock.GetUtcNow();
                        if (running && !room.Info.Running || state.Tick.HasValue && state.Tick != session.LastTick)
                            session.TickChangedAt = room.LastState;
                        room.Info = room.Info with { Count = count, Running = running };
                        session.LastTick = state.Tick ?? session.LastTick;
                    }
                }
                if (active && !session.Listed)
                {
                    session.Listed = true;
                    await Control(session, new { kind = "listed" }, session.Stop.Token);
                }
                if (candidate != null && active) candidate.Activated.TrySetResult();
                if (active) await PrepareCandidate(room);
            }
        }
        finally
        {
            session.Stop.Cancel();
            await monitor;
            bool failed;
            lock (gate)
            {
                failed = room.Active == session && !session.Retiring && !room.Ended.IsCancellationRequested;
                if (failed && room.MigrationTask is { IsCompleted: false })
                {
                    room.Migrating = true;
                    room.FinalBackup.TrySetCanceled();
                }
                if (room.Standby?.Session == session && room.Active != session)
                {
                    room.Standby.Slot.Capable = false;
                    room.Standby = null;
                }
            }
            if (failed) await BeginMigration(room, false, null);
        }
    }

    private async Task WatchRoom(Room room, HostSession session)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.MigrationHeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(session.Stop.Token))
            {
                Slot[] players;
                bool expired;
                lock (gate)
                {
                    players = room.Players.Values.ToArray();
                    expired = !IsReserved(room, clock.GetUtcNow()) || room.Active == session &&
                        (!accounts.IsActive(room.Creator.Id) || !room.Migrating && !room.AwaitingBridges && room.Info.Running &&
                         clock.GetUtcNow() - session.TickChangedAt > TimeSpan.FromSeconds(settings.MigrationLossSeconds));
                }
                foreach (Slot slot in players)
                    if (!accounts.IsActive(slot.Account.Id)) slot.Revoked.Cancel();
                if (expired) { session.Stop.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested) { }
    }

    private async Task Invite(Room room, Slot slot, CancellationToken cancellation)
    {
        HostSession? active;
        string? key;
        lock (gate)
        {
            active = room.Active;
            key = slot.Key;
            slot.Key = null;
        }
        if (active == null || key == null) return;
        await Control(active, new { kind = "peer", bridgeKey = key, accountId = slot.Account.Id,
            nickname = slot.Account.Nickname, startElement = slot.Element }, cancellation);
    }

    private async Task ConnectBridge(string id, WebSocket socket, byte[] proof, CancellationToken cancellation)
    {
        Room? room;
        Slot? slot = null;
        HostBridge? bridge = null;
        HostSession? source = null;
        lock (gate)
        {
            if (rooms.TryGetValue(id, out room) && IsLive(room, clock.GetUtcNow()))
            {
                slot = room.Players.Values.FirstOrDefault(value => value.Deadline > clock.GetUtcNow() && value.Bridge == null &&
                    !value.Finished.Task.IsCompleted && !value.Revoked.IsCancellationRequested &&
                    CryptographicOperations.FixedTimeEquals(proof, value.Proof));
                if (slot != null)
                {
                    slot.Bridge = bridge = new HostBridge(socket, room.Generation);
                    slot.Paired.TrySetResult();
                    source = room.Active;
                    if (slot.Account.Id == room.Creator.Id) room.CreatorPaired = true;
                }
            }
        }
        if (room == null || slot == null || bridge == null) { await OnlineGameServer.RejectSocket(socket, "invalidTicket"); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, room.Ended.Token, slot.Revoked.Token);
        var buffer = new byte[Protocol.MaxDatagramBytes + 1];
        try
        {
            if (!slot.AuthoritySent)
            {
                slot.AuthoritySent = true;
                await Notice(room, slot, MigrationPhase.None, room.Generation, 0,
                    slot.Account.Id == room.Creator.Id, "", lifetime.Token);
            }
            while (!lifetime.IsCancellationRequested)
            {
                int length = await OnlinePeer.ReceiveBinary(socket, buffer, lifetime.Token);
                if (length == 0)
                {
                    string reason = SafeHostClose(socket.CloseStatusDescription);
                    if (reason.Length != 0)
                    {
                        lock (gate) slot.Terminal = true;
                        await OnlineGameServer.CloseSocket(slot.Player!, WebSocketCloseStatus.PolicyViolation, reason);
                        return;
                    }
                    break;
                }
                bool valid;
                lock (gate) valid = !room.Migrating && room.Generation == bridge.Generation && slot.Bridge == bridge &&
                    room.Active?.Stop.IsCancellationRequested == false;
                if (!valid) continue;
                if ((PacketKind)buffer[0] is PacketKind.HostMigration or PacketKind.HostStandbyOffer or PacketKind.HostCapability or PacketKind.HostLeave)
                    throw new InvalidDataException("invalidPacket");
                if ((PacketKind)buffer[0] == PacketKind.SessionEnded && length == 2 &&
                    buffer[1] is (byte)SessionEndReason.BaseDestroyed or (byte)SessionEndReason.Victory)
                    lock (gate) slot.Terminal = true;
                if (slot.Player == null) break;
                await SendPlayer(slot, bridge.Generation, buffer.AsSpan(0, length).ToArray(), lifetime.Token);
            }
        }
        finally
        {
            bool lost;
            lock (gate)
            {
                if (slot.Bridge == bridge) slot.Bridge = null;
                lost = !slot.Finished.Task.IsCompleted && !slot.Leaving && !slot.Terminal && !room.Migrating && room.Active == source;
            }
            if (lost && !room.Ended.IsCancellationRequested) await BeginMigration(room, false, null);
        }
    }

    private static async Task SendPlayer(Slot slot, ulong generation, byte[] payload, CancellationToken cancellation)
    {
        if (slot.Player == null || slot.Finished.Task.IsCompleted) return;
        await slot.SendGate.WaitAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(SendTimeout);
            await slot.Player.SendAsync(OnlineFrame.Wrap(generation, payload).AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        }
        finally { slot.SendGate.Release(); }
    }

    private static async Task Control(HostSession session, object message, CancellationToken cancellation)
    {
        await session.SendGate.WaitAsync(cancellation);
        try { await SendJson(session.Socket, message, cancellation); }
        finally { session.SendGate.Release(); }
    }

    private static async Task<byte[]> ReadText(WebSocket socket, int maximum, CancellationToken cancellation)
    {
        var buffer = new byte[maximum + 1];
        int length = 0;
        for (int fragments = 0; fragments <= maximum; fragments++)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(length), cancellation);
            length += part.Count;
            if (part.MessageType != WebSocketMessageType.Text || length > maximum) throw new InvalidDataException("invalidPacket");
            if (part.EndOfMessage) return buffer.AsSpan(0, length).ToArray();
        }
        throw new InvalidDataException("invalidPacket");
    }
}
