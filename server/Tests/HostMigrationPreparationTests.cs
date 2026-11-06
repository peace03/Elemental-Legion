using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>실제 복원과 참가자 검증을 마친 후보만 승계 준비를 알릴 수 있다.</summary>
public sealed class HostMigrationPreparationTests
{
    [Fact]
    public async Task InvalidCurrentRosterCannotReceiveRestoreAcknowledgment()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        string id = Guid.NewGuid().ToString("N"), old = Guid.NewGuid().ToString("N"), candidate = Guid.NewGuid().ToString("N");
        var snapshot = new HostMigrationSnapshot
        {
            RoomId = id, Title = "복원 준비 검증", DataHash = rules.DataHash, Generation = 1, BackupNumber = 1,
            MinimumPlayers = 2, Capacity = 2,
            Members = [new(old, "이전 방장", rules.StartingElements[0], false, 0), new(candidate, "새 방장", rules.StartingElements[0], false, 0)]
        };
        byte[] compressed = MigrationSnapshotCodec.Encode(snapshot);
        bool acknowledged = false;
        bool transferred = false;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var relay = builder.Build(); relay.UseWebSockets();
        relay.MapGet("/rooms/{room}/standby", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            try
            {
                await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                await Send(socket, new { kind = "ready", title = snapshot.Title, minimumPlayers = 2, capacity = 2,
                    creatorId = candidate, creatorNickname = "새 방장", version = Protocol.Version, dataHash = rules.DataHash, generation = 2 });
                await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                // 후보 자신이 빠진 명단은 실제 런타임 복원에서 반드시 거절되어야 한다.
                await Send(socket, new { kind = "restore", snapshotGeneration = 1, creatorId = candidate,
                    creatorNickname = "새 방장", accounts = new[] { old } });
                while (true)
                {
                    var frame = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) break;
                    using var message = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
                    if (message.RootElement.GetProperty("kind").GetString() != "readyForActivation") continue;
                    acknowledged = true;
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                    break;
                }
            }
            catch (WebSocketException) { }
        });
        relay.MapGet("/rooms/{room}/migration/download", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64];
            await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            await MigrationSnapshotTransfer.Write(socket, compressed, timeout.Token);
            transferred = true;
            try { await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token); }
            catch (WebSocketException) { }
        });
        await relay.StartAsync(timeout.Token);
        try
        {
            string address = relay.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var input = new StringReader(JsonSerializer.Serialize(new
            {
                serviceUrl = address, roomId = id, hostKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                standby = true, generation = 2
            }));
            using var output = new StringWriter();
            try { await PlayerHostedServer.Run(rules, settings, input, output, timeout.Token, TestSocket); }
            catch (InvalidDataException) { }
            Assert.Contains("STANDBY_READY", output.ToString());
            Assert.True(transferred);
            Assert.False(acknowledged);
        }
        finally { await relay.StopAsync(CancellationToken.None); }

        ClientWebSocket TestSocket()
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, presented, _, errors) =>
                presented != null && (errors == SslPolicyErrors.None || errors == SslPolicyErrors.RemoteCertificateChainErrors) &&
                presented.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData);
            return socket;
        }
        Task Send(WebSocket socket, object message) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(),
            WebSocketMessageType.Text, true, timeout.Token).AsTask();
    }
}
