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

/// <summary>마지막 참가자가 나갈 때 시작 중인 예비 서버도 정상 정리한다.</summary>
public sealed class HostMigrationStartupTests
{
    [Theory]
    [InlineData(WebSocketCloseStatus.NormalClosure, false)]
    [InlineData(WebSocketCloseStatus.PolicyViolation, false)]
    [InlineData(WebSocketCloseStatus.NormalClosure, true)]
    public async Task StandbyClosedBeforeInitialReadyDoesNotClaimReadiness(WebSocketCloseStatus status, bool retire)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var relay = builder.Build();
        relay.UseWebSockets();
        relay.MapGet("/rooms/{id}/standby", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            byte[] authentication = new byte[32];
            await socket.ReceiveAsync(authentication.AsMemory(), timeout.Token);
            if (retire) await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { kind = "retire" }).AsMemory(),
                WebSocketMessageType.Text, true, timeout.Token);
            await socket.CloseOutputAsync(status, "", timeout.Token);
        });
        await relay.StartAsync(timeout.Token);
        try
        {
            string address = relay.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var input = new StringReader(JsonSerializer.Serialize(new
            {
                serviceUrl = address, roomId = Guid.NewGuid().ToString("N"), hostKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                standby = true, generation = 2
            }));
            using var output = new StringWriter();
            Task runtime = PlayerHostedServer.Run(GameRulesTests.Rules(), new NetworkSettings(GameRulesTests.Data()),
                input, output, timeout.Token, () =>
                {
                    var socket = new ClientWebSocket();
                    socket.Options.RemoteCertificateValidationCallback = (_, presented, _, errors) =>
                        presented != null && (errors == SslPolicyErrors.None || errors == SslPolicyErrors.RemoteCertificateChainErrors) &&
                        presented.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData);
                    return socket;
                });
            if (status == WebSocketCloseStatus.NormalClosure) await runtime;
            else await Assert.ThrowsAsync<InvalidDataException>(() => runtime);
            Assert.Empty(output.ToString());
        }
        finally { await relay.StopAsync(CancellationToken.None); }
    }
}
