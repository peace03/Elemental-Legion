using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>인터넷 세대 프레임과 승계 제어 입력의 경계를 확인한다.</summary>
public sealed class HostMigrationProtocolTests
{
    [Fact]
    public void FramesRoundTripGenerationAndKeepTheGamePayloadLimit()
    {
        var payload = new byte[Protocol.MaxDatagramBytes];
        payload[0] = (byte)PacketKind.Pose;
        byte[] frame = OnlineFrame.Wrap(8, payload);
        Assert.Equal(Protocol.MaxDatagramBytes + 8, frame.Length);
        Assert.True(OnlineFrame.TryRead(frame, frame.Length, out ulong generation, out byte[] body));
        Assert.Equal(8UL, generation);
        Assert.Equal(payload, body);
        Assert.False(OnlineFrame.TryRead(frame, 8, out _, out _));
        Assert.False(OnlineFrame.TryRead(new byte[OnlineFrame.MaxMessageBytes + 1], OnlineFrame.MaxMessageBytes + 1, out _, out _));
        Assert.Throws<ArgumentException>(() => OnlineFrame.Wrap(1, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => OnlineFrame.Wrap(1, new byte[Protocol.MaxDatagramBytes + 1]));
    }

    [Fact]
    public void StandbyAndMigrationControlsRoundTripAndRejectTrailingBytes()
    {
        var writer = new NetDataWriter();
        string room = Guid.NewGuid().ToString("N"), key = new('a', 64);
        HostMigrationProtocol.WriteStandby(writer, room, key, 2);
        Assert.True(HostMigrationProtocol.TryReadStandby(new NetDataReader(writer.CopyData()), out HostStandbyOffer offer));
        Assert.Equal(room, offer.RoomId);
        Assert.Equal(key, offer.Key);
        Assert.Equal(2UL, offer.Generation);
        writer.Put((byte)0);
        Assert.False(HostMigrationProtocol.TryReadStandby(new NetDataReader(writer.CopyData()), out _));

        HostMigrationProtocol.WriteMigration(writer, MigrationPhase.Resyncing, 2, 75, true, "restored", backupAgeMs: 125);
        Assert.True(HostMigrationProtocol.TryReadMigration(new NetDataReader(writer.CopyData()), out HostMigrationNotice state));
        Assert.Equal(MigrationPhase.Resyncing, state.Phase);
        Assert.Equal(2UL, state.Generation);
        Assert.Equal(75, state.Tick);
        Assert.True(state.IsHost);
        Assert.Equal("restored", state.Reason);
        Assert.Equal(125, state.BackupAgeMs);
        writer.Put((byte)0);
        Assert.False(HostMigrationProtocol.TryReadMigration(new NetDataReader(writer.CopyData()), out _));
        Assert.Throws<ArgumentException>(() => HostMigrationProtocol.WriteStandby(writer, "../room", key, 2));
    }

    [Fact]
    public void BackupAgeAcceptsKnownMillisecondsOrUnknownSentinelAndRejectsOtherNegatives()
    {
        foreach (int age in new[] { 125, -1, -2 })
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketKind.HostMigration); writer.Put((byte)MigrationPhase.Resyncing);
            writer.Put(2UL); writer.Put(75L); writer.Put((byte)1); writer.Put(age); writer.Put("restored");
            Assert.Equal(age >= -1, HostMigrationProtocol.TryReadMigration(new NetDataReader(writer.CopyData()), out HostMigrationNotice notice));
            if (age >= -1) Assert.Equal(age, notice.BackupAgeMs);
        }
        Assert.Throws<ArgumentException>(() => HostMigrationProtocol.WriteMigration(new NetDataWriter(), MigrationPhase.None, 1, 0, false, backupAgeMs: -2));
    }

    [Fact]
    public void EveryTruncatedControlIsRejected()
    {
        var writer = new NetDataWriter();
        HostMigrationProtocol.WriteMigration(writer, MigrationPhase.Recovering, 2, 75, false, "recovering");
        byte[] bytes = writer.CopyData();
        for (int length = 0; length < bytes.Length; length++)
            Assert.False(HostMigrationProtocol.TryReadMigration(new NetDataReader(bytes, 0, length), out _));
        HostMigrationProtocol.WriteCapability(writer, true);
        Assert.True(HostMigrationProtocol.TryReadCapability(new NetDataReader(writer.CopyData()), out bool capable));
        Assert.True(capable);
        HostMigrationProtocol.WriteLeave(writer);
        Assert.True(HostMigrationProtocol.TryReadLeave(new NetDataReader(writer.CopyData())));
    }

    [Fact]
    public void StandbyPermissionsRejectTruncationInvalidKeysAndInvalidFlags()
    {
        var writer = new NetDataWriter();
        HostMigrationProtocol.WriteStandby(writer, new string('a', 32), new string('b', 64), 2);
        byte[] bytes = writer.CopyData();
        for (int length = 0; length < bytes.Length; length++)
            Assert.False(HostMigrationProtocol.TryReadStandby(new NetDataReader(bytes, 0, length), out _));
        writer.Reset(); writer.Put((byte)PacketKind.HostStandbyOffer);
        writer.Put(new string('a', 32)); writer.Put(new string('z', 64)); writer.Put(2UL);
        Assert.False(HostMigrationProtocol.TryReadStandby(new NetDataReader(writer.CopyData()), out _));
        writer.Reset(); writer.Put((byte)PacketKind.HostCapability); writer.Put((byte)2);
        Assert.False(HostMigrationProtocol.TryReadCapability(new NetDataReader(writer.CopyData()), out _));
        HostMigrationProtocol.WriteMigration(writer, MigrationPhase.None, 2, 1, false);
        bytes = writer.CopyData(); bytes[1] = byte.MaxValue;
        Assert.False(HostMigrationProtocol.TryReadMigration(new NetDataReader(bytes), out _));
    }

    [Fact]
    public async Task StandbyStartupRequiresAnInstalledExecutableAndHttpsOrigin()
    {
        var offer = new HostStandbyOffer(new string('a', 32), new string('b', 64), 2);
        Assert.Throws<ArgumentException>(() => HostedServerProcess.StandbyInput(new Uri("http://example.invalid"), offer));
        Assert.Throws<ArgumentException>(() => HostedServerProcess.StandbyInput(new Uri("https://user:secret@example.invalid"), offer));
        string input = HostedServerProcess.StandbyInput(new Uri("https://example.invalid"), offer);
        var json = Newtonsoft.Json.Linq.JObject.Parse(input);
        Assert.True((bool)json["standby"]!);
        Assert.Equal(2UL, (ulong)json["generation"]!);
        Assert.Equal(offer.RoomId, (string?)json["roomId"]);
        await Assert.ThrowsAsync<ArgumentException>(() => HostedServerProcess.StartAsync("untrusted-relative.exe", input, true, CancellationToken.None));
    }
}
