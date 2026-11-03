using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ElementalLegion.Tests;

/// <summary>실제 루프백 TLS에서 계정·입장권·대기실·복귀 경계를 확인한다.</summary>
public sealed class M5OnlineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HostStartsOnceAndOnlyTheOriginalAccountCanResumeItsPlayer()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"elemental-online-{Guid.NewGuid():N}");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var alternativeName = new SubjectAlternativeNameBuilder();
        alternativeName.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(alternativeName.Build());
        using X509Certificate2 selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pkcs12), null);
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken cancellation = timeout.Token;
        var time = new TestClock();
        var roomTicks = new ConcurrentQueue<double>();
        var saveCopies = new ConcurrentQueue<double>();
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "ElementalLegion.Online") listener.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == "online.room.tick") roomTicks.Enqueue(value);
            if (instrument.Name == "online.world.copy") saveCopies.Enqueue(value);
        });
        metrics.Start();
        WebApplication? app = null;
        try
        {
            app = OnlineGameServer.Build(rules, settings, 0, certificate, directory, timeProvider: time, persistentWorldTitle: "상시 월드");
            await app.StartAsync(cancellation);
            var address = Address(app);
            using var untrusted = new HttpClient { BaseAddress = address };
            await Assert.ThrowsAsync<HttpRequestException>(() => untrusted.GetAsync("/rooms", cancellation));
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, presented, _, errors) => IsExpected(presented, errors)
            });
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new UriBuilder(address) { Host = "localhost", Path = "/rooms" }.Uri, cancellation));

            string host = (await Post("/accounts", new { nickname = "같은이름" })).GetProperty("token").GetString()!;
            string guest = (await Post("/accounts", new { nickname = "같은이름" })).GetProperty("token").GetString()!;
            string other = (await Post("/accounts", new { nickname = "같은이름" })).GetProperty("token").GetString()!;
            Assert.Equal("invalidPassword", (await Post("/hosted-rooms", new
            {
                title = "비밀번호 형식 오류", minimumPlayers = 2, capacity = 2, password = "１２３４",
                version = Protocol.Version, dataHash = rules.DataHash
            }, host, HttpStatusCode.BadRequest)).GetProperty("error").GetString());
            Assert.Equal("invalidRoom", (await Post("/rooms", new { title = "인원 오류", minimumPlayers = 0, capacity = 2 }, host,
                HttpStatusCode.BadRequest)).GetProperty("error").GetString());
            string roomId = (await Post("/rooms", new { title = "테스트 방", minimumPlayers = 2, capacity = 2 }, host)).GetProperty("id").GetString()!;
            string otherRoom = (await Post("/rooms", new { title = "다른 방", minimumPlayers = 2, capacity = 2 }, other)).GetProperty("id").GetString()!;
            string ticketPath = $"/rooms/{roomId}/tickets";
            var version = new { version = Protocol.Version, dataHash = rules.DataHash };
            byte[] hostTicket = await Ticket(host);
            using (ClientWebSocket wrongRoom = await Connect(hostTicket, otherRoom))
            {
                var closed = await wrongRoom.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), cancellation);
                Assert.Equal("invalidTicket", closed.CloseStatusDescription);
            }

            using ClientWebSocket hostSocket = await Connect(hostTicket, roomId);
            await ReadUntil(hostSocket, PacketKind.LobbyState);
            await Send(hostSocket, [(byte)PacketKind.LobbyStart]);
            Assert.Equal(2, (await ReadUntil(hostSocket, PacketKind.LobbyError))[1]);

            byte[] expiredTicket = await Ticket(guest);
            time.Advance(TimeSpan.FromSeconds(31));
            using (ClientWebSocket expired = await Connect(expiredTicket, roomId))
            {
                var closed = await expired.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), cancellation);
                Assert.Equal("invalidTicket", closed.CloseStatusDescription);
            }

            using ClientWebSocket guestSocket = await Connect(await Ticket(guest), roomId);
            await ReadUntil(guestSocket, PacketKind.LobbyState);
            await Send(guestSocket, [(byte)PacketKind.LobbyStart]);
            Assert.Equal(1, (await ReadUntil(guestSocket, PacketKind.LobbyError))[1]);
            await Send(guestSocket, [(byte)PacketKind.LobbyReady, (byte)rules.StartingElements[0], 1]);
            await ReadUntil(guestSocket, PacketKind.LobbyState);
            await Send(hostSocket, [(byte)PacketKind.LobbyReady, (byte)rules.StartingElements[0], 1]);
            await Send(hostSocket, [(byte)PacketKind.LobbyStart]);
            int hostPlayer = BitConverter.ToInt32(await ReadUntil(hostSocket, PacketKind.Welcome), 1);
            int guestPlayer = BitConverter.ToInt32(await ReadUntil(guestSocket, PacketKind.Welcome), 1);
            Assert.NotEqual(hostPlayer, guestPlayer);
            await Send(hostSocket, [(byte)PacketKind.LobbyStart]);
            Assert.Equal(4, (await ReadUntil(hostSocket, PacketKind.LobbyError))[1]);
            var denied = await Post(ticketPath, version, other, HttpStatusCode.Conflict);
            Assert.Equal("reentryUnavailable", denied.GetProperty("error").GetString());

            string malformedClient = (await Post("/accounts", new { nickname = "길이검증" })).GetProperty("token").GetString()!;
            string malformedRoom = (await Post("/rooms", new { title = "과대 패킷 검증", minimumPlayers = 2, capacity = 2 }, malformedClient))
                .GetProperty("id").GetString()!;
            byte[] oversizedTicket = Convert.FromHexString((await Post($"/rooms/{malformedRoom}/tickets", version, malformedClient)).GetProperty("ticket").GetString()!);
            using (ClientWebSocket oversized = await Connect(oversizedTicket, malformedRoom))
            {
                await ReadUntil(oversized, PacketKind.LobbyState);
                await oversized.SendAsync(new ArraySegment<byte>(new byte[700]), WebSocketMessageType.Binary, false, cancellation);
                await oversized.SendAsync(new ArraySegment<byte>(new byte[325]), WebSocketMessageType.Binary, true, cancellation);
                var closed = await oversized.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), cancellation);
                Assert.Equal("packetTooLarge", closed.CloseStatusDescription);
            }

            JsonElement list = await http.GetFromJsonAsync<JsonElement>(new Uri(address, "/rooms"), cancellation);
            JsonElement permanent = Assert.Single(list.GetProperty("rooms").EnumerateArray(), room => room.GetProperty("persistent").GetBoolean());
            string permanentRoom = permanent.GetProperty("id").GetString()!;
            string permanentPath = $"/rooms/{permanentRoom}/tickets";
            var invalidElement = new { version = Protocol.Version, dataHash = rules.DataHash, startElement = 255 };
            Assert.Equal("invalidElement", (await Post(permanentPath, invalidElement, other, HttpStatusCode.BadRequest)).GetProperty("error").GetString());
            var validElement = new { version = Protocol.Version, dataHash = rules.DataHash, startElement = rules.StartingElements[^1] };
            byte[] permanentTicket = Convert.FromHexString((await Post(permanentPath, validElement, other)).GetProperty("ticket").GetString()!);
            using ClientWebSocket permanentSocket = await Connect(permanentTicket, permanentRoom);
            int permanentPlayer = BitConverter.ToInt32(await ReadUntil(permanentSocket, PacketKind.Welcome), 1);

            await ReadUntil(hostSocket, PacketKind.Pose);
            var movement = new NetDataWriter();
            var confirmations = new double[30];
            long appBytes = 0;
            long measuredAt = Stopwatch.GetTimestamp();
            for (int index = 0; index < confirmations.Length; index++)
            {
                ulong sequence = (ulong)index + 1;
                Protocol.WriteMove(movement, sequence, Vector2.Zero);
                long sentAt = Stopwatch.GetTimestamp();
                await Send(hostSocket, movement.CopyData());
                byte[] accepted = await ReadUntil(hostSocket, PacketKind.Accepted, bytes => appBytes += bytes);
                Assert.Equal(sequence, BitConverter.ToUInt64(accepted, 1));
                LegionPose pose;
                do
                {
                    byte[] applied = await ReadUntil(hostSocket, PacketKind.Pose, bytes => appBytes += bytes);
                    Assert.True(Protocol.TryReadPose(new NetDataReader(applied), rules, out pose));
                } while (pose.AppliedSequence < sequence);
                confirmations[index] = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
            }
            double receiveRate = appBytes / Stopwatch.GetElapsedTime(measuredAt).TotalSeconds / 1024;

            var chat = new NetDataWriter();
            M5Protocol.WriteChat(chat, true, "임시 방의 메시지");
            await Send(hostSocket, chat.CopyData());
            await ReadUntil(hostSocket, PacketKind.ChatMessage);
            byte[] guestChat = await ReadUntil(guestSocket, PacketKind.ChatMessage);
            Assert.True(M5Protocol.TryReadChatMessage(new NetDataReader(guestChat), settings, out ChatMessage receivedChat));
            Assert.Equal("임시 방의 메시지", receivedChat.Text);
            M5Protocol.WriteChatReport(chat, receivedChat.Id, "신고 사유");
            await Send(guestSocket, chat.CopyData());
            M5Protocol.WriteChat(chat, true, "상시 월드의 메시지");
            await Send(permanentSocket, chat.CopyData());
            byte[] permanentChat = await ReadUntil(permanentSocket, PacketKind.ChatMessage);
            Assert.True(M5Protocol.TryReadChatMessage(new NetDataReader(permanentChat), settings, out receivedChat));
            Assert.Equal("상시 월드의 메시지", receivedChat.Text);

            await hostSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellation);
            using (ClientWebSocket reused = await Connect(hostTicket, roomId))
            {
                var closed = await reused.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), cancellation);
                Assert.Equal("invalidTicket", closed.CloseStatusDescription);
            }

            byte[] resumeTicket;
            while (true)
            {
                using HttpResponseMessage response = await Request(ticketPath, version, host);
                if (response.IsSuccessStatusCode)
                {
                    JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellation);
                    resumeTicket = Convert.FromHexString(body.GetProperty("ticket").GetString()!);
                    break;
                }
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                await Task.Delay(100, cancellation);
            }
            using ClientWebSocket resumed = await Connect(resumeTicket, roomId);
            Assert.True(Protocol.TryReadWelcome(new NetDataReader(await ReadUntil(resumed, PacketKind.Welcome)),
                out int resumedOwner, out bool wasResumed, out _));
            Assert.True(wasResumed);
            Assert.Equal(hostPlayer, resumedOwner);
            await ReadUntil(resumed, PacketKind.FogBaseline);

            await app.StopAsync(cancellation);
            await app.DisposeAsync();
            string reportPath = Assert.Single(Directory.EnumerateFiles(Path.Combine(directory, "reports"), "*.report"));
            using (JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, cancellation)))
            {
                Assert.Equal(roomId, report.RootElement.GetProperty("RoomId").GetString());
                Assert.Equal("임시 방의 메시지", report.RootElement.GetProperty("Text").GetString());
                Assert.Equal("신고 사유", report.RootElement.GetProperty("Reason").GetString());
            }
            app = OnlineGameServer.Build(rules, settings, 0, certificate, directory, persistentWorldTitle: "상시 월드");
            await app.StartAsync(cancellation);
            address = Address(app);
            using ClientWebSocket restarted = await Connect(await Ticket(host), roomId);
            Assert.Equal(hostPlayer, BitConverter.ToInt32(await ReadUntil(restarted, PacketKind.Welcome), 1));
            await ReadUntil(restarted, PacketKind.FogBaseline);
            permanentTicket = Convert.FromHexString((await Post(permanentPath, validElement, other)).GetProperty("ticket").GetString()!);
            using ClientWebSocket permanentRestarted = await Connect(permanentTicket, permanentRoom);
            Assert.Equal(permanentPlayer, BitConverter.ToInt32(await ReadUntil(permanentRestarted, PacketKind.Welcome), 1));
            double[] ticks = roomTicks.ToArray();
            double[] copies = saveCopies.ToArray();
            Assert.NotEmpty(ticks);
            Assert.NotEmpty(copies);
            output.WriteLine(FormattableString.Invariant($"M5 WSS loopback: applied p95={Percentile(confirmations, .95):F3} ms (n={confirmations.Length}); receive={receiveRate:F2} KiB/s app payload; room tick p99={Percentile(ticks, .99):F3} ms (n={ticks.Length}); checkpoint copy p99={Percentile(copies, .99):F3} ms (n={copies.Length})"));

            Task<HttpResponseMessage> Request(string path, object body, string? bearer)
            {
                var message = new HttpRequestMessage(HttpMethod.Post, new Uri(address, path)) { Content = JsonContent.Create(body) };
                if (bearer != null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                return http.SendAsync(message, cancellation);
            }

            async Task<JsonElement> Post(string path, object body, string? bearer = null, HttpStatusCode expected = HttpStatusCode.OK)
            {
                using HttpResponseMessage response = await Request(path, body, bearer);
                Assert.Equal(expected, response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellation);
            }

            async Task<byte[]> Ticket(string bearer) => Convert.FromHexString((await Post(ticketPath, version, bearer)).GetProperty("ticket").GetString()!);

            async Task<ClientWebSocket> Connect(byte[] ticket, string room)
            {
                var socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = (_, presented, _, errors) => IsExpected(presented, errors);
                await socket.ConnectAsync(new UriBuilder(address) { Scheme = "wss", Path = $"/rooms/{room}/play" }.Uri, cancellation);
                await Send(socket, ticket);
                return socket;
            }

            bool IsExpected(X509Certificate? presented, SslPolicyErrors errors) =>
                (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && presented?.GetCertHashString() == certificate.Thumbprint;

            async Task Send(ClientWebSocket socket, byte[] bytes) =>
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, cancellation);

            async Task<byte[]> ReadUntil(ClientWebSocket socket, PacketKind kind, Action<int>? received = null)
            {
                var buffer = new byte[1024];
                while (true)
                {
                    var message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
                    Assert.Equal(WebSocketMessageType.Binary, message.MessageType);
                    Assert.True(message.EndOfMessage);
                    received?.Invoke(message.Count);
                    if (message.Count > 0 && buffer[0] == (byte)kind) return buffer[..message.Count];
                }
            }
        }
        finally
        {
            if (app != null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static double Percentile(double[] values, double percentile)
    {
        Array.Sort(values);
        return values[(int)Math.Ceiling(values.Length * percentile) - 1];
    }

    [Fact]
    public void AccountKeyIsHashedAndRevocationIsVisibleAcrossInstances()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"elemental-accounts-{Guid.NewGuid():N}");
        try
        {
            var store = new FileAccountStore(directory);
            IssuedAccount issued = store.Create("별빛");
            var reloaded = new FileAccountStore(directory);
            Assert.Equal(issued.Account, reloaded.Authenticate(issued.Token));
            Assert.DoesNotContain(Convert.ToHexString(issued.Token), File.ReadAllText(Directory.EnumerateFiles(directory).Single()), StringComparison.OrdinalIgnoreCase);
            Assert.True(reloaded.Revoke(issued.Account.Id));
            Assert.Null(store.Authenticate(issued.Token));
            Assert.Null(new FileAccountStore(directory).Authenticate(issued.Token));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}
