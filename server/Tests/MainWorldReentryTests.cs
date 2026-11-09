using System.Diagnostics;
using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>정상 입장·명령 경로에서 재입장 보상과 방 전장 호환성을 검사한다.</summary>
public sealed class MainWorldReentryTests
{
    [Fact]
    public void AnonymousAndAccountReentryCannotDuplicateAssets()
    {
        var rules = GameRulesTests.Rules();
        var settings = new NetworkSettings(GameRulesTests.Data());
        string directory = Directory.CreateTempSubdirectory("elemental-reentry-").FullName;
        try
        {
            using var server = new UdpGameServer(rules, settings, accounts: new FileAccountStore(directory));
            server.Start(0);
            using var anonymous = new ClientConnection(rules, settings);
            using var account = new ClientConnection(rules, settings);
            using var resumed = new ClientConnection(rules, settings);
            using var duplicate = new ClientConnection(rules, settings);
            using var newcomer = new ClientConnection(rules, settings);
            ClientConnection[] clients = [anonymous, account, resumed, duplicate, newcomer];
            int element = rules.StartingElements[0];

            Connect(anonymous);
            int oldAnonymous = anonymous.PlayerId;
            int[] anonymousUnits = OwnUnitIds(server.Simulation, oldAnonymous);
            AssertStartingAssets(server.Simulation, oldAnonymous);
            anonymous.Disconnect();
            Pump(() => server.Simulation.ConnectedCount == 0);
            Assert.Equal(CommandAdmission.UnknownPlayer, server.Simulation.Move(oldAnonymous, 77, Vector2.UnitX));
            Connect(anonymous);
            Assert.False(anonymous.Resumed);
            Assert.NotEqual(oldAnonymous, anonymous.PlayerId);
            AssertStartingAssets(server.Simulation, anonymous.PlayerId);
            Assert.Equal(anonymousUnits, OwnUnitIds(server.Simulation, oldAnonymous));
            Assert.Empty(anonymousUnits.Intersect(OwnUnitIds(server.Simulation, anonymous.PlayerId)));

            Connect(account, createAccount: true);
            int owner = account.PlayerId;
            byte[] key = Assert.IsType<byte[]>(account.IssuedAccountKey).ToArray();
            AssertStartingAssets(server.Simulation, owner);
            var baseline = server.Simulation.World.GetView(owner);
            Vector2 home = baseline.BasePosition;
            int[] units = OwnUnitIds(server.Simulation, owner);
            int[] buildings = OwnBuildingIds(server.Simulation, owner);
            long protection = ProtectionEnd(server.Simulation, owner);
            Assert.Equal(30L * rules.TickRate, protection - baseline.Tick);

            // 살아 있는 연결의 계정 키는 두 번째 세션을 만들 수 없다.
            duplicate.Connect("127.0.0.1", server.Port, element, resumeKey: key);
            Pump(() => duplicate.State == ConnectionState.Disconnected);
            Assert.Equal(JoinFailure.AlreadyConnected, duplicate.JoinFailure);
            Assert.Equal(units, OwnUnitIds(server.Simulation, owner));
            Assert.Equal(3, server.Simulation.World.PlayerCount);

            CommandAdmission? response = null;
            account.CommandResponse += (_, result) => response = result;
            account.SendRecall(1, 1);
            Pump(() => response.HasValue);
            Assert.Equal(CommandAdmission.Accepted, response);
            for (int tick = 0; tick <= rules.RecallCastTicks; tick++) server.Simulation.Step();
            long ready = server.Simulation.World.GetView(owner).RecallReadyTick;
            Assert.Equal(server.Simulation.World.GetView(owner).Tick + rules.RecallCooldownTicks, ready);

            // 접수됐지만 아직 실행하지 않은 구 연결의 이동도 로그아웃에서 제거한다.
            response = null;
            account.SendMove(2, Vector2.UnitX);
            Pump(() => response.HasValue);
            Assert.Equal(CommandAdmission.Accepted, response);
            Vector2 position = server.Simulation.World.GetView(owner).LegionPosition;
            byte[] explored = server.Simulation.World.GetView(owner).Vision.Explored.ToArray();
            account.Disconnect();
            Pump(() => !server.Simulation.ConnectedOwners.Contains(owner));
            Assert.Equal(CommandAdmission.UnknownPlayer, server.Simulation.Command(owner, SimCommand.Recall(99, 1)));
            resumed.Connect("127.0.0.1", server.Port, rules.StartingElements[1], "다른표시이름", resumeKey: key);
            Pump(() => resumed.State == ConnectionState.Connected);
            Assert.True(resumed.Resumed);
            Assert.Equal(owner, resumed.PlayerId);
            Assert.Equal(element, server.Simulation.World.GetView(owner).MainElement);
            Assert.Equal(home, server.Simulation.World.GetView(owner).BasePosition);
            Assert.Equal(explored, server.Simulation.World.GetView(owner).Vision.Explored.ToArray());
            Assert.Equal(units, OwnUnitIds(server.Simulation, owner));
            Assert.Equal(buildings, OwnBuildingIds(server.Simulation, owner));
            Assert.Equal(0, server.Simulation.World.GetView(owner).CoreBalance);
            Assert.Equal(ready, server.Simulation.World.GetView(owner).RecallReadyTick);
            Assert.Equal(0, ProtectionEnd(server.Simulation, owner));
            server.Simulation.Step();
            Assert.Equal(position, server.Simulation.World.GetView(owner).LegionPosition);
            Assert.Equal(Vector2.Zero, server.Simulation.World.GetView(owner).Direction);

            response = null;
            resumed.CommandResponse += (_, result) => response = result;
            resumed.SendRecall(1, 2);
            Pump(() => response.HasValue);
            Assert.Equal(CommandAdmission.Accepted, response);
            server.Simulation.Step();
            Assert.Contains(server.Simulation.World.GetView(owner).Events, item =>
                item.Kind == SimEventKind.CommandRejected && item.Sequence == 1 && item.Detail == (int)CommandFailure.Busy);
            Assert.Equal(ready, server.Simulation.World.GetView(owner).RecallReadyTick);
            response = null;
            resumed.SendMove(1, Vector2.UnitY);
            Pump(() => response.HasValue);
            Assert.Equal(CommandAdmission.StaleSequence, response);
            response = null;
            resumed.SendBuild(2, BuildingKind.Collector, (int)(home.X / rules.BuildGridSize), (int)(home.Y / rules.BuildGridSize));
            Pump(() => response.HasValue);
            Assert.Equal(CommandAdmission.Accepted, response);
            server.Simulation.Step();
            Assert.Contains(server.Simulation.World.GetView(owner).Events, item =>
                item.Kind == SimEventKind.CommandRejected && item.Sequence == 2 && item.Detail == (int)CommandFailure.InsufficientCores);
            Assert.Equal(buildings, OwnBuildingIds(server.Simulation, owner));

            // 구 ClientConnection은 복귀한 소유자의 연결로 다시 사용되지 않는다.
            account.SendMove(1000, Vector2.UnitX);
            Connect(newcomer, createAccount: true);
            Assert.NotEqual(owner, newcomer.PlayerId);
            Assert.False(newcomer.Resumed);
            AssertStartingAssets(server.Simulation, newcomer.PlayerId);
            Assert.Equal(ready, server.Simulation.World.GetView(owner).RecallReadyTick);
            Assert.Equal(units, OwnUnitIds(server.Simulation, owner));
            Assert.Equal(0, server.Simulation.World.GetView(owner).CoreBalance);
            Assert.False(server.Simulation.World.GetView(owner).NatureForge);
            Assert.False(server.Simulation.World.GetView(owner).EnergyForge);
            Assert.Equal(4, server.Simulation.World.PlayerCount);

            void Connect(ClientConnection client, bool createAccount = false)
            {
                client.Connect("127.0.0.1", server.Port, element, requestAccount: createAccount);
                Pump(() => client.State == ConnectionState.Connected);
            }

            void Pump(Func<bool> complete) => PumpUdp(complete, server, clients);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredAnonymousAndAccountJourneysCannotFarmFreeAssetsOrOpeningSupply(bool accountBased)
    {
        var data = GameRulesTests.Data();
        // 수명만 짧게 하여 정상 만료 경로를 같은 테스트 안에서 반복한다.
        data.Root["ghost"]!["decaySeconds"] = data.Root["ghost"]!["reconnectWindowSeconds"] = 0.1;
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        string directory = Directory.CreateTempSubdirectory("elemental-reentry-expiry-").FullName;
        try
        {
            using var server = new UdpGameServer(rules, settings, accounts: new FileAccountStore(directory));
            using var client = new ClientConnection(rules, settings);
            server.Start(0);
            byte[]? key = null;
            Vector2? firstHome = null;
            for (int journey = 0; journey < 3; journey++)
            {
                client.Connect("127.0.0.1", server.Port, rules.StartingElements[0],
                    resumeKey: key, requestAccount: accountBased && key == null);
                PumpUdp(() => client.State == ConnectionState.Connected, server, client);
                if (accountBased && key == null) key = Assert.IsType<byte[]>(client.IssuedAccountKey).ToArray();
                int owner = client.PlayerId;
                Assert.False(client.Resumed);
                AssertStartingAssets(server.Simulation, owner);
                var view = server.Simulation.World.GetView(owner);
                firstHome ??= view.BasePosition;
                Assert.Equal(firstHome.Value, view.BasePosition);
                server.Simulation.Step();
                Assert.Equal(rules.OpeningCoreBundleSize, TotalCores(server.Simulation));
                client.Disconnect();
                PumpUdp(() => server.Simulation.ConnectedCount == 0, server, client);
                for (int tick = 0; tick <= rules.GhostDecayTicks; tick++) server.Simulation.Step();
                Assert.False(server.Simulation.TryResume(owner));
                Assert.Equal(0, server.Simulation.World.PlayerCount);
                Assert.Equal(rules.OpeningCoreBundleSize, TotalCores(server.Simulation));
                Assert.Empty(Snapshot(server.Simulation)["Buildings"]!);
                Assert.DoesNotContain(Snapshot(server.Simulation)["Units"]!, item => (int)item["Owner"]! >= 0);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(15, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(15, true)]
    public void RoomRosterKeepsSmallMapDensityAssetsAndSupplyThroughResumeAndRestore(int players, bool mixedAi)
    {
        var data = GameRulesTests.Data();
        var mainRules = new GameRules(data);
        var rules = mainRules.ForRoom(players);
        var settings = new NetworkSettings(data);
        var room = new NetworkWorld(rules, settings);
        Assert.Equal((float)Math.Round(240 * Math.Sqrt(players / 2.0)), rules.MapSize);
        Assert.True(rules.MapSize < mainRules.MapSize);
        Assert.Equal(mainRules.DataHash, rules.DataHash);
        int groups = (int)Math.Ceiling(rules.MapSize * (double)rules.MapSize * rules.NeutralWorldGroupsPerSquareKm / 1_000_000);
        Assert.Equal(groups, Snapshot(room)["Ecology"]!["Groups"]!.Count());
        Assert.InRange(room.World.Metrics.NeutralUnits, groups, groups * rules.NeutralMaxGroupSize);
        int humanCount = mixedAi ? 1 : players;
        var humans = new List<int>();
        for (int index = 0; index < humanCount; index++)
        {
            Assert.Equal(JoinFailure.None, room.TryJoin(rules.StartingElements[index % rules.StartingElements.Count], out int owner));
            humans.Add(owner);
        }
        if (mixedAi) Assert.Equal(JoinFailure.None, room.AddAiOpponents(players - 1, rules.StartingElements[0]));
        Assert.Equal(players, room.World.PlayerCount);
        Assert.Equal(humanCount, room.ConnectedCount);
        Assert.Equal(JoinFailure.WorldFull, room.TryJoin(rules.StartingElements[0], out _));
        int[] owners = Snapshot(room)["Players"]!.Select(item => (int)item["Id"]!).ToArray();
        foreach (int owner in owners) AssertStartingAssets(room, owner);
        var homes = owners.Select(owner => room.World.GetView(owner).BasePosition).ToArray();
        for (int index = 0; index < homes.Length; index++)
            foreach (Vector2 other in homes.Skip(index + 1))
                Assert.True(Vector2.Distance(homes[index], other) >= rules.RoomMinBaseDistance);
        room.Step();
        Assert.Equal(players * rules.OpeningCoreBundleSize, TotalCores(room));
        Assert.Equal(0, room.World.Metrics.Attacks);
        int human = humans[0];
        int[] unitIds = OwnUnitIds(room, human);
        int[] buildingIds = OwnBuildingIds(room, human);
        room.Leave(human);
        Assert.Equal(CommandAdmission.UnknownPlayer, room.Move(human, 1, Vector2.UnitX));
        Assert.True(room.TryResume(human));
        Assert.False(room.TryResume(human));
        Assert.Equal(unitIds, OwnUnitIds(room, human));
        Assert.Equal(buildingIds, OwnBuildingIds(room, human));
        var before = Snapshot(room);
        var migrated = new NetworkWorld(mainRules, settings, room.World.CaptureMigrationState(), room.NextPlayerId,
            room.CaptureAiMigration(), humans, humans);
        Assert.Equal(rules.MapSize, migrated.Rules.MapSize);
        Assert.True(JToken.DeepEquals(before, Snapshot(migrated)));
        var disk = new NetworkWorld(mainRules, settings, room.World.CaptureCheckpoint(), room.NextPlayerId,
            aiOwners: room.CaptureAiOwners(), aiDifficulties: room.CaptureAiDifficulties());
        foreach (int owner in humans) Assert.True(disk.TryResume(owner));
        Assert.Equal(rules.MapSize, disk.Rules.MapSize);
        Assert.Equal(unitIds, OwnUnitIds(disk, human));
        Assert.Equal(buildingIds, OwnBuildingIds(disk, human));
        Assert.True(JToken.DeepEquals(before["Ecology"], Snapshot(disk)["Ecology"]));
        foreach (var value in new[] { room, migrated, disk })
        {
            value.Step();
            Assert.Equal(players * rules.OpeningCoreBundleSize, TotalCores(value));
            Assert.Equal(unitIds, OwnUnitIds(value, human));
        }
    }

    private static void AssertStartingAssets(NetworkWorld world, int owner)
    {
        var view = world.World.GetView(owner);
        UnitView[] units = view.Units.Where(unit => unit.OwnerId == owner).ToArray();
        Assert.Equal(12, units.Length);
        Assert.Equal(8, units.Count(unit => unit.SquadId == 1 && !unit.Garrison));
        Assert.Equal(4, units.Count(unit => unit.SquadId == 4 && unit.Garrison));
        Assert.Equal(1, view.BaseLevel);
        Assert.Equal(0, view.CoreBalance);
        Assert.Equal(0, view.Journey.Total.CoresIn);
        Assert.Equal(BuildingKind.Base, Assert.Single(view.Buildings, building => building.OwnerId == owner).Kind);
        JObject saved = Snapshot(world);
        Assert.All(saved["Units"]!.Concat(saved["Buildings"]!).Where(item => (int)item["Owner"]! == owner),
            item => Assert.True((bool)item["FreeOrigin"]!));
    }

    private static int[] OwnUnitIds(NetworkWorld world, int owner) => world.World.GetView(owner).Units
        .Where(unit => unit.OwnerId == owner).Select(unit => unit.Id).Order().ToArray();
    private static int[] OwnBuildingIds(NetworkWorld world, int owner) => world.World.GetView(owner).Buildings
        .Where(building => building.OwnerId == owner).Select(building => building.Id).Order().ToArray();
    private static JObject Snapshot(NetworkWorld world) => JObject.FromObject(world.World.CaptureCheckpoint());
    private static long ProtectionEnd(NetworkWorld world, int owner) => (long)JObject.FromObject(world.World.CaptureMigrationState())
        ["Players"]!.Single(item => (int)item["Id"]! == owner)["ProtectionEndTick"]!;
    private static long TotalCores(NetworkWorld world)
    {
        JObject saved = Snapshot(world);
        return saved["Cores"]!.Sum(item => (long)item["Amount"]!) + saved["Players"]!.Sum(item => (long)item["Cores"]!);
    }

    private static void PumpUdp(Func<bool> complete, UdpGameServer server, params ClientConnection[] clients)
    {
        var timeout = Stopwatch.StartNew();
        // 인증 대기 시간이 보호·공급에 영향을 주지 않도록 명시한 Step에서만 틱을 진행한다.
        while (!complete() && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            server.Poll();
            foreach (var client in clients) client.Poll();
            Thread.Sleep(1);
        }
        Assert.True(complete(), "UDP 입장 또는 명령 응답이 제한 시간 안에 완료되지 않았습니다.");
    }
}
