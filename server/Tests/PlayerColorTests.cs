using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>소유 색의 서버 배정, 패킷 검증과 저장 호환성을 확인한다.</summary>
public sealed class PlayerColorTests
{
    [Fact]
    public async Task ExpiredLobbyRecoveryReservationDoesNotRestoreAColorClaimedAfterItsDeadline()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var first = new AccountIdentity(Guid.NewGuid().ToString("N"), "늦은 참가자");
        var host = new AccountIdentity(Guid.NewGuid().ToString("N"), "복구한 방장");
        var snapshot = new HostMigrationSnapshot
        {
            RoomId = Guid.NewGuid().ToString("N"), Title = "복구 예약 만료", DataHash = rules.DataHash,
            Generation = 1, BackupNumber = 1, MinimumPlayers = 2, Capacity = 3,
            Members = [new(first.Id, first.Nickname, 1, true, 0, 0), new(host.Id, host.Nickname, 0, false, 0, 1)]
        };
        var clock = new RecoveryClock();
        using var rooms = new OnlineRooms(rules, settings, null, null, null, clock, NullLogger<OnlineRooms>.Instance, null);
        rooms.RestoreHostedMigration(snapshot, host, [first.Id, host.Id]);
        Assert.Null(rooms.AttachHosted(snapshot.RoomId, host, null, out var hostPeer));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await rooms.StartAsync(timeout.Token);
        try
        {
            clock.Now += TimeSpan.FromSeconds(settings.MigrationCandidateSeconds + 1);
            await rooms.CaptureHostedMigration(snapshot.RoomId, 2).WaitAsync(timeout.Token);
            var writer = new NetDataWriter();
            M5Protocol.WriteLobbyColor(writer, 0);
            hostPeer!.Input.Writer.TryWrite(writer.CopyData());
            await rooms.CaptureHostedMigration(snapshot.RoomId, 2).WaitAsync(timeout.Token);
            Assert.Equal(0, hostPeer.ColorId);
            Assert.Null(rooms.AttachHosted(snapshot.RoomId, first, 0, out var rejoined));
            Assert.Equal(1, rejoined!.ColorId);
            Assert.Equal(0, rejoined.Element);
            Assert.False(rejoined.Ready);
        }
        finally { await rooms.StopAsync(CancellationToken.None); }
    }

    private sealed class RecoveryClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task RejoiningMigratedLobbyDoesNotRestoreConsumedColorElementOrReadyState()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var first = new AccountIdentity(Guid.NewGuid().ToString("N"), "첫 참가자");
        var host = new AccountIdentity(Guid.NewGuid().ToString("N"), "새 방장");
        var snapshot = new HostMigrationSnapshot
        {
            RoomId = Guid.NewGuid().ToString("N"), Title = "복구 후 재입장", DataHash = rules.DataHash,
            Generation = 1, BackupNumber = 1, MinimumPlayers = 2, Capacity = 3,
            Members = [new(first.Id, first.Nickname, 1, true, 0, 0), new(host.Id, host.Nickname, 0, false, 0, 1)]
        };
        using var rooms = new OnlineRooms(rules, settings);
        rooms.RestoreHostedMigration(snapshot, host, [first.Id, host.Id]);
        Assert.Null(rooms.AttachHosted(snapshot.RoomId, first, null, out var firstPeer));
        Assert.Equal(1, firstPeer!.Element);
        Assert.True(firstPeer.Ready);
        Assert.Equal(0, firstPeer.ColorId);
        Assert.Null(rooms.AttachHosted(snapshot.RoomId, host, null, out var hostPeer));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await rooms.StartAsync(timeout.Token);
        try
        {
            var writer = new NetDataWriter();
            M5Protocol.WriteLobbyReady(writer, 1, false);
            firstPeer.Input.Writer.TryWrite(writer.CopyData());
            await Capture();
            M5Protocol.WriteLobbyColor(writer, 2);
            firstPeer.Input.Writer.TryWrite(writer.CopyData());
            await Capture();
            M5Protocol.WriteLobbyColor(writer, 0);
            hostPeer!.Input.Writer.TryWrite(writer.CopyData());
            await Capture();
            firstPeer.Abort();
            Assert.Single((await Capture()).Members);
            Assert.Null(rooms.AttachHosted(snapshot.RoomId, first, 0, out var rejoined));
            Assert.False(rejoined!.Ready);
            Assert.Equal(0, rejoined.Element);
            Assert.Equal(1, rejoined.ColorId);
            var final = await Capture();
            Assert.Equal(2, final.Members.Select(member => member.ColorId).Distinct().Count());

            async Task<HostMigrationSnapshot> Capture() => await rooms.CaptureHostedMigration(snapshot.RoomId, 2).WaitAsync(timeout.Token);
        }
        finally { await rooms.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void SavedOnlineRoomCanRestoreColorsAfterAnAiHasBeenEliminated()
    {
        var data = GameRulesTests.Data();
        data.Root["ghost"]!["decaySeconds"] = 0.05;
        data.Root["ghost"]!["reconnectWindowSeconds"] = 0.05;
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        var world = new NetworkWorld(rules.ForRoom(3), settings);
        Assert.Equal(JoinFailure.None, world.TryJoin(0, out int human, colorId: 9));
        Assert.Equal(JoinFailure.None, world.AddAiOpponents(2, 0));
        int[] ai = world.CaptureAiOwners()!;
        Assert.True(world.World.BeginGhost(ai[0]));
        for (int tick = 0; tick < 3; tick++) world.Step();
        Assert.False(world.World.TryGetView(ai[0], out _));
        var directory = Directory.CreateTempSubdirectory("elemental-eliminated-color-");
        try
        {
            var store = new FileWorldStore(directory.FullName, rules);
            string id = Guid.NewGuid().ToString("N");
            store.Save(new WorldSave(id, world.World.CaptureCheckpoint(), new() { [Guid.NewGuid().ToString("N")] = human },
                world.NextPlayerId, "AI 색 복원", 2, 3, AiOwners: ai));
            using var restored = new OnlineRooms(rules, settings, null, store, null, TimeProvider.System, NullLogger<OnlineRooms>.Instance, null);
            Assert.NotNull(restored.Status(id));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void PersistentLateJoinsUseAvailableColorsEvenWhenOwnerIdsExceedPaletteSize()
    {
        var data = GameRulesTests.Data();
        data.Root["ghost"]!["decaySeconds"] = 0.05;
        data.Root["ghost"]!["reconnectWindowSeconds"] = 0.05;
        var rules = new GameRules(data);
        var world = new NetworkWorld(rules, new NetworkSettings(data));
        for (int index = 0; index < PlayerPalette.Count + 2; index++)
        {
            Assert.Equal(JoinFailure.None, world.TryJoin(0, out int owner));
            Assert.Equal(index + 1, owner);
            Assert.Equal(0, world.World.GetView(owner).Standings.Single(player => player.PlayerId == owner).ColorId);
            world.Leave(owner);
            for (int tick = 0; tick < 3 && world.World.PlayerCount > 0; tick++) world.Step();
            Assert.Equal(0, world.World.PlayerCount);
        }
    }

    [Fact]
    public async Task OnlineLobbyRejectsHumanDuplicatesMovesAiAndLocksReadyRunningAndFrozenPlayers()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var host = new AccountIdentity(Guid.NewGuid().ToString("N"), "색 방장");
        var guest = new AccountIdentity(Guid.NewGuid().ToString("N"), "색 참가자");
        using var rooms = new OnlineRooms(rules, settings);
        Assert.Null(rooms.Create(host, "소유 색 검증", 2, 4, out var room));
        Assert.Null(rooms.AttachHosted(room!.Id, host, 0, out var first));
        Assert.Null(rooms.AttachHosted(room.Id, guest, 0, out var second));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rooms.StartAsync(timeout.Token);
        try
        {
            var writer = new NetDataWriter();
            M5Protocol.WriteLobbyAiCount(writer, 1);
            first!.Input.Writer.TryWrite(writer.CopyData());
            var snapshot = await Capture();
            int aiColor = Assert.Single(snapshot.AiColorIds!);
            int originalGuest = second!.ColorId;
            M5Protocol.WriteLobbyColor(writer, first.ColorId);
            second.Input.Writer.TryWrite(writer.CopyData());
            snapshot = await Capture();
            Assert.Equal(originalGuest, snapshot.Members.Single(member => member.AccountId == guest.Id).ColorId);
            M5Protocol.WriteLobbyColor(writer, aiColor);
            second.Input.Writer.TryWrite(writer.CopyData());
            snapshot = await Capture();
            Assert.Equal(aiColor, second.ColorId);
            Assert.Equal(originalGuest, Assert.Single(snapshot.AiColorIds!));
            Assert.Equal(3, snapshot.Members.Select(member => member.ColorId).Concat(snapshot.AiColorIds!).Distinct().Count());

            M5Protocol.WriteLobbyReady(writer, 0, true);
            second.Input.Writer.TryWrite(writer.CopyData());
            await Capture();
            M5Protocol.WriteLobbyColor(writer, 14);
            second.Input.Writer.TryWrite(writer.CopyData());
            await Capture();
            Assert.Equal(aiColor, second.ColorId);
            second.Input.Writer.TryWrite(new byte[] { (byte)PacketKind.LobbyColor, 255 });
            await Capture();
            Assert.Equal(aiColor, second.ColorId);

            using var recovered = new OnlineRooms(rules, settings);
            recovered.RestoreHostedMigration(snapshot, guest, [host.Id, guest.Id]);
            Assert.Null(recovered.AttachHosted(room.Id, guest, null, out var restoredGuest));
            await recovered.StartAsync(timeout.Token);
            try
            {
                M5Protocol.WriteLobbyColor(writer, 14);
                restoredGuest!.Input.Writer.TryWrite(writer.CopyData());
                var recovering = await recovered.CaptureHostedMigration(room.Id, 2).WaitAsync(timeout.Token);
                Assert.Equal(aiColor, recovering.Members.Single(member => member.AccountId == guest.Id).ColorId);
                Assert.Null(recovered.AttachHosted(room.Id, host, null, out var restoredHost));
                Assert.Equal(aiColor, restoredGuest.ColorId);
                Assert.Equal(first.ColorId, restoredHost!.ColorId);
            }
            finally { await recovered.StopAsync(CancellationToken.None); }

            M5Protocol.WriteLobbyReady(writer, 0, true);
            first.Input.Writer.TryWrite(writer.CopyData());
            await Capture();
            M5Protocol.WriteLobbyStart(writer);
            first.Input.Writer.TryWrite(writer.CopyData());
            snapshot = await Capture();
            Assert.NotNull(snapshot.World);
            var world = SimulationWorld.RestoreMigrationState(rules, snapshot.World!);
            int guestOwner = snapshot.Players[guest.Id];
            Assert.Equal(aiColor, world.GetView(guestOwner).Standings.Single(player => player.PlayerId == guestOwner).ColorId);
            M5Protocol.WriteLobbyColor(writer, 14);
            second.Input.Writer.TryWrite(writer.CopyData());
            await Capture();
            Assert.Equal(aiColor, second.ColorId);
            snapshot = await rooms.CaptureHostedMigration(room.Id, 1, freeze: true).WaitAsync(timeout.Token);
            first.Input.Writer.TryWrite(writer.CopyData());
            snapshot = await Capture();
            Assert.Equal(first.ColorId, snapshot.Members.Single(member => member.AccountId == host.Id).ColorId);

            async Task<HostMigrationSnapshot> Capture() => await rooms.CaptureHostedMigration(room.Id, 1).WaitAsync(timeout.Token);
        }
        finally { await rooms.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void OwnerColorMetadataIncludesVisibleGhostsAndHidesUnobservedGhosts()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(101, "water", new Vector2(200), new Vector2(210), colorId: 8));
        setup.Players.Add(new PlayerSpawn(202, "fire", new Vector2(200, 205), new Vector2(210, 215), colorId: 2));
        setup.Players.Add(new PlayerSpawn(303, "earth", new Vector2(1400), new Vector2(1410), colorId: 13));
        var world = new SimulationWorld(rules, setup, 1);
        world.BeginGhost(202);
        world.BeginGhost(303);
        WorldView view = world.GetView(101);
        Assert.DoesNotContain(view.Standings, owner => owner.PlayerId == 202 || owner.PlayerId == 303);
        Assert.Contains(view.OwnerColors, owner => owner.PlayerId == 202 && owner.ColorId == 2);
        Assert.DoesNotContain(view.OwnerColors, owner => owner.PlayerId == 303);
        var writer = new NetDataWriter();
        GameplayCodec.WriteBody(writer, view, Array.Empty<NetworkEvent>(), 0);
        var frame = new GameplayFrame(rules);
        Assert.True(GameplayCodec.ReadBody(new NetDataReader(writer.CopyData()), rules, frame, view.Tick, 101, 1));
        Assert.Equal(view.OwnerColors.Select(color => (color.PlayerId, color.ColorId)), frame.OwnerColors.Select(color => (color.PlayerId, color.ColorId)));
    }

    [Fact]
    public void ExplicitColorsAreUniqueAndSurviveResumeCheckpointAndMigration()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data).ForRoom(3);
        var settings = new NetworkSettings(data);
        var world = new NetworkWorld(rules, settings);
        Assert.Equal(JoinFailure.None, world.TryJoin(rules.StartingElements[0], out int owner, colorId: 9));
        Assert.Equal(JoinFailure.WorldFull, world.TryJoin(rules.StartingElements[0], out _, colorId: 9));
        Assert.Equal(JoinFailure.None, world.TryJoin(rules.StartingElements[0], out int other));
        Assert.Equal(9, world.World.GetView(owner).Standings.Single(p => p.PlayerId == owner).ColorId);
        world.Leave(owner);
        var restored = new NetworkWorld(rules, settings, world.World.CaptureCheckpoint(), world.NextPlayerId);
        Assert.True(restored.TryResume(owner));
        Assert.Equal(9, restored.World.GetView(owner).Standings.Single(p => p.PlayerId == owner).ColorId);
        var migrated = SimulationWorld.RestoreMigrationState(rules, restored.World.CaptureMigrationState());
        Assert.Equal(9, migrated.GetView(owner).Standings.Single(p => p.PlayerId == owner).ColorId);
    }

    [Fact]
    public void LegacyCheckpointWithoutColorUsesDistinctSlotsAndRejectsDuplicateOrInvalidColors()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(101, "water", new Vector2(100), new Vector2(110), colorId: 8));
        setup.Players.Add(new PlayerSpawn(202, "fire", new Vector2(300), new Vector2(310), colorId: 2));
        var world = new SimulationWorld(rules, setup, 1);
        var json = JObject.Parse(JsonConvert.SerializeObject(world.CaptureCheckpoint()));
        foreach (JObject player in json["Players"]!) player.Remove("ColorId");
        var legacy = JsonConvert.DeserializeObject<WorldCheckpoint>(json.ToString())!;
        var restored = SimulationWorld.RestoreCheckpoint(rules, legacy);
        Assert.True(restored.TryResumeGhost(101));
        Assert.True(restored.TryResumeGhost(202));
        Assert.Equal(new[] { 0, 1 }, restored.GetView(101).Standings.OrderBy(p => p.PlayerId).Select(p => p.ColorId));
        json["Players"]![0]!["ColorId"] = 4;
        json["Players"]![1]!["ColorId"] = 4;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules,
            JsonConvert.DeserializeObject<WorldCheckpoint>(json.ToString())!));
        json["Players"]![1]!["ColorId"] = PlayerPalette.Count;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules,
            JsonConvert.DeserializeObject<WorldCheckpoint>(json.ToString())!));
    }

    [Fact]
    public void LobbyColorPacketRejectsInvalidTruncatedAndTrailingBytes()
    {
        var writer = new NetDataWriter();
        M5Protocol.WriteLobbyColor(writer, 14);
        Assert.True(M5Protocol.TryReadLobbyColor(new NetDataReader(writer.CopyData()), out int color));
        Assert.Equal(14, color);
        Assert.Throws<ArgumentOutOfRangeException>(() => M5Protocol.WriteLobbyColor(writer, 15));
        Assert.False(M5Protocol.TryReadLobbyColor(new NetDataReader(new byte[] { (byte)PacketKind.LobbyColor }), out _));
        Assert.False(M5Protocol.TryReadLobbyColor(new NetDataReader(new byte[] { (byte)PacketKind.LobbyColor, 255 }), out _));
        Assert.False(M5Protocol.TryReadLobbyColor(new NetDataReader(new byte[] { (byte)PacketKind.LobbyColor, 0, 0 }), out _));
    }

    [Fact]
    public void LobbyStateRejectsDuplicateAndInvalidColorsAcrossHumansAndAi()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        LobbyMember[] members = [new("사람", 0, false, 9), new("참가자", 0, false)];
        M5Protocol.WriteLobbyState(writer, "색 검증", 0, 2, 3, members, 1, aiColorIds: [14]);
        byte[] valid = writer.CopyData();
        Assert.True(M5Protocol.TryReadLobbyState(new NetDataReader(valid), rules, out var state));
        Assert.Equal(new[] { 9, 0 }, state.Members.Select(member => member.ColorId));
        Assert.Equal(14, Assert.Single(state.AiColorIds));
        byte[] duplicate = valid.ToArray();
        duplicate[7] = 9;
        Assert.False(M5Protocol.TryReadLobbyState(new NetDataReader(duplicate), rules, out _));
        duplicate[7] = 15;
        Assert.False(M5Protocol.TryReadLobbyState(new NetDataReader(duplicate), rules, out _));
        Assert.Throws<ArgumentException>(() => M5Protocol.WriteLobbyState(writer, "중복", 0, 2, 3,
            [new("하나", 0, false, 4), new("둘", 0, false, 4)]));
    }

    [Fact]
    public void DiskColorsRoundTripAndInvalidMetadataCannotReplaceThePreviousSave()
    {
        var rules = GameRulesTests.Rules();
        var world = new NetworkWorld(rules, new NetworkSettings(GameRulesTests.Data()));
        Assert.Equal(JoinFailure.None, world.TryJoin(0, out int owner, colorId: 11));
        var directory = Directory.CreateTempSubdirectory("elemental-colors-");
        try
        {
            var store = new FileWorldStore(directory.FullName, rules);
            var save = new WorldSave("color", world.World.CaptureCheckpoint(), new() { [Guid.NewGuid().ToString("N")] = owner }, world.NextPlayerId);
            store.Save(save);
            var loaded = Assert.IsType<WorldSave>(store.Load("color"));
            var restored = new NetworkWorld(rules, new NetworkSettings(GameRulesTests.Data()), loaded.World, loaded.NextPlayerId);
            Assert.True(restored.TryResume(owner));
            Assert.Equal(11, Assert.Single(restored.World.GetView(owner).Standings).ColorId);
            string path = Path.Combine(directory.FullName, "world-color.json");
            string original = File.ReadAllText(path);
            var json = JObject.Parse(JsonConvert.SerializeObject(save.World));
            json["Players"]![0]!["ColorId"] = 15;
            Assert.Throws<InvalidDataException>(() => store.Save(save with
            {
                World = JsonConvert.DeserializeObject<WorldCheckpoint>(json.ToString())!
            }));
            Assert.Equal(original, File.ReadAllText(path));
            var legacy = JObject.Parse(original);
            foreach (JObject player in legacy["Save"]!["World"]!["Players"]!) player.Remove("ColorId");
            File.WriteAllText(path, legacy.ToString());
            loaded = Assert.IsType<WorldSave>(store.Load("color"));
            restored = new NetworkWorld(rules, new NetworkSettings(GameRulesTests.Data()), loaded.World, loaded.NextPlayerId);
            Assert.True(restored.TryResume(owner));
            Assert.Equal(0, Assert.Single(restored.World.GetView(owner).Standings).ColorId);
        }
        finally { directory.Delete(recursive: true); }
    }
}
