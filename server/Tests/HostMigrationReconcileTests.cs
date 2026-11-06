using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>후보 준비 후 끊긴 참가자만 로그아웃하고 나머지 입력을 보존한다.</summary>
public sealed class HostMigrationReconcileTests
{
    [Fact]
    public async Task LateDisconnectRefundsItsTrainingAndKeepsOtherAcceptedInput()
    {
        var rules = GameRulesTests.Rules();
        var roomRules = rules.ForRoom(3);
        var settings = new NetworkSettings(GameRulesTests.Data());
        var accounts = Enumerable.Range(1, 3).Select(index => new AccountIdentity(Guid.NewGuid().ToString("N"), "참가자" + index)).ToArray();
        var setup = new WorldSetup();
        for (int index = 0; index < 3; index++)
        {
            Vector2 position = new(40 + 60 * index, 40 + 60 * index);
            setup.Players.Add(new PlayerSpawn(index + 1, "water", position, position + new Vector2(0, 8),
                cores: 100, natureForge: true, nickname: accounts[index].Nickname));
            setup.Units.Add(new UnitSpawn(index + 1, "water", position + new Vector2(0, 8)));
        }
        var world = new SimulationWorld(roomRules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.Train(1, "water")));
        world.Step();
        Assert.True(world.GetView(2).IsTraining);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(3, SimCommand.Move(1, Vector2.UnitY)));
        var snapshot = new HostMigrationSnapshot
        {
            RoomId = Guid.NewGuid().ToString("N"), Title = "확정 직전 접속 종료", DataHash = rules.DataHash,
            Generation = 1, BackupNumber = 1, Tick = world.Metrics.Tick, MinimumPlayers = 2, Capacity = 3,
            NextPlayerId = 4, World = world.CaptureMigrationState(),
            Members = accounts.Select((account, index) => new MigrationMember(account.Id, account.Nickname, roomRules.ElementIndex("water"), true, index + 1)).ToArray(),
            Players = accounts.Select((account, index) => (account.Id, Owner: index + 1)).ToDictionary(item => item.Id, item => item.Owner)
        };
        using var restored = new OnlineRooms(rules, settings);
        restored.RestoreHostedMigration(snapshot, accounts[2], accounts.Select(account => account.Id).ToArray());
        Assert.Null(restored.ExecuteTask);
        restored.ReconcileHostedMigration(snapshot.RoomId, accounts[2], [accounts[0].Id, accounts[2].Id]);
        Assert.Equal(2, restored.Status(snapshot.RoomId)!.Count);
        Assert.Throws<InvalidDataException>(() => restored.ReconcileHostedMigration(snapshot.RoomId, accounts[2],
            [accounts[2].Id, Guid.NewGuid().ToString("N")]));
        Assert.Null(restored.AttachHosted(snapshot.RoomId, accounts[2], null, out _));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await restored.StartAsync(timeout.Token);
        try
        {
            var recaptured = await restored.CaptureHostedMigration(snapshot.RoomId, 2, freeze: true).WaitAsync(timeout.Token);
            var continued = SimulationWorld.RestoreMigrationState(rules, recaptured.World!);
            WorldView disconnected = continued.GetView(2), active = continued.GetView(3);
            Assert.False(disconnected.IsTraining);
            Assert.Equal(0, disconnected.ReservedPopulation);
            Assert.Equal(100, disconnected.CoreBalance);
            Assert.All(disconnected.Units.Where(unit => unit.OwnerId == 2), unit => Assert.True(unit.Ghost));
            Assert.Equal(1UL, active.AppliedSequence);
            Assert.Equal(Vector2.UnitY, active.Direction);
            Assert.DoesNotContain(recaptured.Members, member => member.AccountId == accounts[1].Id);
            Assert.True(world.GetView(2).IsTraining);
        }
        finally { await restored.StopAsync(CancellationToken.None); }
    }
}
