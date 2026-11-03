using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

/// <summary>별도 수신 포트 없이 중계에 연결하여 한 방의 게임 판정을 실행한다.</summary>
public static class PlayerHostedServer
{
    private const int MaxStartupChars = 1024;
    private const int MaxControlBytes = 512;
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpMessageInvoker Transport = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 4,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record Startup(string? ServiceUrl, string? RoomId, string? HostKey);
    private sealed record ControlMessage(string? Kind, string? Title, int? MinimumPlayers, int? Capacity,
        string? CreatorId, string? CreatorNickname, int? Version, string? DataHash,
        string? BridgeKey, string? AccountId, string? Nickname, int? StartElement);

    /// <summary>표준 입력 한 줄로 시작하고 중계의 목록 등록 확인 뒤 HOST_READY를 출력한다.</summary>
    public static Task Run(GameRules rules, NetworkSettings settings, TextReader input, TextWriter output, CancellationToken stop)
        => Run(rules, settings, input, output, stop, connectSocketFactory: null);

    internal static async Task Run(GameRules rules, NetworkSettings settings, TextReader input, TextWriter output,
        CancellationToken stop, Func<ClientWebSocket>? connectSocketFactory, NetworkLoadTrace? trace = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(stop);
        startup.CancelAfter(AuthenticationTimeout);
        try
        {
            // Windows 표준 입력 파이프의 읽기가 끝나지 않아도 프로세스 시작 대기는 제한한다.
            (Uri origin, string roomId, byte[] key) = await ReadStartup(input, startup.Token).WaitAsync(startup.Token);
            using ClientWebSocket control = connectSocketFactory?.Invoke() ?? new ClientWebSocket();
            await Authenticate(control, SocketUri(origin, roomId, "host"), key, connectSocketFactory == null, startup.Token);
            var buffer = new byte[MaxControlBytes + 1];
            ControlMessage? ready = await ReceiveControl(control, buffer, startup.Token);
            AccountIdentity? creator = Identity(ready?.CreatorId, ready?.CreatorNickname);
            using var rooms = new OnlineRooms(rules, settings) { LoadTrace = trace };
            if (creator == null || ready is not
                { Kind: "ready", MinimumPlayers: int minimumPlayers, Capacity: int capacity, Version: Protocol.Version } ||
                !string.Equals(ready.DataHash, rules.DataHash, StringComparison.OrdinalIgnoreCase) ||
                rooms.Create(creator, ready.Title, minimumPlayers, capacity, out OnlineRoomInfo? room, hostedId: roomId) != null)
                throw new InvalidDataException("중계가 유효한 방 생성 정보를 보내지 않았습니다.");

            await rooms.StartAsync(lifetime.Token);
            try
            {
                await SendStatus(control, room!, startup.Token);
                if (await ReceiveControl(control, buffer, startup.Token) is not { Kind: "listed" })
                    throw new InvalidDataException("중계에서 방 목록 등록을 확인하지 못했습니다.");
                await output.WriteLineAsync("HOST_READY".AsMemory(), startup.Token);
                await output.FlushAsync(startup.Token);
                startup.CancelAfter(Timeout.InfiniteTimeSpan);

                Task publish = PublishStatus(control, rooms, roomId, lifetime.Token);
                Task receive = ReceiveInvites(control, buffer, rooms, roomId, SocketUri(origin, roomId, "bridge"), capacity,
                    Math.Max(12, rules.CommandRateLimit * 2), connectSocketFactory, lifetime.Token);
                Task tick = rooms.ExecuteTask!;
                await Task.WhenAny(publish, receive, tick);
                await lifetime.CancelAsync();
                await Task.WhenAll(publish, receive);
                if (tick.IsCompleted) await tick;
            }
            finally
            {
                await lifetime.CancelAsync();
                await rooms.StopAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (OperationCanceledException) when (startup.IsCancellationRequested)
        { throw new InvalidDataException("방 호스트 시작 시간이 초과되었습니다."); }
    }

    private static async Task PublishStatus(WebSocket control, OnlineRooms rooms, string roomId, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                OnlineRoomInfo? room = rooms.Status(roomId);
                if (room == null)
                {
                    if (control.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await OnlineGameServer.CloseSocket(control, WebSocketCloseStatus.NormalClosure, "");
                    return;
                }
                using var send = CancellationTokenSource.CreateLinkedTokenSource(stop);
                send.CancelAfter(AuthenticationTimeout);
                await SendStatus(control, room, send.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) when (stop.IsCancellationRequested) { }
        catch (OperationCanceledException) { throw new IOException("중계에 방 상태를 보내는 시간이 초과되었습니다."); }
    }

    private static ValueTask SendStatus(WebSocket control, OnlineRoomInfo room, CancellationToken stop)
        => control.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { count = room.Count, running = room.Running }).AsMemory(),
            WebSocketMessageType.Text, true, stop);

    private static async Task ReceiveInvites(WebSocket control, byte[] buffer, OnlineRooms rooms, string roomId, Uri bridge,
        int capacity, int maxMessages, Func<ClientWebSocket>? connectSocketFactory, CancellationToken stop)
    {
        using var connections = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var peers = new List<Task>(capacity);
        try
        {
            while (await ReceiveControl(control, buffer, stop) is ControlMessage invite)
            {
                AccountIdentity? account = Identity(invite.AccountId, invite.Nickname);
                if (invite.Kind != "peer" || account == null) throw new InvalidDataException("중계 참가 요청의 형식이 잘못됐습니다.");
                for (int index = peers.Count - 1; index >= 0; index--)
                {
                    if (!peers[index].IsCompleted) continue;
                    await peers[index];
                    peers.RemoveAt(index);
                }
                if (peers.Count >= capacity)
                {
                    OnlineGameServer.RecordRejected("roomFull");
                    continue;
                }
                peers.Add(RunBridge(rooms, roomId, bridge, ReadKey(invite.BridgeKey), account, invite.StartElement,
                    maxMessages, connectSocketFactory, connections.Token));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) when (stop.IsCancellationRequested) { }
        finally
        {
            await connections.CancelAsync();
            await Task.WhenAll(peers);
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
        catch (OperationCanceledException) when (authentication.IsCancellationRequested)
        { OnlineGameServer.RecordRejected("authenticationTimeout"); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { OnlineGameServer.RecordRejected("socketClosed"); }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await OnlineGameServer.CloseSocket(socket, WebSocketCloseStatus.NormalClosure, "");
    }

    private static async Task Authenticate(ClientWebSocket socket, Uri address, byte[] key, bool useProductionTransport,
        CancellationToken cancellation)
    {
        try
        {
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            await socket.ConnectAsync(address, useProductionTransport ? Transport : null, cancellation);
            await socket.SendAsync(key.AsMemory(), WebSocketMessageType.Binary, true, cancellation);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<ControlMessage?> ReceiveControl(WebSocket socket, byte[] buffer, CancellationToken cancellation)
    {
        int length = 0;
        try
        {
            for (int fragments = 0; fragments < buffer.Length; fragments++)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellation);
                length += result.Count;
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text || length >= buffer.Length) break;
                if (result.EndOfMessage)
                    return JsonSerializer.Deserialize<ControlMessage>(buffer.AsSpan(0, length), Json) ??
                        throw new InvalidDataException("중계 제어 메시지가 비어 있습니다.");
            }
            throw new InvalidDataException("중계 제어 메시지의 형식 또는 크기가 잘못됐습니다.");
        }
        catch (JsonException) { throw new InvalidDataException("중계 제어 메시지의 JSON 형식이 잘못됐습니다."); }
        finally { CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length)); }
    }

    private static async Task<(Uri Origin, string RoomId, byte[] Key)> ReadStartup(TextReader input, CancellationToken cancellation)
    {
        var buffer = new char[MaxStartupChars + 1];
        try
        {
            int length = 0;
            while (length < buffer.Length)
            {
                int count = await input.ReadAsync(buffer.AsMemory(length), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (count == 0) break;
                int newline = buffer.AsSpan(length, count).IndexOf('\n');
                length += newline < 0 ? count : newline;
                if (newline >= 0) break;
            }
            if (length > MaxStartupChars) throw new InvalidDataException("방 호스트 시작 입력이 너무 깁니다.");
            Startup? request = JsonSerializer.Deserialize<Startup>(buffer.AsSpan(0, length), Json);
            if (request == null || !IsId(request.RoomId)) throw new InvalidDataException("방 호스트 시작 입력의 방 ID가 잘못됐습니다.");
            return (ReadOrigin(request.ServiceUrl), request.RoomId!, ReadKey(request.HostKey));
        }
        catch (JsonException) { throw new InvalidDataException("방 호스트 시작 입력의 JSON 형식이 잘못됐습니다."); }
        finally { Array.Clear(buffer); }
    }

    private static Uri ReadOrigin(string? value)
    {
        const string scheme = "https://";
        if (value?.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("중계 서버는 HTTPS 주소가 필요합니다.");
        ReadOnlySpan<char> authority = value.AsSpan(scheme.Length);
        if (authority.EndsWith("/")) authority = authority[..^1];
        if (authority.IsEmpty || authority[^1] == ':' || authority.IndexOfAny("/?#\\@") >= 0 ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !uri.IsWellFormedOriginalString() ||
            uri.HostNameType == UriHostNameType.Unknown || uri.Port <= 0)
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
        if (value?.Length != FileAccountStore.TokenBytes * 2)
            throw new InvalidDataException("중계 키 길이가 잘못됐습니다.");
        var key = new byte[FileAccountStore.TokenBytes];
        if (Convert.FromHexString(value, key, out _, out int length) == OperationStatus.Done && length == key.Length) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidDataException("중계 키 형식이 잘못됐습니다.");
    }
}
