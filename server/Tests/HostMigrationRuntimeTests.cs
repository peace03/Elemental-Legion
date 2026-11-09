using System.IO.Compression;
using System.Net.WebSockets;
using System.Threading.Channels;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using System.Numerics;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>방 런타임의 승계 상태와 제한된 백업 직렬화를 검사한다.</summary>
public sealed class HostMigrationRuntimeTests
{
    [Fact]
    public async Task RestoredPeerReceivesACompleteBaselineBeforeTheFirstStep()
    {
        var rules = GameRulesTests.Rules();
        var roomRules = rules.ForRoom(2);
        var settings = new NetworkSettings(GameRulesTests.Data());
        var first = new AccountIdentity(Guid.NewGuid().ToString("N"), "이전 방장");
        var second = new AccountIdentity(Guid.NewGuid().ToString("N"), "즉시 복구할 참가자");
        var original = new NetworkWorld(roomRules, settings);
        int element = roomRules.StartingElements[0];
        Assert.Equal(JoinFailure.None, original.TryJoin(element, out int firstOwner, first.Nickname));
        Assert.Equal(JoinFailure.None, original.TryJoin(element, out int secondOwner, second.Nickname));
        var snapshot = new HostMigrationSnapshot
        {
            RoomId = Guid.NewGuid().ToString("N"), Title = "첫 틱 전 복구 기준 상태", DataHash = rules.DataHash,
            Generation = 1, BackupNumber = 1, MinimumPlayers = 2, Capacity = 2,
            NextPlayerId = original.NextPlayerId, World = original.World.CaptureMigrationState(),
            Members = [new(first.Id, first.Nickname, element, true, firstOwner), new(second.Id, second.Nickname, element, true, secondOwner)],
            Players = new() { [first.Id] = firstOwner, [second.Id] = secondOwner }
        };
        using var restored = new OnlineRooms(rules, settings);
        restored.RestoreHostedMigration(snapshot, second, [first.Id, second.Id]);
        Assert.Null(restored.AttachHosted(snapshot.RoomId, second, null, out var peer));
        Assert.Null(restored.ExecuteTask);
        using var socket = new BaselineSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task sending = peer!.Run(socket, 20, timeout.Token);
        var fog = new FogStateReplica(roomRules);
        using var gameplay = new GameplayReplica(roomRules);
        var units = new UnitSnapshotAssembler(roomRules);
        LegionPose? pose = null;
        var received = new List<PacketKind>();
        try
        {
            while (!fog.Ready || gameplay.State.Tick < 0 || units.Tick < 0 || !pose.HasValue)
            {
                byte[] packet = await socket.Frames.Reader.ReadAsync(timeout.Token);
                var reader = new NetDataReader(packet);
                PacketKind kind = (PacketKind)packet[0];
                received.Add(kind);
                switch (kind)
                {
                    case PacketKind.Welcome:
                        Assert.True(Protocol.TryReadWelcome(reader, out int owner, out bool resumed, out _, out int players));
                        Assert.Equal(secondOwner, owner); Assert.True(resumed); Assert.Equal(2, players);
                        break;
                    case PacketKind.FogBaseline: fog.TryAccept(reader); break;
                    case PacketKind.Gameplay: gameplay.TryAccept(reader, 0); break;
                    case PacketKind.Pose:
                        Assert.True(Protocol.TryReadPose(reader, roomRules, out var restoredPose));
                        pose = restoredPose;
                        break;
                    case PacketKind.Units: units.TryAccept(reader); break;
                }
                gameplay.TryPublish(fog, out _);
            }
            Assert.Equal(snapshot.Tick, pose!.Value.Tick);
            Assert.Equal(snapshot.Tick, units.Tick);
            Assert.Equal(snapshot.Tick, fog.Tick);
            Assert.Equal(snapshot.Tick, gameplay.State.Tick);
            Assert.Equal(secondOwner, units.PlayerId);
            Assert.Equal(original.World.GetView(secondOwner).Units.Count, units.Count);
            Assert.Equal(fog.Revision, units.Revision);
            Assert.Null(restored.ExecuteTask);
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException($"첫 Step 전 복구 기준 상태가 완성되지 않았습니다. 수신: {string.Join(", ", received)}", error);
        }
        finally
        {
            peer.Abort();
            try { await sending; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task LobbyRecoveryPrioritizesAuthenticatedHumansOverStaleAiSlots()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var first = new AccountIdentity(Guid.NewGuid().ToString("N"), "기존 참가자");
        var second = new AccountIdentity(Guid.NewGuid().ToString("N"), "두 번째 참가자");
        var third = new AccountIdentity(Guid.NewGuid().ToString("N"), "세 번째 참가자");
        using var original = new OnlineRooms(rules, settings);
        Assert.Null(original.Create(first, "현재 참가자 우선 복구", 2, 3, out var room));
        Assert.Null(original.AttachHosted(room!.Id, first, 0, out var firstPeer));
        var writer = new NetDataWriter();
        M5Protocol.WriteLobbyAiCount(writer, 2);
        Assert.True(firstPeer!.Input.Writer.TryWrite(writer.CopyData()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await original.StartAsync(timeout.Token);
        try
        {
            var snapshot = await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token);
            Assert.Null(snapshot.World);
            Assert.Single(snapshot.Members);
            Assert.Equal(2, snapshot.AiCount);
            var decoded = MigrationSnapshotCodec.Decode(MigrationSnapshotCodec.Encode(snapshot), rules);
            using var restored = new OnlineRooms(rules, settings);

            // 백업 이후 AI를 줄여 입장한 사람은 이전 백업의 AI 예약 때문에 밀려나면 안 된다.
            restored.RestoreHostedMigration(decoded, first, [first.Id, second.Id, third.Id]);
            Assert.Null(restored.AttachHosted(room.Id, first, null, out _));
            var stranger = new AccountIdentity(Guid.NewGuid().ToString("N"), "복원 예약에 없는 참가자");
            Assert.Equal("roomFull", restored.AttachHosted(room.Id, stranger, 0, out _));
            Assert.Null(restored.AttachHosted(room.Id, second, 1, out _));
            Assert.Null(restored.AttachHosted(room.Id, third, 2, out _));
            Assert.Equal(3, restored.Status(room.Id)!.Count);
            await restored.StartAsync(timeout.Token);
            try
            {
                var recaptured = await restored.CaptureHostedMigration(room.Id, 2).WaitAsync(timeout.Token);
                Assert.Equal(0, recaptured.AiCount);
                Assert.Equal([first.Id, second.Id, third.Id], recaptured.Members.Select(member => member.AccountId));
            }
            finally { await restored.StopAsync(CancellationToken.None); }
        }
        finally { await original.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LobbyRecoveryAddsAuthenticatedNewHostMissingFromTheLatestBackup(bool newHostConnectsFirst)
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var first = new AccountIdentity(Guid.NewGuid().ToString("N"), "기존 방장");
        var second = new AccountIdentity(Guid.NewGuid().ToString("N"), "백업 이후 참가자");
        using var original = new OnlineRooms(rules, settings);
        Assert.Null(original.Create(first, "대기실 백업 복구", 2, 4, out var room));
        Assert.Null(original.AttachHosted(room!.Id, first, 0, out var firstPeer));
        var writer = new NetDataWriter();
        M5Protocol.WriteLobbyReady(writer, 0, true);
        Assert.True(firstPeer!.Input.Writer.TryWrite(writer.CopyData()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await original.StartAsync(timeout.Token);
        try
        {
            var snapshot = await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token);
            Assert.Null(snapshot.World);
            Assert.True(Assert.Single(snapshot.Members).Ready);
            var decoded = MigrationSnapshotCodec.Decode(MigrationSnapshotCodec.Encode(snapshot), rules);
            using var restored = new OnlineRooms(rules, settings);

            // 중계가 인증한 현재 참가자는 마지막 백업보다 늦게 들어왔어도 대기실을 이어 받는다.
            restored.RestoreHostedMigration(decoded, second, [first.Id, second.Id]);
            var uninvited = new AccountIdentity(Guid.NewGuid().ToString("N"), "현재 접속 명단에 없는 계정");
            Assert.Equal("hostPending", restored.AttachHosted(room.Id, uninvited, 2, out _));
            OnlinePeer? nextHost, previousHost;
            if (newHostConnectsFirst)
            {
                Assert.Null(restored.AttachHosted(room.Id, second, 1, out nextHost));
                Assert.Null(restored.AttachHosted(room.Id, first, null, out previousHost));
            }
            else
            {
                // 별도 WSS 연결은 새 방장보다 기존 참가자가 먼저 도착할 수 있다.
                Assert.Null(restored.AttachHosted(room.Id, first, null, out previousHost));
                Assert.Null(restored.AttachHosted(room.Id, second, 1, out nextHost));
            }
            Assert.False(nextHost!.Ready);
            Assert.Equal(1, nextHost.Element);
            Assert.True(previousHost!.Ready);
            Assert.Equal(0, previousHost.Element);
            Assert.Equal(2, restored.Status(room.Id)!.Count);
            await restored.StartAsync(timeout.Token);
            try
            {
                var recaptured = await restored.CaptureHostedMigration(room.Id, 2).WaitAsync(timeout.Token);
                Assert.Equal([second.Id, first.Id], recaptured.Members.Select(member => member.AccountId));
                Assert.Equal(second.Nickname, recaptured.Members[0].Nickname);
            }
            finally { await restored.StopAsync(CancellationToken.None); }
        }
        finally { await original.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void MigrationRetainsAiMatchVictoryAfterTheLastAiWasRemoved()
    {
        var data = GameRulesTests.Data();
        data.Root["ghost"]!["decaySeconds"] = 0.1;
        data.Root["ghost"]!["reconnectWindowSeconds"] = 0.1;
        var rules = new ElementalLegion.Shared.Data.GameRules(data).ForRoom(2);
        var settings = new NetworkSettings(data);
        var original = new NetworkWorld(rules, settings);
        int element = rules.StartingElements[0];
        Assert.Equal(JoinFailure.None, original.TryJoin(element, out int human, "생존자"));
        Assert.Equal(JoinFailure.None, original.AddAiOpponents(1, element));
        int opponent = Assert.Single(original.World.GetView(human).Standings,
            standing => standing.PlayerId != human).PlayerId;
        Assert.True(original.World.BeginGhost(opponent));
        for (int tick = 0; tick <= rules.GhostDecayTicks; tick++) original.Step();
        Assert.Equal(0, original.LivingAiCount);
        Assert.True(original.HasWinner);
        Assert.Equal(human, Assert.Single(original.World.GetView(human).Standings).PlayerId);
        Assert.Empty(original.CaptureAiMigration());

        var restored = new NetworkWorld(rules, settings, original.World.CaptureMigrationState(),
            original.NextPlayerId, original.CaptureAiMigration(), [human], [human], aiMatch: true);

        Assert.Equal(original.HasWinner, restored.HasWinner);
        Assert.Equal(human, Assert.Single(restored.World.GetView(human).Standings).PlayerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecaptureBeforeAllBridgesAttachKeepsRestoredMemberMetadata(bool attachNewHost)
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var first = new AccountIdentity(Guid.NewGuid().ToString("N"), "첫 참가자");
        var second = new AccountIdentity(Guid.NewGuid().ToString("N"), "차기 방장");
        using var original = new OnlineRooms(rules, settings);
        Assert.Null(original.Create(first, "재승계 대기실", 2, 4, out var room));
        Assert.Null(original.AttachHosted(room!.Id, first, 0, out var firstPeer));
        Assert.Null(original.AttachHosted(room.Id, second, 1, out var secondPeer));
        var writer = new NetDataWriter();
        M5Protocol.WriteLobbyReady(writer, 0, true); firstPeer!.Input.Writer.TryWrite(writer.CopyData());
        M5Protocol.WriteLobbyReady(writer, 1, true); secondPeer!.Input.Writer.TryWrite(writer.CopyData());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await original.StartAsync(timeout.Token);
        try
        {
            var initial = await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token);
            using var restored = new OnlineRooms(rules, settings);
            restored.RestoreHostedMigration(initial, second, [first.Id, second.Id]);
            if (attachNewHost) Assert.Null(restored.AttachHosted(room.Id, second, null, out _));
            await restored.StartAsync(timeout.Token);
            try
            {
                var recaptured = await restored.CaptureHostedMigration(room.Id, 2).WaitAsync(timeout.Token);
                Assert.Equal(2, recaptured.Members.Length);
                foreach (MigrationMember previous in initial.Members)
                    Assert.Equal(previous, Assert.Single(recaptured.Members, member => member.AccountId == previous.AccountId));
                var decoded = MigrationSnapshotCodec.Decode(MigrationSnapshotCodec.Encode(recaptured), rules);
                using var third = new OnlineRooms(rules, settings);
                third.RestoreHostedMigration(decoded, first, [first.Id, second.Id]);
                Assert.Null(third.AttachHosted(room.Id, first, null, out var attached));
                Assert.True(attached!.Ready);
            }
            finally { await restored.StopAsync(CancellationToken.None); }
        }
        finally { await original.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task LobbyRoundTripRetainsReadyMembersAndNewHostOrder()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var first = new AccountIdentity(Guid.NewGuid().ToString("N"), "첫 방장");
        var second = new AccountIdentity(Guid.NewGuid().ToString("N"), "다음 방장");
        using var original = new OnlineRooms(rules, settings);
        Assert.Null(original.Create(first, "승계 대기실", 2, 4, out var room));
        Assert.Null(original.AttachHosted(room!.Id, first, 0, out var firstPeer));
        Assert.Null(original.AttachHosted(room.Id, second, 1, out var secondPeer));
        var writer = new NetDataWriter();
        M5Protocol.WriteLobbyReady(writer, 1, true);
        Assert.True(secondPeer!.Input.Writer.TryWrite(writer.CopyData()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await original.StartAsync(timeout.Token);
        try
        {
            M5Protocol.WriteLobbyAiCount(writer, 2); firstPeer!.Input.Writer.TryWrite(writer.CopyData());
            M5Protocol.WriteLobbyAiDifficulty(writer, 0, AiDifficulty.Easy); firstPeer.Input.Writer.TryWrite(writer.CopyData());
            M5Protocol.WriteLobbyAiDifficulty(writer, 1, AiDifficulty.Hard); firstPeer.Input.Writer.TryWrite(writer.CopyData());
            var snapshot = await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token);
            var decoded = MigrationSnapshotCodec.Decode(MigrationSnapshotCodec.Encode(snapshot), rules);
            using var restored = new OnlineRooms(rules, settings);
            restored.RestoreHostedMigration(decoded, second, [first.Id, second.Id]);
            Assert.Null(restored.AttachHosted(room.Id, second, null, out var nextHost));
            Assert.Null(restored.AttachHosted(room.Id, first, null, out _));
            Assert.Equal(room.Id, restored.Status(room.Id)!.Id);
            Assert.Equal("승계 대기실", restored.Status(room.Id)!.Title);
            Assert.True(nextHost!.Ready);
            Assert.Equal(1, nextHost.Element);
            Assert.Equal(new[] { AiDifficulty.Easy, AiDifficulty.Hard }, decoded.AiDifficulties);
            await restored.StartAsync(timeout.Token);
            try
            {
                var recaptured = await restored.CaptureHostedMigration(room.Id, 2).WaitAsync(timeout.Token);
                Assert.Equal(decoded.AiDifficulties, recaptured.AiDifficulties);
            }
            finally { await restored.StopAsync(CancellationToken.None); }
        }
        finally { await original.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void SnapshotDecoderRejectsOversizedExpansionAndBrokenCompression()
    {
        var rules = GameRulesTests.Rules();
        Assert.Throws<InvalidDataException>(() => MigrationSnapshotCodec.Decode([1, 2, 3], rules));
        using var compressed = new MemoryStream();
        using (var output = new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] block = new byte[64 * 1024];
            for (int index = 0; index <= MigrationSnapshotCodec.MaxExpandedBytes / block.Length; index++) output.Write(block);
        }
        Assert.Throws<InvalidDataException>(() => MigrationSnapshotCodec.Decode(compressed.ToArray(), rules));
    }

    [Fact]
    public async Task RunningRoomPreservesIdsCommandsAiAndFrozenTick()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var first = new AccountIdentity(Guid.NewGuid().ToString("N"), "원래 방장");
        var second = new AccountIdentity(Guid.NewGuid().ToString("N"), "새 방장");
        using var original = new OnlineRooms(rules, settings);
        Assert.Null(original.Create(first, "진행 중 승계", 2, 4, out var room));
        original.RequireHostedBackups(room!.Id);
        Assert.Null(original.AttachHosted(room!.Id, first, 0, out var firstPeer));
        Assert.Null(original.AttachHosted(room.Id, second, 1, out var secondPeer));
        var writer = new NetDataWriter();
        M5Protocol.WriteLobbyReady(writer, 0, true); firstPeer!.Input.Writer.TryWrite(writer.CopyData());
        M5Protocol.WriteLobbyReady(writer, 1, true); secondPeer!.Input.Writer.TryWrite(writer.CopyData());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var firstSocket = new BaselineSocket();
        using var secondSocket = new BaselineSocket();
        Task firstTransport = firstPeer.Run(firstSocket, 20, timeout.Token);
        Task secondTransport = secondPeer.Run(secondSocket, 20, timeout.Token);
        await original.StartAsync(timeout.Token);
        try
        {
            await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token);
            M5Protocol.WriteLobbyAiCount(writer, 1); firstPeer.Input.Writer.TryWrite(writer.CopyData());
            M5Protocol.WriteLobbyAiDifficulty(writer, 0, AiDifficulty.Hard); firstPeer.Input.Writer.TryWrite(writer.CopyData());
            M5Protocol.WriteLobbyStart(writer); firstPeer.Input.Writer.TryWrite(writer.CopyData());
            var started = await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token);
            Assert.NotNull(started.World);
            Assert.Equal(AiDifficulty.Hard, Assert.Single(started.Ai).Difficulty);
            Assert.Equal(new[] { AiDifficulty.Hard }, started.AiDifficulties);
            Assert.Equal(0, started.Tick);
            await Task.Delay(100, timeout.Token);
            Assert.Equal(0, (await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token)).Tick);
            original.ConfirmHostedBackup(room.Id, hasWorld: true);
            await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token);
            Protocol.WriteMove(writer, 7, Vector2.UnitX); firstPeer.Input.Writer.TryWrite(writer.CopyData());
            var snapshot = await original.CaptureHostedMigration(room.Id, 1, freeze: true).WaitAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.Equal(snapshot.Tick, (await original.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token)).Tick);
            var decoded = MigrationSnapshotCodec.Decode(MigrationSnapshotCodec.Encode(snapshot), rules);
            using var restored = new OnlineRooms(rules, settings);
            restored.RestoreHostedMigration(decoded, second, [first.Id, second.Id]);
            Assert.Null(restored.AttachHosted(room.Id, second, null, out var newHost));
            Assert.Null(restored.AttachHosted(room.Id, first, null, out var previousHost));
            Assert.Equal(snapshot.Players[first.Id], previousHost!.Owner);
            Assert.Equal(snapshot.Players[second.Id], newHost!.Owner);
            Assert.Single(decoded.Ai);
            Assert.Equal(AiDifficulty.Hard, Assert.Single(decoded.Ai).Difficulty);
            // 전장은 계정별 기존 플레이어 ID가 필수이므로 백업에 없는 계정을 방장으로 받아들이지 않는다.
            var outsider = new AccountIdentity(Guid.NewGuid().ToString("N"), "전장 백업에 없는 참가자");
            using var rejected = new OnlineRooms(rules, settings);
            Assert.Throws<InvalidDataException>(() => rejected.RestoreHostedMigration(decoded, outsider,
                [first.Id, second.Id, outsider.Id]));
            var state = SimulationWorld.RestoreMigrationState(rules, decoded.World!);
            Assert.Equal(7UL, state.GetView(previousHost.Owner).AppliedSequence);
            Assert.Equal(CommandAdmission.StaleSequence, state.TryEnqueue(previousHost.Owner, SimCommand.Move(7, Vector2.UnitY)));
            Assert.Equal(CommandAdmission.Accepted, state.TryEnqueue(previousHost.Owner, SimCommand.Move(8, Vector2.UnitY)));
        }
        finally
        {
            await original.StopAsync(CancellationToken.None);
            try { await Task.WhenAll(firstTransport, secondTransport); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>실제 OnlinePeer 전송 경로의 패킷만 기록하고 입력은 취소될 때까지 기다린다.</summary>
    private sealed class BaselineSocket : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;
        public Channel<byte[]> Frames { get; } = Channel.CreateUnbounded<byte[]>();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Assert.Equal(WebSocketMessageType.Binary, messageType);
            Assert.True(endOfMessage);
            Assert.True(Frames.Writer.TryWrite(buffer.ToArray()));
            return Task.CompletedTask;
        }
    }
}
