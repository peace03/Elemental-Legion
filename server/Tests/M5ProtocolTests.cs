using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class M5ProtocolTests
{
    [Fact]
    public void RoomRestoreUsesValidatedIdAndItsSavedPort()
    {
        string id = Guid.NewGuid().ToString("N");
        Assert.Equal(id, ServerOptions.Parse(["--listen", "--resume-lan-room", id], 15).RestoreLanRoom);
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--listen", "--resume-lan-room", "../world"], 15));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--listen", "--resume-lan-room", id, "--port", "7777"], 15));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--online", "--revoke-account", id], 15));
    }

    [Fact]
    public void CredentialsRequireExactLengthsAndRemainOutsideLobbyDiscovery()
    {
        var rules = GameRulesTests.Rules();
        var writer = new NetDataWriter();
        var key = Enumerable.Repeat((byte)7, Protocol.AccountKeyBytes).ToArray();
        Protocol.WriteHello(writer, rules.DataHash, rules.StartingElements[0], resumeKey: key, requestAccount: true);
        Assert.Equal(JoinFailure.None, Protocol.ReadHello(new NetDataReader(writer.CopyData()), rules,
            out _, out _, out var recovered, out var host, out bool request));
        Assert.Equal(key, recovered);
        Assert.Null(host);
        Assert.True(request);
        Assert.Equal(JoinFailure.Malformed, Protocol.ReadHello(new NetDataReader(writer.CopyData()[..^1]), rules, out _));
        M5Protocol.WriteRoomListing(writer, 7, new LanRoomListing(7777, "테스트 방", 1, 2, 15, false));
        Assert.True(M5Protocol.TryReadRoomListing(new NetDataReader(writer.CopyData()), out uint nonce, out var listing));
        Assert.Equal(7u, nonce);
        Assert.Equal("테스트 방", listing.Title);
        Assert.False(M5Protocol.TryReadRoomListing(new NetDataReader(writer.CopyData()[..^1]), out _, out _));
    }

    [Fact]
    public void ProximityIncludesBoundaryAndChatRejectsControlCharacters()
    {
        var settings = new NetworkSettings(GameRulesTests.Data());
        Assert.True(ChatRouter.WithinRadius(Vector2.Zero, new Vector2(settings.ChatRadius, 0), settings.ChatRadius));
        Assert.False(ChatRouter.WithinRadius(Vector2.Zero, new Vector2(settings.ChatRadius + 0.01f, 0), settings.ChatRadius));
        Assert.False(M5Protocol.IsSafeText("안녕\n친구", settings.ChatMaxLength));
        var writer = new NetDataWriter();
        M5Protocol.WriteChat(writer, false, " 안녕하세요 ");
        Assert.True(M5Protocol.TryReadChat(new NetDataReader(writer.CopyData()), settings, out var request));
        Assert.Equal("안녕하세요", request.Text);
        M5Protocol.WriteChat(writer, true, new string('x', settings.ChatMaxLength + 1));
        Assert.False(M5Protocol.TryReadChat(new NetDataReader(writer.CopyData()), settings, out _));
    }

    [Fact]
    public void OutOfSightProximityHasNoAnchorAndOnlyRecipientsCanReport()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        var setup = new WorldSetup();
        string element = rules.Elements[rules.StartingElements[0]].Id;
        var origin = new Vector2(200, 200);
        for (int id = 1; id <= 3; id++)
        {
            Vector2 position = origin + new Vector2(id == 1 ? 0 : settings.ChatRadius, id == 3 ? 1 : 0);
            setup.Players.Add(new PlayerSpawn(id, element, new Vector2(200 * id, 600), position, nickname: "검증" + id));
            setup.Units.Add(new UnitSpawn(id, element, position));
        }
        var checkpoint = new SimulationWorld(rules, setup, 1).CaptureCheckpoint();
        var world = new NetworkWorld(rules, settings, checkpoint, 4);
        for (int id = 1; id <= 3; id++) Assert.True(world.TryResume(id));
        world.Step();
        var delivered = new Dictionary<int, ChatMessage>();
        var chat = new ChatRouter(settings);
        Assert.True(Send(false, "경계"));
        Assert.True(delivered[1].AnchorId > 0);
        Assert.Equal(0, delivered[2].AnchorId);
        Assert.Equal("검증1", delivered[2].Nickname);
        Assert.DoesNotContain(3, delivered.Keys);
        Assert.False(Send(false, "연속"));
        ulong idToReport = delivered[2].Id;
        Assert.Null(chat.Report(3, idToReport));
        Assert.NotNull(chat.Report(2, idToReport));
        Assert.Null(chat.Report(2, idToReport));
        delivered.Clear();
        Assert.True(Send(true, "전체"));
        Assert.Equal(3, delivered.Count);
        Assert.All(delivered.Values, message => Assert.Equal(0, message.AnchorId));

        bool Send(bool global, string text)
        {
            var writer = new NetDataWriter();
            M5Protocol.WriteChat(writer, global, text);
            Assert.True(M5Protocol.TryReadChat(new NetDataReader(writer.CopyData()), settings, out var request));
            return chat.Send(world, 1, request, delivered.Add);
        }
    }
}
