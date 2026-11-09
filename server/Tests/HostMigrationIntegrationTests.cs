using System.Collections.Concurrent;
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
using System.Threading.Channels;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>인증된 실제 WSS 연결을 유지한 채 방장 프로세스와 대기실·전장 상태를 승계한다.</summary>
public sealed class HostMigrationIntegrationTests
{
    [Fact]
    public async Task StartingAfterALongLobbyGetsItsOwnTickProgressDeadline()
    {
        var clock = new MigrationClock();
        await using var fixture = await MigrationRoom.Create(startOriginal: false, clock);
        using var control = fixture.Socket();
        await control.ConnectAsync(fixture.Endpoint("host"), fixture.Stop);
        await control.SendAsync(Convert.FromHexString(fixture.HostKey).AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        Assert.Equal("ready", (await ControlMessage()).GetProperty("kind").GetString());
        await Report(0, false);
        Assert.Equal("listed", (await ControlMessage()).GetProperty("kind").GetString());
        await using Player host = await fixture.Join(fixture.Creator, "방장");
        JsonElement invite;
        do { invite = await ControlMessage(); } while (invite.GetProperty("kind").GetString() != "peer");
        using var bridge = fixture.Socket();
        await bridge.ConnectAsync(fixture.Endpoint("bridge"), fixture.Stop);
        await bridge.SendAsync(Convert.FromHexString(invite.GetProperty("bridgeKey").GetString()!).AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        var welcome = new NetDataWriter();
        Protocol.WriteWelcome(welcome, 1, roomPlayers: 2);
        await bridge.SendAsync(welcome.CopyData().AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        await host.Read(PacketKind.Welcome, 1);
        await Report(1, false);
        clock.Advance(TimeSpan.FromSeconds(4));
        await Report(1, true);
        await fixture.WaitUntil(async () => (await fixture.Rooms())[0].GetProperty("running").GetBoolean());
        await Task.Delay(750, fixture.Stop);
        Assert.Single((await fixture.Rooms()).EnumerateArray());
        Assert.False(host.Closed.Task.IsCompleted);

        // 전장 시작 이후에도 틱이 멎으면 하트비트 수신과 별개로 장애를 감지한다.
        clock.Advance(TimeSpan.FromSeconds(4));
        await Report(1, true);
        await fixture.WaitUntil(async () => (await fixture.Rooms()).GetArrayLength() == 0);

        async Task Report(int count, bool running) => await control.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        { kind = "state", count, running, tick = 0, generation = 1UL }).AsMemory(), WebSocketMessageType.Text, true, fixture.Stop);
        async Task<JsonElement> ControlMessage()
        {
            var buffer = new byte[1024];
            var message = await control.ReceiveAsync(buffer.AsMemory(), fixture.Stop);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, message.Count));
            return json.RootElement.Clone();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealTlsRoomMigratesInLobbyAndGameWithoutDisconnectingRemainingPlayers(bool crash, bool inGame)
    {
        await using var fixture = await MigrationRoom.Create();
        await using Player host = await fixture.Join(fixture.Creator, "방장");
        await host.Lobby(_ => true);
        await fixture.WaitUntil(async () => (await fixture.Rooms())[0].GetProperty("count").GetInt32() == 1);
        await using Player guest = await fixture.Join(fixture.Guest, "친구");
        await guest.Lobby(lobby => lobby.Members.Length == 2);
        await host.Send([(byte)PacketKind.LobbyAiCount, 1]);
        await guest.Lobby(lobby => lobby.AiCount == 1);
        await guest.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
        await guest.Lobby(lobby => lobby.Members.Any(member => member.Nickname == "친구" && member.Ready));
        var writer = new NetDataWriter();
        HostMigrationProtocol.WriteCapability(writer, true);
        await guest.Send(writer.CopyData());
        await guest.StandbyReady.Task.WaitAsync(fixture.Stop);

        int hostId = -1, guestId = -1;
        if (inGame)
        {
            await host.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
            await host.Send([(byte)PacketKind.LobbyStart]);
            hostId = Owner(await host.Read(PacketKind.Welcome, 1));
            guestId = Owner(await guest.Read(PacketKind.Welcome, 1));
            Assert.NotEqual(hostId, guestId);
            Protocol.WriteMove(writer, 1, Vector2.UnitX);
            await guest.Send(writer.CopyData());
            await guest.Read(PacketKind.Accepted, 1);
            await guest.Pose(1, 1);
        }

        if (crash) fixture.Original.Stop.Cancel();
        else
        {
            HostMigrationProtocol.WriteLeave(writer);
            await host.Send(writer.CopyData());
        }

        if (inGame)
        {
            Assert.Equal(guestId, Owner(await guest.Read(PacketKind.Welcome, 2)));
            if (crash) Assert.Equal(hostId, Owner(await host.Read(PacketKind.Welcome, 2)));
            else
            {
                Protocol.WriteMove(writer, 1, Vector2.UnitY);
                await guest.Send(writer.CopyData());
                byte[] refusal = await guest.Read(PacketKind.Rejected, 2);
                Assert.Equal((byte)CommandAdmission.StaleSequence, refusal[9]);
            }
            Protocol.WriteMove(writer, 2, Vector2.UnitX);
            await guest.Send(writer.CopyData(), 1);
            Protocol.WriteMove(writer, 2, Vector2.UnitY);
            await guest.Send(writer.CopyData());
            Assert.Equal(2UL, BitConverter.ToUInt64(await guest.Read(PacketKind.Accepted, 2), 1));
            Assert.Equal(Vector2.UnitY, (await guest.Pose(2, 2)).Direction);
        }
        else
        {
            LobbyState lobby = await guest.Lobby(state => state.Members.Length == (crash ? 2 : 1), 2);
            if (crash)
            {
                // 강제 종료는 마지막 백업 이후의 준비·AI 설정을 되돌릴 수 있다. 현재 접속자는 유지한다.
                Assert.InRange(lobby.AiCount, 0, 1);
                int changedAiCount = 1 - lobby.AiCount;
                await guest.Send([(byte)PacketKind.LobbyAiCount, (byte)changedAiCount]);
                lobby = await guest.Lobby(state => state.Members.Length == 2 && state.AiCount == changedAiCount, 2);
                await guest.Send([(byte)PacketKind.LobbyAiCount, 1]);
                await guest.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
                if (lobby.AiCount != 1 || !lobby.Members[0].Ready)
                    lobby = await guest.Lobby(state => state.Members.Length == 2 && state.AiCount == 1 && state.Members[0].Ready, 2);
            }
            Assert.Equal(1, lobby.AiCount);
            Assert.Equal("친구", lobby.Members[0].Nickname);
            Assert.True(lobby.Members[0].Ready);
            Assert.Equal(fixture.Element, lobby.Members[0].Element);
        }

        Assert.False(guest.Closed.Task.IsCompleted);
        Assert.True(guest.IsHost);
        if (!crash && !inGame)
        {
            Runtime promoted = await guest.StandbyReady.Task;
            await Task.Delay(1200, fixture.Stop);
            await fixture.WaitUntil(() => Task.FromResult(promoted.OpenedSockets >= 4));
            // 제어·다운로드·참가 bridge·업로드 하나씩이며 최종 복원은 다운로드 TLS를 재사용한다.
            Assert.Equal(4, promoted.OpenedSockets);
        }
        Assert.Equal(fixture.RoomId, Assert.Single((await fixture.Rooms()).EnumerateArray()).GetProperty("id").GetString());
        await fixture.Post($"/rooms/{fixture.RoomId}/tickets", new
        {
            version = Protocol.Version, dataHash = fixture.Rules.DataHash, startElement = fixture.Element, password = "9999"
        }, fixture.Stranger, HttpStatusCode.Forbidden);

        // AI가 남아 있어도 마지막 실제 참가자가 나가면 방을 종료한다.
        await host.Close();
        await guest.Close();
        await fixture.WaitUntil(async () => (await fixture.Rooms()).GetArrayLength() == 0);
        fixture.AssertRuntimesHealthy();
    }

    [Fact]
    public async Task RoomDoesNotAdmitANewPlayerWhileItsCandidateIsRecovering()
    {
        await using var fixture = await MigrationRoom.Create();
        await using Player host = await fixture.Join(fixture.Creator, "방장");
        await host.Lobby(_ => true);
        await fixture.WaitUntil(async () => (await fixture.Rooms())[0].GetProperty("count").GetInt32() == 1);
        await using Player guest = await fixture.Join(fixture.Guest, "친구");
        guest.AutoLaunchStandby = false;
        await guest.Lobby(state => state.Members.Length == 2);
        var writer = new NetDataWriter();
        HostMigrationProtocol.WriteCapability(writer, true);
        await guest.Send(writer.CopyData());
        await guest.StandbyOffered.Task.WaitAsync(fixture.Stop);
        fixture.Original.Stop.Cancel();
        HostMigrationNotice notice;
        do
        {
            Assert.True(HostMigrationProtocol.TryReadMigration(new NetDataReader(await guest.Read(PacketKind.HostMigration, 1)), out notice));
        } while (notice.Phase != MigrationPhase.Recovering);

        JsonElement denied = await fixture.Post($"/rooms/{fixture.RoomId}/tickets", new
        {
            version = Protocol.Version, dataHash = fixture.Rules.DataHash, startElement = fixture.Element, password = "0427"
        }, fixture.Stranger, HttpStatusCode.ServiceUnavailable);

        Assert.Equal("unavailable", denied.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ADefeatedPlayersBridgeClosureDoesNotMigrateTheHealthyHost()
    {
        await using var fixture = await MigrationRoom.Create(startOriginal: false);
        using var control = fixture.Socket();
        await control.ConnectAsync(fixture.Endpoint("host"), fixture.Stop);
        await control.SendAsync(Convert.FromHexString(fixture.HostKey).AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        Assert.Equal("ready", (await ControlMessage()).GetProperty("kind").GetString());
        await Report(0, 0);
        Assert.Equal("listed", (await ControlMessage()).GetProperty("kind").GetString());
        await using Player host = await fixture.Join(fixture.Creator, "방장");
        using ClientWebSocket hostBridge = await LinkNextPlayer(1);
        Assert.Equal(1, Owner(await host.Read(PacketKind.Welcome, 1)));
        await Report(1, 1);
        await fixture.WaitUntil(async () => (await fixture.Rooms())[0].GetProperty("count").GetInt32() == 1);
        await using Player guest = await fixture.Join(fixture.Guest, "패배 참가자");
        using ClientWebSocket guestBridge = await LinkNextPlayer(2);
        Assert.Equal(2, Owner(await guest.Read(PacketKind.Welcome, 1)));
        await Report(2, 2);

        byte[] defeat = [(byte)PacketKind.SessionEnded, (byte)SessionEndReason.BaseDestroyed];
        await guestBridge.SendAsync(defeat.AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        Assert.Equal(defeat, await guest.Read(PacketKind.SessionEnded, 1));
        await guestBridge.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", fixture.Stop);
        var buffer = new byte[OnlineFrame.MaxMessageBytes];
        try { Assert.Equal(WebSocketMessageType.Close, (await guestBridge.ReceiveAsync(buffer.AsMemory(), fixture.Stop)).MessageType); }
        catch (WebSocketException) { Assert.NotEqual(WebSocketState.Open, guestBridge.State); }

        Assert.Single((await fixture.Rooms()).EnumerateArray());
        Assert.False(host.Closed.Task.IsCompleted);
        Assert.True(host.IsHost);
        await guest.Close();
        await Report(1, 3);
        Assert.Single((await fixture.Rooms()).EnumerateArray());

        async Task Report(int count, long tick) => await control.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "state", count, running = count > 0, tick, generation = 1UL
        }).AsMemory(), WebSocketMessageType.Text, true, fixture.Stop);

        async Task<JsonElement> ControlMessage()
        {
            var bytes = new byte[1024];
            var message = await control.ReceiveAsync(bytes.AsMemory(), fixture.Stop);
            Assert.Equal(WebSocketMessageType.Text, message.MessageType);
            Assert.True(message.EndOfMessage);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, message.Count));
            return json.RootElement.Clone();
        }

        async Task<ClientWebSocket> LinkNextPlayer(int owner)
        {
            JsonElement invite;
            do { invite = await ControlMessage(); } while (invite.GetProperty("kind").GetString() != "peer");
            ClientWebSocket bridge = fixture.Socket();
            await bridge.ConnectAsync(fixture.Endpoint("bridge"), fixture.Stop);
            await bridge.SendAsync(Convert.FromHexString(invite.GetProperty("bridgeKey").GetString()!).AsMemory(),
                WebSocketMessageType.Binary, true, fixture.Stop);
            var writer = new NetDataWriter();
            Protocol.WriteWelcome(writer, owner, roomPlayers: 2);
            await bridge.SendAsync(writer.CopyData().AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
            return bridge;
        }
    }

    [Fact]
    public async Task FailedCandidateDoesNotDisconnectItsPlayerAndNextHostCanHandOffAgain()
    {
        await using var fixture = await MigrationRoom.Create();
        await using Player host = await fixture.Join(fixture.Creator, "방장");
        await host.Lobby(_ => true);
        await fixture.WaitUntil(async () => (await fixture.Rooms())[0].GetProperty("count").GetInt32() == 1);
        await using Player guest = await fixture.Join(fixture.Guest, "친구");
        await guest.Lobby(state => state.Members.Length == 2);
        await using Player third = await fixture.Join(fixture.Stranger, "세번째");
        await third.Lobby(state => state.Members.Length == 3);
        var writer = new NetDataWriter();
        HostMigrationProtocol.WriteCapability(writer, true);
        await guest.Send(writer.CopyData());
        Runtime failed = await guest.StandbyReady.Task.WaitAsync(fixture.Stop);
        await third.Send(writer.CopyData());
        await host.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
        await guest.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
        await third.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
        await host.Lobby(state => state.Members.All(member => member.Ready));
        await host.Send([(byte)PacketKind.LobbyStart]);
        int hostId = Owner(await host.Read(PacketKind.Welcome, 1));
        int guestId = Owner(await guest.Read(PacketKind.Welcome, 1));
        int thirdId = Owner(await third.Read(PacketKind.Welcome, 1));

        failed.Stop.Cancel();
        fixture.Original.Stop.Cancel();

        Assert.Equal(thirdId, Owner(await third.Read(PacketKind.Welcome, 2)));
        Assert.Equal(hostId, Owner(await host.Read(PacketKind.Welcome, 2)));
        Assert.Equal(guestId, Owner(await guest.Read(PacketKind.Welcome, 2)));
        Assert.True(third.IsHost);
        Assert.False(guest.Closed.Task.IsCompleted);
        Assert.False(host.Closed.Task.IsCompleted);

        HostMigrationProtocol.WriteCapability(writer, true);
        await host.Send(writer.CopyData());
        await host.StandbyReady.Task.WaitAsync(fixture.Stop);
        HostMigrationProtocol.WriteLeave(writer);
        await third.Send(writer.CopyData());

        Assert.Equal(hostId, Owner(await host.Read(PacketKind.Welcome, 3)));
        Assert.Equal(guestId, Owner(await guest.Read(PacketKind.Welcome, 3)));
        Assert.True(host.IsHost);
        Protocol.WriteMove(writer, 1, Vector2.UnitY);
        await guest.Send(writer.CopyData());
        await guest.Read(PacketKind.Accepted, 3);
        Assert.Equal(Vector2.UnitY, (await guest.Pose(1, 3)).Direction);
        Assert.False(guest.Closed.Task.IsCompleted);
        Assert.Single((await fixture.Rooms()).EnumerateArray());
    }

    [Fact]
    public async Task BackupEndpointsRejectUnknownKeysAndCrossRoomHostKeys()
    {
        await using var fixture = await MigrationRoom.Create();
        await using Player host = await fixture.Join(fixture.Creator, "방장");
        await host.Lobby(_ => true);
        using var unauthorizedUpload = fixture.Socket();
        await unauthorizedUpload.ConnectAsync(fixture.Endpoint("migration/upload"), fixture.Stop);
        await unauthorizedUpload.SendAsync(RandomNumberGenerator.GetBytes(32).AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        var buffer = new byte[OnlineFrame.MaxMessageBytes];
        Assert.Equal(WebSocketMessageType.Close, (await unauthorizedUpload.ReceiveAsync(buffer.AsMemory(), fixture.Stop)).MessageType);

        using var unauthorizedDownload = fixture.Socket();
        await unauthorizedDownload.ConnectAsync(fixture.Endpoint("migration/download"), fixture.Stop);
        await unauthorizedDownload.SendAsync(Convert.FromHexString(fixture.HostKey).AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        Assert.Equal(WebSocketMessageType.Close, (await unauthorizedDownload.ReceiveAsync(buffer.AsMemory(), fixture.Stop)).MessageType);

        JsonElement second = await fixture.Post("/hosted-rooms", new
        {
            title = "다른 방", minimumPlayers = 2, capacity = 2,
            version = Protocol.Version, dataHash = fixture.Rules.DataHash
        }, fixture.Stranger);
        string otherRoom = second.GetProperty("room").GetProperty("id").GetString()!;
        await fixture.StartRuntime(second.GetProperty("hostKey").GetString()!, false, 1, otherRoom);
        using var crossRoom = fixture.Socket();
        await crossRoom.ConnectAsync(fixture.Endpoint("migration/upload", otherRoom), fixture.Stop);
        await crossRoom.SendAsync(Convert.FromHexString(fixture.HostKey).AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        Assert.Equal(WebSocketMessageType.Close, (await crossRoom.ReceiveAsync(buffer.AsMemory(), fixture.Stop)).MessageType);
    }

    [Fact]
    public async Task FailureAfterCommittingAnEpochRetriesWithoutResumingTheFencedSource()
    {
        await using var fixture = await MigrationRoom.Create();
        await using Player host = await fixture.Join(fixture.Creator, "방장");
        await host.Lobby(_ => true);
        await fixture.WaitUntil(async () => (await fixture.Rooms())[0].GetProperty("count").GetInt32() == 1);
        await using Player guest = await fixture.Join(fixture.Guest, "첫 후보");
        guest.AutoLaunchStandby = false;
        await guest.Lobby(state => state.Members.Length == 2);
        await using Player third = await fixture.Join(fixture.Stranger, "대체 후보");
        await third.Lobby(state => state.Members.Length == 3);
        var writer = new NetDataWriter();
        HostMigrationProtocol.WriteCapability(writer, true);
        await guest.Send(writer.CopyData());
        HostStandbyOffer offer = await guest.StandbyOffered.Task.WaitAsync(fixture.Stop);
        using var control = fixture.Socket();
        await control.ConnectAsync(fixture.Endpoint("standby"), fixture.Stop);
        await control.SendAsync(Convert.FromHexString(offer.Key).AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
        Assert.Equal("ready", (await ControlMessage()).GetProperty("kind").GetString());
        using var sendGate = new SemaphoreSlim(1);
        await SendControl(new { kind = "standbyReady", generation = offer.Generation });
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(fixture.Stop);
        Task heartbeat = Heartbeat();
        Task<int> failingCandidate = FailAfterActivation();
        try
        {
            await third.Send(writer.CopyData());
            await host.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
            await guest.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
            await third.Send([(byte)PacketKind.LobbyReady, (byte)fixture.Element, 1]);
            await host.Lobby(state => state.Members.All(member => member.Ready));
            await host.Send([(byte)PacketKind.LobbyStart]);
            int guestId = Owner(await guest.Read(PacketKind.Welcome, 1));
            int thirdId = Owner(await third.Read(PacketKind.Welcome, 1));
            HostMigrationProtocol.WriteLeave(writer);
            await host.Send(writer.CopyData());

            Assert.Equal(2, await failingCandidate.WaitAsync(fixture.Stop));
            Assert.Equal(guestId, Owner(await guest.Read(PacketKind.Welcome, 3)));
            Assert.Equal(thirdId, Owner(await third.Read(PacketKind.Welcome, 3)));
            Assert.True(third.IsHost);
            Assert.False(guest.Closed.Task.IsCompleted);
            Assert.False(third.Closed.Task.IsCompleted);
            // 승계 준비를 마치지 못한 세대를 정상 상태라고 발표해 이전 서버를 다시 쓰면 안 된다.
            Assert.DoesNotContain(guest.MigrationNotices, notice => notice.Generation == 2 && notice.Phase == MigrationPhase.None);
            Protocol.WriteMove(writer, 2, Vector2.UnitX);
            await guest.Send(writer.CopyData(), 2);
            Protocol.WriteMove(writer, 2, Vector2.UnitY);
            await guest.Send(writer.CopyData());
            Assert.Equal(2UL, BitConverter.ToUInt64(await guest.Read(PacketKind.Accepted, 3), 1));
            Assert.Equal(Vector2.UnitY, (await guest.Pose(2, 3)).Direction);
            Assert.Single((await fixture.Rooms()).EnumerateArray());
            await fixture.WaitUntil(() => Task.FromResult(fixture.Original.Execution.IsCompleted));
            fixture.AssertRuntimesHealthy();
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
            control.Abort();
            try { await failingCandidate; }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        }

        async Task<int> FailAfterActivation()
        {
            using var download = fixture.Socket();
            bool downloading = false;
            int restoredCount = 0;
            try
            {
                while (true)
                {
                    JsonElement message = await ControlMessage();
                    switch (message.GetProperty("kind").GetString())
                    {
                        case "restore":
                            if (!downloading)
                            {
                                await download.ConnectAsync(fixture.Endpoint("migration/download"), fixture.Stop);
                                await download.SendAsync(Convert.FromHexString(offer.Key).AsMemory(), WebSocketMessageType.Binary, true, fixture.Stop);
                                downloading = true;
                            }
                            await download.SendAsync("NEXT"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, fixture.Stop);
                            HostMigrationSnapshot snapshot = MigrationSnapshotCodec.Decode(await MigrationSnapshotTransfer.Read(download, fixture.Stop), fixture.Rules);
                            Assert.Equal(fixture.RoomId, snapshot.RoomId);
                            Assert.Equal(1UL, snapshot.Generation);
                            Assert.NotNull(snapshot.World);
                            restoredCount++;
                            await SendControl(new { kind = "readyForActivation", generation = offer.Generation, tick = snapshot.Tick, backupNumber = snapshot.BackupNumber });
                            break;
                        case "activate":
                            heartbeatStop.Cancel();
                            await heartbeat;
                            control.Abort();
                            return restoredCount;
                    }
                }
            }
            finally
            {
                heartbeatStop.Cancel();
                await heartbeat;
            }
        }

        async Task Heartbeat()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                    await SendControl(new { kind = "standbyReady", generation = offer.Generation });
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
        }

        async Task SendControl(object message)
        {
            await sendGate.WaitAsync(fixture.Stop);
            try { await control.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(), WebSocketMessageType.Text, true, fixture.Stop); }
            finally { sendGate.Release(); }
        }

        async Task<JsonElement> ControlMessage()
        {
            var buffer = new byte[1024];
            var message = await control.ReceiveAsync(buffer.AsMemory(), fixture.Stop);
            Assert.Equal(WebSocketMessageType.Text, message.MessageType);
            Assert.True(message.EndOfMessage);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, message.Count));
            return json.RootElement.Clone();
        }
    }

    private static int Owner(byte[] welcome)
    {
        Assert.True(Protocol.TryReadWelcome(new NetDataReader(welcome), out int owner, out _, out _, out _));
        return owner;
    }

    private sealed class MigrationRoom : IAsyncDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("elemental-migration-test-").FullName;
        private readonly RSA rsa = RSA.Create(2048);
        private readonly X509Certificate2 certificate;
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(50));
        private readonly WebApplication relay;
        private readonly ConcurrentBag<Runtime> runtimes = [];
        private HttpClient http = null!;
        private Uri address = null!;
        public GameRules Rules { get; } = GameRulesTests.Rules();
        public NetworkSettings Settings { get; } = new(GameRulesTests.Data());
        public string Creator { get; private set; } = "";
        public string Guest { get; private set; } = "";
        public string Stranger { get; private set; } = "";
        public string RoomId { get; private set; } = "";
        public string HostKey { get; private set; } = "";
        public Runtime Original { get; private set; } = null!;
        public CancellationToken Stop => lifetime.Token;
        public int Element => Rules.StartingElements[0];

        private MigrationRoom(TimeProvider? clock = null)
        {
            var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
            certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
            relay = OnlineGameServer.Build(Rules, Settings, 0, certificate, directory, timeProvider: clock);
        }

        public static async Task<MigrationRoom> Create(bool startOriginal = true, TimeProvider? clock = null)
        {
            var fixture = new MigrationRoom(clock);
            try
            {
                await fixture.relay.StartAsync(fixture.Stop);
                fixture.address = new Uri(fixture.relay.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                fixture.http = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, presented, _, errors) => fixture.TestCertificate(presented, errors)
                }) { BaseAddress = fixture.address };
                fixture.Creator = (await fixture.Post("/accounts", new { nickname = "방장" })).GetProperty("token").GetString()!;
                fixture.Guest = (await fixture.Post("/accounts", new { nickname = "친구" })).GetProperty("token").GetString()!;
                fixture.Stranger = (await fixture.Post("/accounts", new { nickname = "암호검증" })).GetProperty("token").GetString()!;
                JsonElement created = await fixture.Post("/hosted-rooms", new
                {
                    title = "승계 통합 검증", minimumPlayers = 2, capacity = 3, password = "0427",
                    version = Protocol.Version, dataHash = fixture.Rules.DataHash
                }, fixture.Creator);
                fixture.RoomId = created.GetProperty("room").GetProperty("id").GetString()!;
                fixture.HostKey = created.GetProperty("hostKey").GetString()!;
                if (startOriginal) fixture.Original = await fixture.StartRuntime(fixture.HostKey, false, 1);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<Player> Join(string account, string name)
        {
            JsonElement ticket = await Post($"/rooms/{RoomId}/tickets", new
            {
                version = Protocol.Version, dataHash = Rules.DataHash, startElement = Element, password = "0427"
            }, account);
            ClientWebSocket socket = Socket();
            await socket.ConnectAsync(Endpoint("play"), Stop);
            await socket.SendAsync(Convert.FromHexString(ticket.GetProperty("ticket").GetString()!).AsMemory(), WebSocketMessageType.Binary, true, Stop);
            return new Player(this, socket, name);
        }

        public async Task<Runtime> StartRuntime(string key, bool standby, ulong generation, string? roomId = null)
        {
            var runtime = new Runtime(this, JsonSerializer.Serialize(new
            {
                serviceUrl = address.GetLeftPart(UriPartial.Authority), roomId = roomId ?? RoomId, hostKey = key, standby, generation
            }));
            runtimes.Add(runtime);
            Task<string?> line = runtime.Reader.ReadLineAsync(Stop).AsTask();
            if (await Task.WhenAny(line, runtime.Execution) == runtime.Execution) await runtime.Execution;
            Assert.Equal(standby ? "STANDBY_READY" : "HOST_READY", await line);
            return runtime;
        }

        public async Task<JsonElement> Post(string path, object body, string? token = null, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            if (token != null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(message, Stop);
            Assert.Equal(expected, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Stop);
        }

        public async Task<JsonElement> Rooms() => (await http.GetFromJsonAsync<JsonElement>("/rooms", Stop)).GetProperty("rooms");
        public Uri Endpoint(string path, string? room = null) => new UriBuilder(address) { Scheme = "wss", Path = $"/rooms/{room ?? RoomId}/{path}" }.Uri;
        public ClientWebSocket Socket()
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, presented, _, errors) => TestCertificate(presented, errors);
            return socket;
        }
        private bool TestCertificate(X509Certificate? presented, SslPolicyErrors errors) =>
            (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && presented?.GetCertHashString() == certificate.Thumbprint;

        public async Task WaitUntil(Func<Task<bool>> predicate)
        {
            while (!await predicate()) await Task.Delay(25, Stop);
        }

        public void AssertRuntimesHealthy()
        {
            foreach (Runtime runtime in runtimes)
                if (runtime.Execution.IsFaulted) throw new InvalidOperationException("승계 서버 실행 실패", runtime.Execution.Exception);
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            foreach (Runtime runtime in runtimes) await runtime.DisposeAsync();
            await relay.StopAsync();
            await relay.DisposeAsync();
            http?.Dispose();
            certificate.Dispose();
            rsa.Dispose();
            lifetime.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private sealed class MigrationClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class Runtime : IAsyncDisposable
    {
        private readonly Pipe pipe = new();
        private readonly StreamWriter writer;
        private readonly StringReader startup;
        public StreamReader Reader { get; }
        public CancellationTokenSource Stop { get; }
        public Task Execution { get; }
        private int openedSockets;
        public int OpenedSockets => Volatile.Read(ref openedSockets);
        public Runtime(MigrationRoom room, string input)
        {
            Stop = CancellationTokenSource.CreateLinkedTokenSource(room.Stop);
            writer = new StreamWriter(pipe.Writer.AsStream());
            Reader = new StreamReader(pipe.Reader.AsStream());
            startup = new StringReader(input);
            Execution = PlayerHostedServer.Run(room.Rules, room.Settings, startup, writer, Stop.Token,
                () => { Interlocked.Increment(ref openedSockets); return room.Socket(); });
        }
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            try { await Execution.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException) { }
            Reader.Dispose();
            await writer.DisposeAsync();
            startup.Dispose();
            Stop.Dispose();
        }
    }

    private sealed class Player : IAsyncDisposable
    {
        private readonly MigrationRoom room;
        private readonly ClientWebSocket socket;
        private readonly Task receiving;
        private readonly Channel<(ulong Generation, byte[] Body)> input = Channel.CreateUnbounded<(ulong, byte[])>();
        private readonly string name;
        private ulong generation = 1;
        public bool IsHost { get; private set; }
        public bool AutoLaunchStandby { get; set; } = true;
        public TaskCompletionSource<HostStandbyOffer> StandbyOffered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Runtime> StandbyReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<HostMigrationNotice> MigrationNotices { get; } = new();

        public Player(MigrationRoom room, ClientWebSocket socket, string name)
        {
            this.room = room;
            this.socket = socket;
            this.name = name;
            receiving = Receive();
        }
        public async Task Send(byte[] body, ulong? epoch = null) => await socket.SendAsync(OnlineFrame.Wrap(epoch ?? generation, body).AsMemory(), WebSocketMessageType.Binary, true, room.Stop);
        public async Task<byte[]> Read(PacketKind kind, ulong epoch)
        {
            while (await input.Reader.WaitToReadAsync(room.Stop))
                while (input.Reader.TryRead(out var packet))
                    if (packet.Generation == epoch && packet.Body[0] == (byte)kind) return packet.Body;
            throw new IOException($"{name}의 {kind} 수신 전 연결이 종료됐습니다.");
        }
        public async Task<LobbyState> Lobby(Func<LobbyState, bool> predicate, ulong epoch = 1)
        {
            while (true)
            {
                Assert.True(M5Protocol.TryReadLobbyState(new NetDataReader(await Read(PacketKind.LobbyState, epoch)), room.Rules, out LobbyState state));
                if (predicate(state)) return state;
            }
        }
        public async Task<LegionPose> Pose(ulong sequence, ulong epoch)
        {
            while (true)
            {
                Assert.True(Protocol.TryReadPose(new NetDataReader(await Read(PacketKind.Pose, epoch)), room.Rules, out var pose));
                if (pose.AppliedSequence >= sequence) return pose;
            }
        }
        public async Task Close()
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", room.Stop);
        }
        private async Task Receive()
        {
            var buffer = new byte[OnlineFrame.MaxMessageBytes];
            try
            {
                while (!room.Stop.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync(buffer.AsMemory(), room.Stop);
                    if (packet.MessageType == WebSocketMessageType.Close) break;
                    Assert.Equal(WebSocketMessageType.Binary, packet.MessageType);
                    Assert.True(packet.EndOfMessage);
                    Assert.True(OnlineFrame.TryRead(buffer, packet.Count, out ulong epoch, out byte[] body));
                    if (body[0] == (byte)PacketKind.HostMigration)
                    {
                        Assert.True(HostMigrationProtocol.TryReadMigration(new NetDataReader(body), out var notice));
                        MigrationNotices.Enqueue(notice);
                        if (notice.Phase is MigrationPhase.Resyncing or MigrationPhase.None)
                        { generation = notice.Generation; IsHost = notice.IsHost; }
                    }
                    if (body[0] == (byte)PacketKind.HostStandbyOffer)
                    {
                        Assert.True(HostMigrationProtocol.TryReadStandby(new NetDataReader(body), out var offer));
                        StandbyOffered.TrySetResult(offer);
                        if (AutoLaunchStandby)
                        {
                            Runtime standby = await room.StartRuntime(offer.Key, true, offer.Generation);
                            StandbyReady.TrySetResult(standby);
                        }
                    }
                    await input.Writer.WriteAsync((epoch, body), room.Stop);
                }
                input.Writer.TryComplete();
            }
            catch (Exception error)
            {
                StandbyReady.TrySetException(error);
                input.Writer.TryComplete(error);
            }
            finally { Closed.TrySetResult(); }
        }
        public async ValueTask DisposeAsync()
        {
            socket.Abort();
            await receiving.WaitAsync(TimeSpan.FromSeconds(5));
            socket.Dispose();
        }
    }
}
