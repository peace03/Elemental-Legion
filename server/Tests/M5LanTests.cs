using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using Xunit;
using ConnectionState = ElementalLegion.Shared.Net.ConnectionState;

namespace ElementalLegion.Tests;

public sealed class M5LanTests
{
    [Fact]
    public void DiscoverStartWithNewHostAndRejoinOriginalBase()
    {
        var rules = GameRulesTests.Rules();
        var data = GameRulesTests.Data();
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            data.Root["server"]!["discoveryPort"] = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        var settings = new NetworkSettings(data);
        byte[] proof = RandomNumberGenerator.GetBytes(Protocol.AccountKeyBytes);
        string directory = Directory.CreateTempSubdirectory("elemental-lan-").FullName;
        var store = new FileWorldStore(Path.Combine(directory, "worlds"), rules);
        string accountsPath = Path.Combine(directory, "accounts");
        var roomOptions = new LanRoomOptions("LAN 검증", 2, 3, rules.MaxPlayers, proof);
        var accounts = new FileAccountStore(accountsPath);
        using var server = new UdpGameServer(rules, settings, accounts: accounts, room: roomOptions);
        UdpGameServer pollingServer = server;
        server.Start(0);
        using var host = new ClientConnection(rules, settings);
        using var guest = new ClientConnection(rules, settings);
        using var third = new ClientConnection(rules, settings);
        int element = rules.StartingElements[0];
        var clients = new[] { host, guest, third };
        var discovery = new EventBasedNetListener();
        var finder = new NetManager(discovery) { AutoRecycle = true, UnconnectedMessagesEnabled = true };
        LanRoomListing? found = null;
        discovery.NetworkReceiveUnconnectedEvent += (_, reader, _) =>
        {
            if (M5Protocol.TryReadRoomListing(reader, out uint nonce, out var listing) && nonce == 42) found = listing;
        };
        Assert.True(finder.Start());
        try
        {
            guest.Connect("127.0.0.1", server.Port, element, "먼저온손님", requestAccount: true);
            Pump(() => guest.State == ConnectionState.Disconnected);
            Assert.Equal(JoinFailure.Malformed, guest.JoinFailure);
            host.Connect("127.0.0.1", server.Port, element, "처음방장", hostProof: proof, requestAccount: true);
            Pump(() => host.Lobby?.SelfIndex == 0);
            var query = new NetDataWriter();
            M5Protocol.WriteDiscovery(query, 42);
            Assert.True(finder.SendUnconnectedMessage(query, new IPEndPoint(IPAddress.Loopback, settings.DiscoveryPort)));
            Pump(() => found.HasValue);
            Assert.Equal("LAN 검증", found!.Value.Title);
            Assert.Equal(server.Port, found.Value.Port);
            Assert.False(found.Value.Running);

            guest.Connect("127.0.0.1", server.Port, element, "새방장", requestAccount: true);
            Pump(() => guest.Lobby?.SelfIndex == 1);
            guest.StartRoom();
            Pump(() => guest.LastLobbyError == LobbyError.NotHost);
            third.Connect("127.0.0.1", server.Port, element, "참가자", requestAccount: true);
            Pump(() => third.Lobby?.SelfIndex == 2);
            host.Disconnect();
            Pump(() => guest.Lobby?.SelfIndex == 0 && third.Lobby?.SelfIndex == 1);
            PumpFor(60);
            guest.SetLobbyColor(12);
            Pump(() => guest.Lobby?.Members[0].ColorId == 12);
            third.SetLobbyColor(12);
            Pump(() => third.LastLobbyError == LobbyError.ColorTaken);
            Assert.NotEqual(12, third.Lobby!.Value.Members[1].ColorId);
            Assert.Equal(0, server.Simulation.ConnectedCount);
            Assert.Equal(0, server.Simulation.World.Metrics.NeutralUnits);
            PumpFor(60);
            guest.StartRoom();
            Pump(() => guest.LastLobbyError == LobbyError.NotReady);
            PumpFor(60);
            int fire = rules.ElementIndex("fire");
            guest.SetLobbyReady(fire, true);
            third.SetLobbyReady(element, true);
            Pump(() => guest.Lobby is { } lobby && lobby.Members.All(member => member.Ready));
            PumpFor(60);
            guest.StartRoom();
            Pump(() => guest.Fog.Ready && third.Fog.Ready && guest.State == ConnectionState.Connected &&
                third.State == ConnectionState.Connected && guest.Gameplay.State.Tick >= 0);
            Assert.Equal(fire, guest.Gameplay.State.MainElement);
            Assert.Equal(12, guest.Gameplay.State.Standings.Single(item => item.PlayerId == guest.PlayerId).ColorId);
            Assert.Equal(720, server.Simulation.Rules.MapSize);
            Assert.Equal(server.Simulation.Rules.MapSize, guest.WorldRules.MapSize);
            Assert.Equal((int)Math.Ceiling(server.Simulation.Rules.MapSize / rules.CellSize), third.Fog.Width);
            int owner = guest.PlayerId;
            byte[] key = Assert.IsType<byte[]>(guest.IssuedAccountKey).ToArray();
            var originalBase = server.Simulation.World.GetView(owner).BasePosition;
            ChatMessage? chat = null;
            third.ChatReceived += message => chat = message;
            guest.SendChat(true, "같은 방 채팅");
            Pump(() => chat.HasValue);
            Assert.Equal("새방장", chat!.Value.Nickname);
            Assert.Equal(0, chat.Value.AnchorId);
            guest.Disconnect();
            Pump(() => server.Simulation.ConnectedCount == 1);
            guest.Connect("127.0.0.1", server.Port, element, "다른표시이름", resumeKey: key);
            Pump(() => guest.Fog.Ready && guest.State == ConnectionState.Connected);
            Assert.True(guest.Resumed);
            Assert.Equal(owner, guest.PlayerId);
            Assert.Equal(originalBase, server.Simulation.World.GetView(owner).BasePosition);

            string id = Guid.NewGuid().ToString("N");
            var players = new Dictionary<string, int>
            {
                [Assert.IsType<AccountIdentity>(accounts.Authenticate(key)).Id] = owner,
                [Assert.IsType<AccountIdentity>(accounts.Authenticate(third.IssuedAccountKey)).Id] = third.PlayerId
            };
            store.Save(new WorldSave(id, server.Simulation.World.CaptureCheckpoint(), players,
                server.Simulation.NextPlayerId, roomOptions.Title, roomOptions.Minimum, roomOptions.Capacity, server.Port));
            WorldSave saved = Assert.IsType<WorldSave>(store.Load(id));
            Assert.Equal(2, saved.World.RoomPlayers);
            Assert.Equal(server.Port, saved.LanPort);
            guest.Disconnect();
            third.Disconnect();
            server.Dispose();
            using var restarted = new UdpGameServer(rules, settings, restored: new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId),
                accounts: new FileAccountStore(accountsPath), owners: saved.Players, room: roomOptions);
            restarted.Start(saved.LanPort);
            pollingServer = restarted;
            guest.Connect("127.0.0.1", restarted.Port, element, resumeKey: key);
            Pump(() => guest.Fog.Ready && guest.State == ConnectionState.Connected);
            Assert.True(guest.Resumed);
            Assert.Equal(owner, guest.PlayerId);
            Assert.Equal(720, guest.WorldRules.MapSize);
            Assert.Equal(originalBase, restarted.Simulation.World.GetView(owner).BasePosition);
            Assert.Equal(12, restarted.Simulation.World.GetView(owner).Standings.Single(item => item.PlayerId == owner).ColorId);
        }
        finally { finder.Stop(); Directory.Delete(directory, true); }

        void Poll()
        {
            pollingServer.Poll();
            pollingServer.Step();
            finder.PollEvents();
            foreach (var client in clients) client.Poll();
            Thread.Sleep(1);
        }

        void Pump(Func<bool> complete)
        {
            var timer = Stopwatch.StartNew();
            while (!complete() && timer.ElapsedMilliseconds < 5_000) Poll();
            Assert.True(complete(), "LAN 서버가 제한 시간 안에 기대 상태에 도달하지 못했습니다.");
        }

        void PumpFor(int milliseconds)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds) Poll();
        }
    }
}
