using System.Buffers;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElementalLegion.Server;

/// <summary>HTTPS 방 조회와 인증된 WSS 접속만 외부에 공개한다.</summary>
public static class OnlineGameServer
{
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(5);
    private static readonly Meter Meter = new("ElementalLegion.Online");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("online.requests.rejected");
    internal static readonly Histogram<double> RoomTick = Meter.CreateHistogram<double>("online.room.tick", "ms");
    internal static readonly Histogram<double> SaveCopy = Meter.CreateHistogram<double>("online.world.copy", "ms");

    private sealed record AccountRequest(string? Nickname);
    private sealed record RoomRequest(string? Title, int? MinimumPlayers, int Capacity);
    private sealed record HostedRoomRequest(string? Title, int? MinimumPlayers, int Capacity, string? Password, int Version, string? DataHash);
    private sealed record TicketRequest(int Version, string? DataHash, int? StartElement, string? Password);

    public static WebApplication Build(GameRules rules, NetworkSettings settings, int port, X509Certificate2? certificate,
        string storageDirectory, bool allowRoomCreation = true, TimeProvider? timeProvider = null, string? persistentWorldTitle = null)
    {
        // 인증서 없는 테스트 중계는 같은 PC의 터널 프로세스에서만 접근한다.
        if ((certificate == null && port != 0) || certificate?.HasPrivateKey == false)
            throw new ArgumentException("HTTPS 인증서의 개인 키가 필요합니다.", nameof(certificate));
        var accounts = new FileAccountStore(Path.Combine(storageDirectory, "accounts"));
        var store = new FileWorldStore(Path.Combine(storageDirectory, "worlds"), rules);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 1024;
            options.Limits.MaxRequestHeadersTotalSize = 8192;
            options.Limits.RequestHeadersTimeout = AuthenticationTimeout;
            options.Limits.MaxConcurrentConnections = 128;
            options.Limits.MaxConcurrentUpgradedConnections = 144;
            if (port == 0) options.Listen(IPAddress.Loopback, port, ConfigureListener);
            else options.ListenAnyIP(port, ConfigureListener);

            void ConfigureListener(ListenOptions listener)
            {
                listener.Protocols = HttpProtocols.Http1;
                if (certificate == null) return;
                listener.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.HandshakeTimeout = AuthenticationTimeout;
                });
            }
        });
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        builder.Services.AddSingleton(services => new OnlineRooms(rules, settings, accounts, store,
            Path.Combine(storageDirectory, "reports"), clock, services.GetRequiredService<ILogger<OnlineRooms>>(), persistentWorldTitle));
        builder.Services.AddHostedService(services => services.GetRequiredService<OnlineRooms>());
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => Window(ClientIp(context), 120)),
                PartitionedRateLimiter.Create<HttpContext, string>(context => context.Request.Path == "/accounts" ?
                    Window(ClientIp(context), 20) : RateLimitPartition.GetNoLimiter("other")));
            options.AddPolicy("online-websocket", SocketLimit);
            options.AddPolicy("online-bridge", SocketLimit);
            options.AddPolicy("online-migration", SocketLimit);
            options.OnRejected = async (context, cancellation) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                RecordRejected("rateLimited");
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rateLimited" }, cancellation);
            };
        });
        var accountLimits = PartitionedRateLimiter.Create<AccountIdentity, string>(account => Window(account.Id, 30));

        var app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(accountLimits.Dispose);
        var rooms = app.Services.GetRequiredService<OnlineRooms>();
        var relay = new HostedRelay(rules, accounts, clock, settings);
        var creationGate = new object();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseRateLimiter();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(30) });

        app.MapPost("/accounts", (AccountRequest body) =>
        {
            if (body.Nickname == null || !PlayerNickname.IsValid(body.Nickname)) return Error("invalidNickname");
            try
            {
                IssuedAccount account = accounts.Create(body.Nickname);
                string token = Convert.ToHexStringLower(account.Token);
                CryptographicOperations.ZeroMemory(account.Token);
                return Results.Json(new { token });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                app.Logger.LogError("온라인 계정 기록 실패 ({ErrorType}).", error.GetType().Name);
                return Error("unavailable");
            }
        });
        app.MapGet("/rooms", () => Results.Json(new { rooms = rooms.List().Concat(relay.List()) }));
        app.MapPost("/rooms", (HttpContext context, RoomRequest body) =>
        {
            IResult? denied = Authorize(context, out AccountIdentity? account);
            if (denied != null) return denied;
            if (!allowRoomCreation) return Error("roomCreationDisabled");
            lock (creationGate)
            {
                string? error = CreationError(account!.Id);
                if (error != null) return Error(error);
                error = rooms.Create(account, body.Title, body.MinimumPlayers ?? 2, body.Capacity, out OnlineRoomInfo? room);
                return error == null ? Results.Json(room) : Error(error);
            }
        });
        app.MapPost("/hosted-rooms", (HttpContext context, HostedRoomRequest body) =>
        {
            IResult? denied = Authorize(context, out AccountIdentity? account);
            if (denied != null) return denied;
            if (!allowRoomCreation) return Error("roomCreationDisabled");
            lock (creationGate)
            {
                string? error = CreationError(account!.Id);
                if (error != null) return Error(error);
                error = relay.Create(account, body.Title, body.MinimumPlayers ?? 2, body.Capacity, body.Password,
                    body.Version, body.DataHash, out OnlineRoomInfo? room, out string? hostKey);
                return error == null ? Results.Json(new { room, hostKey }) : Error(error);
            }
        });
        app.MapPost("/rooms/{roomId}/tickets", (HttpContext context, string roomId, TicketRequest body) =>
        {
            IResult? denied = Authorize(context, out AccountIdentity? account);
            if (denied != null) return denied;
            string? error = relay.Owns(roomId)
                ? relay.IssueTicket(roomId, account!, ClientIp(context), body.Version, body.DataHash, body.StartElement, body.Password, out string? ticket)
                : rooms.IssueTicket(roomId, account!, body.Version, body.DataHash, body.StartElement, out ticket);
            return error == null ? Results.Json(new { ticket }) : Error(error);
        });
        app.MapGet("/rooms/{roomId}/host", (HttpContext context, string roomId) => relay.Connect(context, roomId, false,
            app.Lifetime.ApplicationStopping)).RequireRateLimiting("online-websocket");
        app.MapGet("/rooms/{roomId}/bridge", (HttpContext context, string roomId) => relay.Connect(context, roomId, true,
            app.Lifetime.ApplicationStopping)).RequireRateLimiting("online-bridge");
        app.MapGet("/rooms/{roomId}/standby", (HttpContext context, string roomId) => relay.Standby(context, roomId,
            app.Lifetime.ApplicationStopping)).RequireRateLimiting("online-websocket");
        app.MapGet("/rooms/{roomId}/migration/upload", (HttpContext context, string roomId) => relay.Backup(context, roomId, false,
            app.Lifetime.ApplicationStopping)).RequireRateLimiting("online-migration");
        app.MapGet("/rooms/{roomId}/migration/download", (HttpContext context, string roomId) => relay.Backup(context, roomId, true,
            app.Lifetime.ApplicationStopping)).RequireRateLimiting("online-migration");
        app.MapGet("/rooms/{roomId}/play", (HttpContext context, string roomId) => Play(context, roomId, rooms, relay, accounts,
            Math.Max(12, rules.CommandRateLimit * 2), app.Lifetime.ApplicationStopping)).RequireRateLimiting("online-websocket");
        return app;

        string? CreationError(string accountId)
        {
            if (rooms.PendingCreation(accountId) || relay.PendingCreation(accountId)) return "alreadyInRoom";
            return rooms.ReservedCount + relay.ReservedCount >= OnlineRooms.MaxRooms ? "roomLimit" : null;
        }

        IResult? Authorize(HttpContext context, out AccountIdentity? account)
        {
            account = null;
            const string prefix = "Bearer ";
            if (context.Request.Headers.Authorization.Count != 1) return Error("invalidAccount");
            string? header = context.Request.Headers.Authorization[0];
            if (header?.Length != prefix.Length + FileAccountStore.TokenBytes * 2 || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Error("invalidAccount");
            Span<byte> token = stackalloc byte[FileAccountStore.TokenBytes];
            try
            {
                if (Convert.FromHexString(header.AsSpan(prefix.Length), token, out _, out int length) != OperationStatus.Done || length != token.Length)
                    return Error("invalidAccount");
                account = accounts.Authenticate(token);
            }
            finally { CryptographicOperations.ZeroMemory(token); }
            if (account == null) return Error("invalidAccount");
            using RateLimitLease lease = accountLimits.AttemptAcquire(account);
            return lease.IsAcquired ? null : Error("rateLimited");
        }
    }

    private static async Task Play(HttpContext context, string roomId, OnlineRooms rooms, HostedRelay relay, IAccountStore accounts, int maxMessages,
        CancellationToken applicationStopping)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = false });
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, applicationStopping);
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        authentication.CancelAfter(AuthenticationTimeout);
        try
        {
            var token = new byte[FileAccountStore.TokenBytes + 1];
            OnlineTicket? hostedTicket;
            OnlineTicket? ticket;
            try
            {
                int received = await OnlinePeer.ReceiveBinary(socket, token, authentication.Token);
                hostedTicket = relay.Redeem(roomId, token.AsSpan(0, received));
                ticket = hostedTicket ?? rooms.Redeem(roomId, token.AsSpan(0, received));
            }
            finally { CryptographicOperations.ZeroMemory(token); }
            if (ticket == null || !accounts.IsActive(ticket.Account.Id)) { await RejectSocket(socket, "invalidTicket"); return; }
            authentication.CancelAfter(Timeout.InfiniteTimeSpan);
            if (hostedTicket != null)
            {
                await relay.Play(hostedTicket, socket, maxMessages, connection.Token);
                return;
            }
            string? error = rooms.Attach(ticket, out OnlinePeer? peer);
            if (error != null) { await RejectSocket(socket, error); return; }
            peer!.UseOnlineFrames = true;
            await peer!.Run(socket, maxMessages, connection.Token);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await CloseSocket(socket, WebSocketCloseStatus.NormalClosure, "");
        }
        catch (InvalidDataException error)
        { await RejectSocket(socket, error.Message is "rateLimited" or "packetTooLarge" ? error.Message : "invalidPacket"); }
        catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
        catch (OperationCanceledException) when (authentication.IsCancellationRequested) { RecordRejected("authenticationTimeout"); }
        catch (OperationCanceledException) when (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        { await CloseSocket(socket, WebSocketCloseStatus.NormalClosure, ""); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { RecordRejected("socketClosed"); }
    }

    internal static async Task RejectSocket(WebSocket socket, string error)
    {
        RecordRejected(error);
        if (socket.State == WebSocketState.Open) await CloseSocket(socket, WebSocketCloseStatus.PolicyViolation, error);
    }

    internal static async Task CloseSocket(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await socket.CloseOutputAsync(status, reason, timeout.Token); }
        catch (WebSocketException) { socket.Abort(); }
        catch (OperationCanceledException) { socket.Abort(); }
    }

    private static IResult Error(string error)
    {
        RecordRejected(error);
        int status = error switch
        {
            "invalidNickname" or "invalidRoom" or "invalidPassword" or "invalidElement" or "versionMismatch" or "dataMismatch" => StatusCodes.Status400BadRequest,
            "invalidAccount" => StatusCodes.Status401Unauthorized,
            "roomCreationDisabled" or "passwordRequired" or "incorrectPassword" => StatusCodes.Status403Forbidden,
            "roomNotFound" => StatusCodes.Status404NotFound,
            "rateLimited" => StatusCodes.Status429TooManyRequests,
            "unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status409Conflict
        };
        return Results.Json(new { error }, statusCode: status);
    }

    internal static void RecordRejected(string reason) => Rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    private static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // 같은 NAT에서 방장과 참가자가 접속해도 각 중계 소켓이 플레이어 한도를 차지하지 않는다.
    private static RateLimitPartition<string> SocketLimit(HttpContext context) => RateLimitPartition.GetConcurrencyLimiter(ClientIp(context), _ =>
        new ConcurrencyLimiterOptions { PermitLimit = 20, QueueLimit = 0 });

    private static RateLimitPartition<string> Window(string key, int permits) => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
        new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = permits, QueueLimit = 0 });
}
