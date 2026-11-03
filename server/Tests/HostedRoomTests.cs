using System.IO.Pipelines;
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
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>중계와 방장 런타임을 실제 루프백 TLS로 연결한다.</summary>
public sealed class HostedRoomTests
{
    [Fact]
    public void HostProcessAcceptsStartupDataOnlyThroughStandardInput()
    {
        Assert.True(ServerOptions.Parse(["--host-room"], 15).HostRoom);
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--host-room", "--online"], 15));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--host-room", "--port", "443"], 15));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0427")]
    public async Task HostRegistersStartsAndRelaysARoomWithOptionalPin(string? password)
    {
        string directory = Directory.CreateTempSubdirectory("elemental-hosted-test-").FullName;
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken stop = timeout.Token;
        using var hostStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await using var relay = OnlineGameServer.Build(rules, settings, 0, certificate, directory);
        Task? runtime = null;
        try
        {
            await relay.StartAsync(stop);
            var address = new Uri(relay.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, presented, _, errors) => IsTestCertificate(presented, errors)
            }) { BaseAddress = address };
            string creator = (await Post("/accounts", new { nickname = "방장" })).GetProperty("token").GetString()!;
            string guest = (await Post("/accounts", new { nickname = "친구" })).GetProperty("token").GetString()!;
            var body = new
            {
                title = "같이 하는 방", minimumPlayers = 2, capacity = 3, password,
                version = Protocol.Version, dataHash = rules.DataHash
            };
            var created = await Post("/hosted-rooms", body, creator);
            string id = created.GetProperty("room").GetProperty("id").GetString()!;
            string hostKey = created.GetProperty("hostKey").GetString()!;
            Assert.Empty((await List()).EnumerateArray());
            var another = new { title = "중복 생성 검증", minimumPlayers = 2, capacity = 2 };
            Assert.Equal("alreadyInRoom", (await Post("/rooms", another, creator, HttpStatusCode.Conflict)).GetProperty("error").GetString());

            var readiness = new Pipe();
            await using var output = new StreamWriter(readiness.Writer.AsStream());
            using var ready = new StreamReader(readiness.Reader.AsStream());
            using var startup = new StringReader(JsonSerializer.Serialize(new { serviceUrl = address.GetLeftPart(UriPartial.Authority), roomId = id, hostKey }));
            runtime = PlayerHostedServer.Run(rules, settings, startup, output, hostStop.Token, TestSocket);
            Assert.Equal("HOST_READY", await ready.ReadLineAsync(stop));

            var listing = Assert.Single((await List()).EnumerateArray());
            Assert.Equal(id, listing.GetProperty("id").GetString());
            Assert.Equal("같이 하는 방", listing.GetProperty("title").GetString());
            Assert.True(listing.GetProperty("playerHosted").GetBoolean());
            Assert.Equal(password != null, listing.GetProperty("passwordRequired").GetBoolean());
            Assert.False(listing.TryGetProperty("password", out _));
            Assert.False(listing.TryGetProperty("hostKey", out _));

            string ticketRoute = $"/rooms/{id}/tickets";
            int element = rules.StartingElements[0];
            object TicketBody(string? pin) => new { version = Protocol.Version, dataHash = rules.DataHash, startElement = element, password = pin };
            using var host = await Connect(await Post(ticketRoute, TicketBody(null), creator));
            await ReadUntil(host, PacketKind.LobbyState);
            while ((await List())[0].GetProperty("count").GetInt32() == 0) await Task.Delay(75, stop);

            if (password != null)
            {
                Assert.Equal("passwordRequired", (await Post(ticketRoute, TicketBody(null), guest, HttpStatusCode.Forbidden)).GetProperty("error").GetString());
                Assert.Equal("incorrectPassword", (await Post(ticketRoute, TicketBody("4270"), guest, HttpStatusCode.Forbidden)).GetProperty("error").GetString());
                string guesser = (await Post("/accounts", new { nickname = "암호검증" })).GetProperty("token").GetString()!;
                for (int attempt = 0; attempt < 5; attempt++)
                    await Post(ticketRoute, TicketBody("0000"), guesser, HttpStatusCode.Forbidden);
                Assert.Equal("rateLimited", (await Post(ticketRoute, TicketBody(password), guesser, HttpStatusCode.TooManyRequests)).GetProperty("error").GetString());
            }

            using var friend = await Connect(await Post(ticketRoute, TicketBody(password), guest));
            await ReadUntil(friend, PacketKind.LobbyState);
            await Send(host, [(byte)PacketKind.LobbyAiCount, 1]);
            Assert.True(M5Protocol.TryReadLobbyState(new NetDataReader(await ReadUntil(friend, PacketKind.LobbyState)), rules, out var lobby));
            Assert.Equal(1, lobby.AiCount);
            Assert.Equal(2, lobby.Members.Length);
            while ((await List())[0].GetProperty("count").GetInt32() != 3) await Task.Delay(75, stop);
            await Send(friend, [(byte)PacketKind.LobbyStart]);
            Assert.Equal((byte)LobbyError.NotHost, (await ReadUntil(friend, PacketKind.LobbyError))[1]);
            await Send(friend, [(byte)PacketKind.LobbyReady, (byte)element, 1]);
            await ReadUntil(friend, PacketKind.LobbyState);
            await Send(host, [(byte)PacketKind.LobbyReady, (byte)element, 1]);
            await Send(host, [(byte)PacketKind.LobbyStart]);
            byte[] hostWelcome = await ReadUntil(host, PacketKind.Welcome);
            byte[] friendWelcome = await ReadUntil(friend, PacketKind.Welcome);
            Assert.True(Protocol.TryReadWelcome(new NetDataReader(hostWelcome), out int hostOwner, out _, out _, out int players));
            Assert.True(Protocol.TryReadWelcome(new NetDataReader(friendWelcome), out int friendOwner, out _, out _, out int friendPlayers));
            Assert.Equal(3, players);
            Assert.Equal(players, friendPlayers);
            Assert.NotEqual(hostOwner, friendOwner);
            var fog = new FogStateReplica(rules.ForRoom(players));
            Assert.True(fog.BindPlayer(friendOwner));
            Assert.True(fog.TryAccept(new NetDataReader(await ReadUntil(friend, PacketKind.FogBaseline))));
            Assert.Equal(30, fog.Width);
            var writer = new NetDataWriter();
            Protocol.WriteMove(writer, 1, Vector2.UnitX);
            await Send(friend, writer.CopyData());
            Assert.Equal(1UL, BitConverter.ToUInt64(await ReadUntil(friend, PacketKind.Accepted), 1));

            await friend.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", stop);
            while ((await List())[0].GetProperty("count").GetInt32() != 2) await Task.Delay(75, stop);
            using var resumed = await Connect(await Post(ticketRoute, TicketBody(password), guest));
            Assert.True(Protocol.TryReadWelcome(new NetDataReader(await ReadUntil(resumed, PacketKind.Welcome)),
                out int resumedOwner, out bool wasResumed, out _, out int resumedPlayers));
            Assert.True(wasResumed);
            Assert.Equal(players, resumedPlayers);
            Assert.Equal(friendOwner, resumedOwner);

            var accounts = new FileAccountStore(Path.Combine(directory, "accounts"));
            AccountIdentity friendAccount = Assert.IsType<AccountIdentity>(accounts.Authenticate(Convert.FromHexString(guest)));
            Assert.True(accounts.Revoke(friendAccount.Id));
            var buffer = new byte[Protocol.MaxDatagramBytes];
            try { while ((await resumed.ReceiveAsync(buffer.AsMemory(), stop)).MessageType != WebSocketMessageType.Close) { } }
            catch (WebSocketException) { Assert.NotEqual(WebSocketState.Open, resumed.State); }
            Assert.Equal("invalidAccount", (await Post(ticketRoute, TicketBody(password), guest, HttpStatusCode.Unauthorized)).GetProperty("error").GetString());

            hostStop.Cancel();
            await runtime.WaitAsync(stop);
            while ((await List()).GetArrayLength() != 0) await Task.Delay(75, stop);

            async Task<JsonElement> List() => (await http.GetFromJsonAsync<JsonElement>("/rooms", stop)).GetProperty("rooms");

            async Task<JsonElement> Post(string path, object payload, string? token = null, HttpStatusCode expected = HttpStatusCode.OK)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
                if (token != null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(message, stop);
                Assert.Equal(expected, response.StatusCode);
                return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: stop);
            }

            async Task<ClientWebSocket> Connect(JsonElement ticket)
            {
                var socket = TestSocket();
                await socket.ConnectAsync(new UriBuilder(address) { Scheme = "wss", Path = $"/rooms/{id}/play" }.Uri, stop);
                await Send(socket, Convert.FromHexString(ticket.GetProperty("ticket").GetString()!));
                return socket;
            }

            async Task Send(WebSocket socket, byte[] bytes) => await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, stop);

            async Task<byte[]> ReadUntil(WebSocket socket, PacketKind kind)
            {
                var buffer = new byte[Protocol.MaxDatagramBytes];
                while (true)
                {
                    var packet = await socket.ReceiveAsync(buffer.AsMemory(), stop);
                    Assert.Equal(WebSocketMessageType.Binary, packet.MessageType);
                    Assert.True(packet.EndOfMessage);
                    if (packet.Count > 0 && buffer[0] == (byte)kind) return buffer[..packet.Count];
                }
            }

            ClientWebSocket TestSocket()
            {
                var socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = (_, presented, _, errors) => IsTestCertificate(presented, errors);
                return socket;
            }

            bool IsTestCertificate(X509Certificate? presented, SslPolicyErrors errors) =>
                (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && presented?.GetCertHashString() == certificate.Thumbprint;
        }
        finally
        {
            hostStop.Cancel();
            try { if (runtime != null) await runtime.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (OperationCanceledException) when (hostStop.IsCancellationRequested) { }
            finally
            {
                await relay.StopAsync();
                Directory.Delete(directory, true);
            }
        }
    }
}
