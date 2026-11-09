using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class AiMatchTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LanLobbySharesSeatsWithAiAndCanStartWithOneHuman(bool humanWins)
    {
        var json = GameRulesTests.Data().Root;
        json["ghost"]!["decaySeconds"] = 0.1;
        json["ghost"]!["reconnectWindowSeconds"] = 0.1;
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            json["server"]!["discoveryPort"] = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        byte[] proof = RandomNumberGenerator.GetBytes(Protocol.AccountKeyBytes);
        const int opponents = 2;
        var room = new LanRoomOptions("AI 검증", 3, 4, rules.MaxPlayers, proof);
        using var server = new UdpGameServer(rules, settings, room: room);
        using var human = new ClientConnection(rules, settings);
        using var friend = new ClientConnection(rules, settings);
        server.Start(0);

        int element = rules.StartingElements[0];
        human.Connect("127.0.0.1", server.Port, element, "플레이어", hostProof: proof, requestAccount: true);
        Pump(() => human.Lobby?.SelfIndex == 0);
        human.SetLobbyAiCount(opponents);
        Pump(() => human.Lobby?.AiCount == opponents);
        Thread.Sleep(60);
        human.SetLobbyAiDifficulty(0, AiDifficulty.Easy);
        Pump(() => human.Lobby?.AiDifficulties[0] == AiDifficulty.Easy);
        Thread.Sleep(60);
        human.SetLobbyAiDifficulty(1, AiDifficulty.Hard);
        Pump(() => human.Lobby?.AiDifficulties[1] == AiDifficulty.Hard);
        friend.Connect("127.0.0.1", server.Port, element, "친구", requestAccount: true);
        Pump(() => friend.Lobby is { SelfIndex: 1, AiCount: opponents });
        Assert.Equal(human.Lobby!.Value.AiDifficulties, friend.Lobby!.Value.AiDifficulties);
        int displacedColor = human.Lobby.Value.AiColorIds[0];
        int oldColor = friend.Lobby.Value.Members[1].ColorId;
        friend.SetLobbyColor(displacedColor);
        Pump(() => friend.Lobby?.Members[1].ColorId == displacedColor);
        Assert.NotEqual(displacedColor, friend.Lobby!.Value.AiColorIds[0]);
        Assert.NotEqual(oldColor, displacedColor);
        Thread.Sleep(60);
        friend.SetLobbyAiDifficulty(0, AiDifficulty.Hard);
        Pump(() => friend.LastLobbyError == LobbyError.NotHost);
        Assert.Equal(AiDifficulty.Easy, human.Lobby.Value.AiDifficulties[0]);
        Thread.Sleep(60);
        friend.SetLobbyAiCount(0);
        Pump(() => friend.LastLobbyError == LobbyError.NotHost);
        Thread.Sleep(60);
        human.SetLobbyAiCount(opponents + 1);
        Pump(() => human.LastLobbyError == LobbyError.RoomFull);
        Assert.Equal(opponents, human.Lobby?.AiCount);
        friend.Disconnect();
        Pump(() => human.Lobby?.Members.Length == 1);
        Thread.Sleep(60);
        human.SetLobbyAiCount(opponents - 1);
        Pump(() => human.Lobby?.AiCount == opponents - 1);
        Thread.Sleep(60);
        human.SetLobbyAiCount(opponents);
        Pump(() => human.Lobby?.AiCount == opponents);
        Assert.Equal(new[] { AiDifficulty.Easy, AiDifficulty.Normal }, human.Lobby!.Value.AiDifficulties);
        Thread.Sleep(60);
        human.SetLobbyReady(element, true);
        Pump(() => human.Lobby?.Members[0].Ready == true);
        AiDifficulty[] selectedDifficulties = human.Lobby!.Value.AiDifficulties;
        int[] selectedColors = human.Lobby.Value.Members.Select(member => member.ColorId).Concat(human.Lobby.Value.AiColorIds).ToArray();
        Thread.Sleep(60);
        human.StartRoom();
        Pump(() => human.State == ConnectionState.Connected && human.Fog.Ready);
        Assert.Equal(opponents + 1, human.WorldRules.RoomPlayers);
        Assert.Equal(1, server.Simulation.ConnectedCount);
        var view = server.Simulation.World.GetView(human.PlayerId);
        int[] ai = view.Standings.Where(standing => standing.Nickname.StartsWith("AI 군단 ")).Select(standing => standing.PlayerId).ToArray();
        Assert.Equal(opponents, ai.Length);
        Assert.Equal(selectedColors, view.Standings.OrderBy(player => player.PlayerId).Select(player => player.ColorId));
        Assert.Equal(selectedDifficulties, server.Simulation.CaptureAiMigration().Select(bot => bot.Difficulty));

        if (humanWins)
        {
            Assert.True(server.Simulation.World.BeginGhost(ai[0]));
            Pump(() => !server.Simulation.World.TryGetView(ai[0], out _));
            Assert.Equal(ConnectionState.Connected, human.State);
            foreach (int owner in ai.Skip(1)) Assert.True(server.Simulation.World.BeginGhost(owner));
        }
        else Assert.True(server.Simulation.World.BeginGhost(human.PlayerId));
        Pump(() => human.State == ConnectionState.Disconnected);
        Assert.Equal(humanWins, human.Victorious);
        Assert.Equal(!humanWins, human.Defeated);
        Assert.True(server.RoomClosed);

        void Pump(Func<bool> done)
        {
            var timeout = Stopwatch.StartNew();
            while (!done() && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                server.Poll();
                server.Step();
                human.Poll();
                friend.Poll();
                Thread.Sleep(1);
            }
            Assert.True(done());
        }
    }

    [Fact]
    public void AiBuildsTrainsAndRalliesFromTheNormalStart()
    {
        var json = GameRulesTests.Data().Root;
        // 새 시작 병력의 조기 승리가 건설·양성 흐름의 검증을 끝내지 않도록 본진을 유지한다.
        json["buildings"]!["base"]!["hp"] = 100000;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data).ForRoom(2);
        var match = new NetworkWorld(rules, new NetworkSettings(data));
        int element = rules.StartingElements[0];
        Assert.Equal(JoinFailure.None, match.TryJoin(element, out int human));
        Assert.Equal(JoinFailure.None, match.AddAiOpponents(1, element));
        int ai = Assert.Single(match.World.GetView(human).Standings, standing => standing.Nickname == "AI 군단 1").PlayerId;
        var view = match.World.GetView(ai);
        Assert.Equal(0, view.CoreBalance);
        for (int tick = 0; tick < 240 * rules.TickRate && (view.TrainedCount == 0 || view.GarrisonArrivals <= rules.StartGarrisonUnitCount); tick++) match.Step();
        Assert.True(view.NatureForge, $"양성소 미완성. 핵 {view.CoreBalance}, 시설 {view.OwnBuildingCount}, 위치 {view.LegionPosition}, 인구 {view.Population}, 전향 {view.ConvertedCount}, 틱 {view.Tick}.");
        Assert.True(view.TrainedCount > 0, $"양성 미완료. 핵 {view.CoreBalance}, 인구 {view.Population}/{view.PopulationCap}, 예약 {view.ReservedPopulation}, 양성 {view.IsTraining}, 합류 {view.GarrisonArrivals}, 시설 {view.OwnBuildingCount}, 틱 {view.Tick}.");
        Assert.True(view.GarrisonArrivals > 0);

        var directory = Directory.CreateTempSubdirectory("elemental-ai-save-");
        try
        {
            var store = new FileWorldStore(directory.FullName, rules);
            string id = Guid.NewGuid().ToString("N");
            store.Save(new WorldSave(id, match.World.CaptureCheckpoint(), new() { [Guid.NewGuid().ToString("N")] = human },
                match.NextPlayerId, "AI 검증", 2, 2, AiOwners: [ai]));
            var saved = Assert.IsType<WorldSave>(store.Load(id));
            var restored = new NetworkWorld(rules, new NetworkSettings(data), saved.World, saved.NextPlayerId, aiOwners: saved.AiOwners);
            Assert.Equal(new[] { ai }, saved.AiOwners);
            Assert.Equal(1, restored.LivingAiCount);
            Assert.True(restored.TryResume(human));
            Assert.Contains(restored.World.GetView(human).Standings, standing => standing.PlayerId == ai);
        }
        finally { directory.Delete(recursive: true); }
    }
}
