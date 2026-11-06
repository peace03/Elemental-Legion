using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

/// <summary>중계에만 연결하여 한 방의 판정 또는 승계를 기다리는 예비 서버를 실행한다.</summary>
public static class PlayerHostedServer
{
    private const int MaxStartupChars = 1024;
    private const int MaxControlBytes = 2048;
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpMessageInvoker Transport = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 4,
        AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record Startup(string? ServiceUrl, string? RoomId, string? HostKey, bool Standby = false, ulong Generation = 1);
    private sealed record StartRequest(Uri Origin, string RoomId, byte[] Key, bool Standby, ulong Generation);
    private sealed record ControlMessage(string? Kind, string? Title, int? MinimumPlayers, int? Capacity,
        string? CreatorId, string? CreatorNickname, int? Version, string? DataHash,
        string? BridgeKey, string? AccountId, string? Nickname, int? StartElement,
        ulong? Generation, bool? Standby, string? LeavingAccountId, ulong? SnapshotGeneration,
        string[]? Accounts, ulong? BackupNumber, long? Tick);

    public static Task Run(GameRules rules, NetworkSettings settings, TextReader input, TextWriter output, CancellationToken stop)
        => Run(rules, settings, input, output, stop, connectSocketFactory: null);

    internal static async Task Run(GameRules rules, NetworkSettings settings, TextReader input, TextWriter output,
        CancellationToken stop, Func<ClientWebSocket>? connectSocketFactory, NetworkLoadTrace? trace = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(stop);
        startup.CancelAfter(AuthenticationTimeout);
        StartRequest? request = null;
        try
        {
            request = await ReadStartup(input, startup.Token).WaitAsync(startup.Token);
            using ClientWebSocket control = connectSocketFactory?.Invoke() ?? new ClientWebSocket();
            await Authenticate(control, SocketUri(request.Origin, request.RoomId, request.Standby ? "standby" : "host"),
                (byte[])request.Key.Clone(), connectSocketFactory == null, startup.Token);
            var buffer = new byte[MaxControlBytes + 1];
            ControlMessage? ready = await ReceiveControl(control, buffer, startup.Token);
            // 마지막 참가자 종료와 예비 서버 인증이 겹치면 준비 응답 전에 정상 종료될 수 있다.
            if (request.Standby && (ready?.Kind == "retire" || ready == null && control.CloseStatus == WebSocketCloseStatus.NormalClosure)) return;
            AccountIdentity? creator = Identity(ready?.CreatorId, ready?.CreatorNickname);
            if (creator == null || ready is not { Kind: "ready", MinimumPlayers: int minimum, Capacity: int capacity, Version: Protocol.Version } ||
                ready.Generation != request.Generation || !string.Equals(ready.DataHash, rules.DataHash, StringComparison.OrdinalIgnoreCase) ||
                OnlineRooms.ValidateRoom(rules, ready.Title, minimum, capacity) != ready.Title)
                throw new InvalidDataException("중계가 유효한 방 생성 정보를 보내지 않았습니다.");

            await using var session = new RuntimeSession(rules, settings, request, control, connectSocketFactory, trace);
            if (!request.Standby)
            {
                if (session.Rooms.Create(creator, ready.Title, minimum, capacity, out _, hostedId: request.RoomId) != null)
                    throw new InvalidDataException("방 런타임을 만들 수 없습니다.");
                session.Rooms.RequireHostedBackups(request.RoomId);
                await session.Activate(startup.Token);
                await session.Publish(startup.Token);
                if (await ReceiveControl(control, buffer, startup.Token) is not { Kind: "listed" })
                    throw new InvalidDataException("중계에서 방 목록 등록을 확인하지 못했습니다.");
            }
            else await session.Publish(startup.Token);

            await output.WriteLineAsync((request.Standby ? "STANDBY_READY" : "HOST_READY").AsMemory(), startup.Token);
            await output.FlushAsync(startup.Token);
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            Task publish = session.PublishLoop(lifetime.Token);
            Task backup = session.BackupLoop(lifetime.Token);
            Task receive = session.ReceiveLoop(buffer, capacity, lifetime.Token);
            Task tick = session.ObserveTick(lifetime.Token);
            Task[] running = [publish, backup, receive, tick];
            await Task.WhenAny(running);
            await lifetime.CancelAsync();
            try { await Task.WhenAll(running); }
            // 실행 중 소켓 종료와 그 뒤의 취소는 정상 정리하며 형식·판정 오류는 그대로 전파한다.
            catch (Exception) when (running.Where(task => task.IsFaulted).SelectMany(task => task.Exception!.Flatten().InnerExceptions)
                .All(error => error is WebSocketException or OperationCanceledException)) { }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (OperationCanceledException) when (startup.IsCancellationRequested)
        { throw new InvalidDataException("방 호스트 시작 시간이 초과되었습니다."); }
        catch (Exception error)
        {
            trace?.RecordFault(error, request?.Generation ?? 0, trace.LastTick);
            throw;
        }
        finally { if (request != null) CryptographicOperations.ZeroMemory(request.Key); }
    }

    /// <summary>제어 송신과 백업 송신을 분리해 큰 백업이 하트비트를 막지 않도록 한다.</summary>
    private sealed class RuntimeSession : IAsyncDisposable
    {
        private readonly GameRules rules;
        private readonly NetworkSettings settings;
        private readonly StartRequest request;
        private readonly WebSocket control;
        private readonly Func<ClientWebSocket>? factory;
        private readonly NetworkLoadTrace? trace;
        private readonly SemaphoreSlim send = new(1, 1);
        private readonly SemaphoreSlim backup = new(1, 1);
        private readonly TaskCompletionSource<Task> tickStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ClientWebSocket? upload;
        private ClientWebSocket? download;
        private HostMigrationSnapshot? staged;
        private volatile bool active;
        internal OnlineRooms Rooms { get; private set; }

        internal RuntimeSession(GameRules rules, NetworkSettings settings, StartRequest request, WebSocket control,
            Func<ClientWebSocket>? factory, NetworkLoadTrace? trace)
        {
            this.rules = rules;
            this.settings = settings;
            this.request = request;
            this.control = control;
            this.factory = factory;
            this.trace = trace;
            Rooms = new OnlineRooms(rules, settings) { LoadTrace = trace };
        }

        internal async Task Activate(CancellationToken stop)
        {
            if (active) return;
            await Rooms.StartAsync(stop);
            active = true;
            tickStarted.TrySetResult(Rooms.ExecuteTask!);
        }

        internal async Task ObserveTick(CancellationToken stop)
        {
            Task tick = await tickStarted.Task.WaitAsync(stop);
            await tick.WaitAsync(stop);
        }

        internal Task Publish(CancellationToken stop)
        {
            OnlineRoomInfo? room = Rooms.Status(request.RoomId);
            return WriteControl(new
            {
                kind = active ? "state" : "standbyReady", generation = request.Generation,
                count = room?.Count ?? 0, running = room?.Running ?? false, tick = Rooms.HostedTick(request.RoomId)
            }, stop);
        }

        internal async Task PublishLoop(CancellationToken stop)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.MigrationHeartbeatSeconds));
            while (await timer.WaitForNextTickAsync(stop))
            {
                if (active && Rooms.Status(request.RoomId) == null) return;
                await Publish(stop);
            }
        }

        internal async Task BackupLoop(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                if (active && !Rooms.HostedFrozen(request.RoomId) && Rooms.Status(request.RoomId) != null)
                {
                    try { await CaptureAndUpload(false, null, stop); }
                    catch (Exception error) when (!stop.IsCancellationRequested && error is IOException or WebSocketException or OperationCanceledException or InvalidOperationException)
                    {
                        Console.Error.WriteLine($"승계 백업 전송 실패 ({error.GetType().Name})");
                    }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                Task signal = Rooms.WaitForHostedBackup(wait.Token).AsTask();
                Task delay = Task.Delay(TimeSpan.FromSeconds(settings.MigrationBackupSeconds), wait.Token);
                await Task.WhenAny(signal, delay);
                await wait.CancelAsync();
                try { await Task.WhenAll(signal, delay); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
            }
        }

        private async Task CaptureAndUpload(bool freeze, string? leavingAccount, CancellationToken stop)
        {
            await backup.WaitAsync(stop);
            try
            {
                HostMigrationSnapshot snapshot = await Rooms.CaptureHostedMigration(request.RoomId, request.Generation, freeze, leavingAccount).WaitAsync(stop);
                var encoded = await Task.Run(() =>
                {
                    long started = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
                    byte[] bytes = MigrationSnapshotCodec.Encode(snapshot);
                    return (Bytes: bytes, Milliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        Allocated: GC.GetAllocatedBytesForCurrentThread() - allocated);
                }, stop);
                byte[] bytes = encoded.Bytes;
                long transferStarted = Stopwatch.GetTimestamp();
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(stop);
                transfer.CancelAfter(TimeSpan.FromSeconds(settings.MigrationCandidateSeconds));
                if (upload == null || upload.State != WebSocketState.Open)
                {
                    upload?.Dispose();
                    upload = factory?.Invoke() ?? new ClientWebSocket();
                    await Authenticate(upload, SocketUri(request.Origin, request.RoomId, "migration/upload"),
                        (byte[])request.Key.Clone(), factory == null, transfer.Token);
                }
                await MigrationSnapshotTransfer.Write(upload, bytes, transfer.Token);
                await MigrationSnapshotTransfer.ReadAcknowledgment(upload, transfer.Token);
                Rooms.ConfirmHostedBackup(request.RoomId, snapshot.World != null);
                await WriteControl(new { kind = "checkpoint", generation = request.Generation, tick = snapshot.Tick, backupNumber = snapshot.BackupNumber, final = freeze }, stop);
                double transferMs = Stopwatch.GetElapsedTime(transferStarted).TotalMilliseconds;
                if (trace != null) await trace.RecordBackup(snapshot.Generation, snapshot.BackupNumber, snapshot.Tick,
                    bytes, encoded.Milliseconds, encoded.Allocated, transferMs);
            }
            catch
            {
                upload?.Dispose();
                upload = null;
                throw;
            }
            finally { backup.Release(); }
        }

        internal async Task ReceiveLoop(byte[] buffer, int capacity, CancellationToken stop)
        {
            using var connections = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var peers = new List<Task>(capacity);
            try
            {
                while (await ReceiveControl(control, buffer, stop) is ControlMessage message)
                {
                    switch (message.Kind)
                    {
                        case "peer":
                            AccountIdentity account = Identity(message.AccountId, message.Nickname) ?? throw new InvalidDataException("참가 계정이 잘못됐습니다.");
                            for (int index = peers.Count - 1; index >= 0; index--)
                                if (peers[index].IsCompleted) { await peers[index]; peers.RemoveAt(index); }
                            if (!active || peers.Count >= capacity) throw new InvalidDataException("참가 연결을 받을 수 없습니다.");
                            peers.Add(RunBridge(Rooms, request.RoomId, SocketUri(request.Origin, request.RoomId, "bridge"), ReadKey(message.BridgeKey),
                                account, message.StartElement, Math.Max(12, rules.CommandRateLimit * 2), factory, connections.Token));
                            break;
                        case "backup":
                            if (active) Rooms.RequestHostedBackup();
                            break;
                        case "freeze":
                            if (!active) throw new InvalidDataException("예비 서버를 동결할 수 없습니다.");
                            await CaptureAndUpload(true, message.LeavingAccountId, stop);
                            break;
                        case "resume":
                            if (active) Rooms.ResumeHostedMigration(request.RoomId);
                            break;
                        case "restore":
                            if (active) throw new InvalidDataException("실행 중인 서버에 이전 상태를 덮어쓸 수 없습니다.");
                            staged = await Download(stop);
                            if (staged.RoomId != request.RoomId || staged.Generation >= request.Generation ||
                                message.SnapshotGeneration.HasValue && staged.Generation != message.SnapshotGeneration.Value)
                                throw new InvalidDataException("승계 백업의 방 또는 세대가 다릅니다.");
                            AccountIdentity restoredCreator = Identity(message.CreatorId, message.CreatorNickname) ??
                                throw new InvalidDataException("준비할 새 방장 계정이 잘못됐습니다.");
                            if (message.Accounts == null || message.Accounts.Any(accountId => !IsId(accountId)))
                                throw new InvalidDataException("준비할 현재 참가자가 없습니다.");
                            var prepared = new OnlineRooms(rules, settings) { LoadTrace = trace };
                            try { prepared.RestoreHostedMigration(staged, restoredCreator, message.Accounts); }
                            catch { prepared.Dispose(); throw; }
                            OnlineRooms previous = Rooms;
                            Rooms = prepared;
                            previous.Dispose();
                            await WriteControl(new { kind = "readyForActivation", generation = request.Generation, tick = staged.Tick, backupNumber = staged.BackupNumber }, stop);
                            break;
                        case "activate":
                            if (!active)
                            {
                                download?.Dispose();
                                download = null;
                                AccountIdentity creator = Identity(message.CreatorId, message.CreatorNickname) ?? throw new InvalidDataException("새 방장 계정이 잘못됐습니다.");
                                if (staged == null || message.Accounts == null || message.Accounts.Any(accountId => !IsId(accountId)))
                                    throw new InvalidDataException("승계할 상태 또는 현재 참가자가 없습니다.");
                                Rooms.ReconcileHostedMigration(request.RoomId, creator, message.Accounts);
                                staged = null;
                                await Activate(stop);
                            }
                            await Publish(stop);
                            break;
                        case "listed": break;
                        case "retire": return;
                        default: throw new InvalidDataException("중계 제어 메시지 종류가 잘못됐습니다.");
                    }
                }
            }
            finally
            {
                await connections.CancelAsync();
                await Task.WhenAll(peers);
            }
        }

        private async Task<HostMigrationSnapshot> Download(CancellationToken stop)
        {
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(stop);
            transfer.CancelAfter(TimeSpan.FromSeconds(settings.MigrationCandidateSeconds));
            try
            {
                if (download == null || download.State != WebSocketState.Open)
                {
                    download?.Dispose();
                    download = factory?.Invoke() ?? new ClientWebSocket();
                    await Authenticate(download, SocketUri(request.Origin, request.RoomId, "migration/download"),
                        (byte[])request.Key.Clone(), factory == null, transfer.Token);
                }
                await download.SendAsync("NEXT"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, transfer.Token);
                byte[] bytes = await MigrationSnapshotTransfer.Read(download, transfer.Token);
                return await Task.Run(() => MigrationSnapshotCodec.Decode(bytes, rules), stop);
            }
            catch { download?.Dispose(); download = null; throw; }
        }

        private async Task WriteControl(object message, CancellationToken stop)
        {
            await send.WaitAsync(stop);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(AuthenticationTimeout);
                await control.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Json).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            }
            finally { send.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            upload?.Dispose();
            download?.Dispose();
            await Rooms.StopAsync(CancellationToken.None);
            Rooms.Dispose();
            send.Dispose();
            backup.Dispose();
        }
    }

    private static async Task RunBridge(OnlineRooms rooms, string roomId, Uri bridge, byte[] key, AccountIdentity account,
        int? startElement, int maxMessages, Func<ClientWebSocket>? connectSocketFactory, CancellationToken stop)
    {
        using ClientWebSocket socket = connectSocketFactory?.Invoke() ?? new ClientWebSocket();
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(stop);
        authentication.CancelAfter(AuthenticationTimeout);
        try
        {
            await Authenticate(socket, bridge, key, connectSocketFactory == null, authentication.Token);
            authentication.CancelAfter(Timeout.InfiniteTimeSpan);
            stop.ThrowIfCancellationRequested();
            string? denied = rooms.AttachHosted(roomId, account, startElement, out OnlinePeer? peer);
            if (denied != null) { await OnlineGameServer.RejectSocket(socket, denied); return; }
            await peer!.Run(socket, maxMessages, stop);
        }
        catch (InvalidDataException error)
        { await OnlineGameServer.RejectSocket(socket, error.Message is "rateLimited" or "packetTooLarge" ? error.Message : "invalidPacket"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (OperationCanceledException) when (authentication.IsCancellationRequested) { OnlineGameServer.RecordRejected("authenticationTimeout"); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { OnlineGameServer.RecordRejected("socketClosed"); }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await OnlineGameServer.CloseSocket(socket, WebSocketCloseStatus.NormalClosure, ""); }
            catch (WebSocketException) { }
        }
    }

    private static async Task Authenticate(ClientWebSocket socket, Uri address, byte[] key, bool production, CancellationToken stop)
    {
        try
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            await socket.ConnectAsync(address, production ? Transport : null, stop);
            await socket.SendAsync(key.AsMemory(), WebSocketMessageType.Binary, true, stop);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<ControlMessage?> ReceiveControl(WebSocket socket, byte[] buffer, CancellationToken stop)
    {
        int length = 0;
        try
        {
            for (int fragments = 0; fragments < buffer.Length; fragments++)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(length), stop);
                length += result.Count;
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text || length >= buffer.Length) break;
                if (result.EndOfMessage)
                    return JsonSerializer.Deserialize<ControlMessage>(buffer.AsSpan(0, length), Json) ?? throw new InvalidDataException("제어 메시지가 비어 있습니다.");
            }
            throw new InvalidDataException("제어 메시지의 형식 또는 크기가 잘못됐습니다.");
        }
        catch (JsonException error) { throw new InvalidDataException("제어 메시지의 JSON 형식이 잘못됐습니다.", error); }
        finally { CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length)); }
    }

    private static async Task<StartRequest> ReadStartup(TextReader input, CancellationToken stop)
    {
        var buffer = new char[MaxStartupChars + 1];
        try
        {
            int length = 0;
            while (length < buffer.Length)
            {
                int count = await input.ReadAsync(buffer.AsMemory(length), stop);
                stop.ThrowIfCancellationRequested();
                if (count == 0) break;
                int newline = buffer.AsSpan(length, count).IndexOf('\n');
                length += newline < 0 ? count : newline;
                if (newline >= 0) break;
            }
            if (length > MaxStartupChars) throw new InvalidDataException("방 호스트 시작 입력이 너무 깁니다.");
            Startup? request = JsonSerializer.Deserialize<Startup>(buffer.AsSpan(0, length), Json);
            if (request == null || !IsId(request.RoomId) || request.Generation == 0)
                throw new InvalidDataException("방 호스트 시작 입력의 방 또는 세대가 잘못됐습니다.");
            return new StartRequest(ReadOrigin(request.ServiceUrl), request.RoomId!, ReadKey(request.HostKey), request.Standby, request.Generation);
        }
        catch (JsonException error) { throw new InvalidDataException("방 호스트 시작 입력의 JSON 형식이 잘못됐습니다.", error); }
        finally { Array.Clear(buffer); }
    }

    private static Uri ReadOrigin(string? value)
    {
        const string scheme = "https://";
        if (value?.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) != true) throw new InvalidDataException("중계 서버는 HTTPS 주소가 필요합니다.");
        ReadOnlySpan<char> authority = value.AsSpan(scheme.Length);
        if (authority.EndsWith("/")) authority = authority[..^1];
        if (authority.IsEmpty || authority[^1] == ':' || authority.IndexOfAny("/?#\\@") >= 0 ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !uri.IsWellFormedOriginalString() || uri.HostNameType == UriHostNameType.Unknown || uri.Port <= 0)
            throw new InvalidDataException("중계 서버 주소에는 호스트와 포트만 사용할 수 있습니다.");
        return uri;
    }

    private static Uri SocketUri(Uri origin, string roomId, string endpoint)
        => new UriBuilder(origin) { Scheme = "wss", Port = origin.Port, Path = $"/rooms/{roomId}/{endpoint}" }.Uri;

    private static bool IsId(string? id) => Guid.TryParseExact(id, "N", out Guid value) && value != Guid.Empty && value.ToString("N") == id;
    private static AccountIdentity? Identity(string? id, string? nickname)
        => IsId(id) && nickname != null && PlayerNickname.IsValid(nickname) ? new AccountIdentity(id!, nickname) : null;

    private static byte[] ReadKey(string? value)
    {
        if (value?.Length != FileAccountStore.TokenBytes * 2) throw new InvalidDataException("중계 키 길이가 잘못됐습니다.");
        var key = new byte[FileAccountStore.TokenBytes];
        if (Convert.FromHexString(value, key, out _, out int length) == OperationStatus.Done && length == key.Length) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidDataException("중계 키 형식이 잘못됐습니다.");
    }
}
