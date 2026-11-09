using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>디스크 복귀의 정지 시간과 주문 번호가 세션 순번과 독립적인지 확인한다.</summary>
public sealed class DiskTrainingPauseTests
{
    [Fact]
    public void DiskRestartPausesPaidOrdersThroughResaveMigrationAndResume()
    {
        var rules = GameRulesTests.Rules();
        var world = TrainingWorld(rules);
        for (int i = 0; i < 40; i++) world.Step();
        var before = world.GetView(1);
        long remaining = before.TrainingEndTick - before.Tick;
        long balance = before.CoreBalance;
        var ids = before.TrainingQueue.Select(order => order.Id).ToArray();
        var restored = SimulationWorld.RestoreCheckpoint(rules, RoundTrip(world.CaptureCheckpoint()));
        Assert.Equal(balance, restored.GetView(1).CoreBalance);
        Assert.Equal(ids, restored.GetView(1).TrainingQueue.Select(order => order.Id));
        for (int i = 0; i < 180; i++) restored.Step();
        Assert.Equal(0, restored.Metrics.Trained);
        restored = SimulationWorld.RestoreCheckpoint(rules, RoundTrip(restored.CaptureCheckpoint()));
        restored = SimulationWorld.RestoreMigrationState(rules, JsonConvert.DeserializeObject<MigrationWorldState>(
            JsonConvert.SerializeObject(restored.CaptureMigrationState()))!);
        Assert.False(restored.TryResumeGhost(2));
        Assert.True(restored.TryResumeGhost(1));
        Assert.Equal(remaining, restored.GetView(1).TrainingEndTick - restored.GetView(1).Tick);
        for (int i = 1; i < remaining; i++) restored.Step();
        Assert.Equal(0, restored.Metrics.Trained);
        restored.Step();
        Assert.Equal(1, restored.Metrics.Trained);
        Assert.Equal(balance, restored.GetView(1).CoreBalance);
    }

    [Fact]
    public void RestoredOrdersUseIndependentIdsAndCancelAfterInputSequenceReset()
    {
        var rules = GameRulesTests.Rules();
        var world = SimulationWorld.RestoreCheckpoint(rules, TrainingWorld(rules).CaptureCheckpoint());
        Assert.True(world.TryResumeGhost(1));
        ulong waiting = Assert.Single(world.GetView(1).TrainingQueue).Id;
        var writer = new NetDataWriter();
        Protocol.WriteCancelTraining(writer, 1, waiting);
        Assert.True(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var cancel));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, cancel));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(2, "water")));
        world = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        world.Step();
        var orders = world.GetView(1).TrainingQueue;
        Assert.Equal(2, world.GetView(1).ReservedPopulation);
        Assert.DoesNotContain(orders, order => order.Id == waiting);
        Assert.True(Assert.Single(orders).Id > waiting);
    }

    [Fact]
    public void VoluntaryLogoutStillRefundsAllOrdersOnce()
    {
        var world = TrainingWorld(GameRulesTests.Rules());
        Assert.True(world.BeginGhost(1));
        Assert.Equal(1000, world.GetView(1).CoreBalance);
        Assert.Empty(world.GetView(1).TrainingQueue);
        Assert.False(world.BeginGhost(1));
        Assert.Equal(1000, world.GetView(1).CoreBalance);
    }

    [Theory]
    [InlineData("TrainingStartTick")]
    [InlineData("TrainingEndTick")]
    [InlineData("TrainingPausedAtTick")]
    [InlineData("NextTrainingOrderId")]
    public void MigrationRejectsTimerAndIdDisagreementWithItsCheckpoint(string field)
    {
        var rules = GameRulesTests.Rules();
        var world = SimulationWorld.RestoreCheckpoint(rules, TrainingWorld(rules).CaptureCheckpoint());
        for (int i = 0; i < 150; i++) world.Step();
        var json = JObject.FromObject(world.CaptureMigrationState());
        json["Players"]![0]![field] = json["Players"]![0]![field]!.Value<long>() + 1;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(rules, json.ToObject<MigrationWorldState>()!));
    }

    [Fact]
    public void MissingOrderCancellationMigratesAndIsRejectedOnlyOnExecution()
    {
        var rules = GameRulesTests.Rules();
        var world = TrainingWorld(rules);
        long balance = world.GetView(1).CoreBalance;
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.CancelTraining(9, 999)));
        var restored = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        restored.Step();
        Assert.Equal(balance, restored.GetView(1).CoreBalance);
        Assert.Equal(2, restored.GetView(1).ReservedPopulation);
        Assert.Contains(restored.GetView(1).Events, item => item.Sequence == 9 &&
            item.Kind == SimEventKind.CommandRejected && item.Detail == (int)CommandFailure.TrainingNotWaiting);
    }

    [Fact]
    public void ExhaustedOrderIdsRejectWithoutChargingOrWrapping()
    {
        var rules = GameRulesTests.Rules();
        var json = JObject.FromObject(TrainingWorld(rules).CaptureCheckpoint());
        json["Players"]![0]!["NextTrainingOrderId"] = ulong.MaxValue;
        var world = SimulationWorld.RestoreCheckpoint(rules, json.ToObject<WorldCheckpoint>()!);
        Assert.True(world.TryResumeGhost(1));
        long balance = world.GetView(1).CoreBalance;
        world.TryEnqueue(1, SimCommand.Train(1, "water"));
        world.Step();
        Assert.Equal(balance, world.GetView(1).CoreBalance);
        Assert.Equal(2, world.GetView(1).ReservedPopulation);
        Assert.Contains(world.GetView(1).Events, item => item.Kind == SimEventKind.CommandRejected &&
            item.Detail == (int)CommandFailure.Busy);
    }

    [Fact]
    public void EmptyQueueRejectsTimersAndPausedQueueRejectsInvalidFreezeTimes()
    {
        var rules = GameRulesTests.Rules();
        var empty = JObject.FromObject(new SimulationWorld(rules, WorldCoreTests.Setup(), 1).CaptureCheckpoint());
        empty["Players"]![0]!["TrainingEndTick"] = 1;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, empty.ToObject<WorldCheckpoint>()!));
        var paused = SimulationWorld.RestoreCheckpoint(rules, TrainingWorld(rules).CaptureCheckpoint());
        for (int i = 0; i < 150; i++) paused.Step();
        var json = JObject.FromObject(paused.CaptureCheckpoint());
        json["Players"]![0]!["TrainingPausedAtTick"] = paused.GetView(1).Tick + 1;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, json.ToObject<WorldCheckpoint>()!));
    }

    [Theory]
    [InlineData("SchemaVersion", 1)]
    [InlineData("Players[0].TrainingStartTick", -1)]
    [InlineData("Players[0].TrainingEndTick", 1)]
    [InlineData("Players[0].NextTrainingOrderId", 1)]
    [InlineData("Players[0].TrainingPausedAtTick", 1)]
    public void CheckpointRejectsInvalidSchemaTimersAndOrderIds(string path, long value)
    {
        var rules = GameRulesTests.Rules();
        var json = JObject.FromObject(TrainingWorld(rules).CaptureCheckpoint());
        // 신규 필드가 없는 구현도 저장 계약 위반으로 검출한다.
        var token = json.SelectToken(path);
        Assert.NotNull(token);
        token!.Replace(value);
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(rules, json.ToObject<WorldCheckpoint>()!));
    }

    internal static SimulationWorld TrainingWorld(GameRules rules)
    {
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(cores: 1000), 7);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(7, "water")));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(8, "water")));
        world.Step();
        return world;
    }

    [Fact]
    public void DestroyedForgeKeepsOtherPausedOrdersThroughResaveAndMigration()
    {
        var data = GameRulesTests.Data().Root;
        data["buildings"]!["forge_nature"]!["hp"] = 1;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        var attacker = new Vector2(214, 208);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(400), attacker));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200), new Vector2(200, 225), 4, 100, true, true));
        setup.Units.Add(new UnitSpawn(1, "earth", attacker));
        var world = new SimulationWorld(rules, setup, 1);
        world.TryEnqueue(2, SimCommand.Train(1, "water"));
        world.TryEnqueue(2, SimCommand.Train(2, "fire"));
        world.TryEnqueue(2, SimCommand.Train(3, "light"));
        world.Step();
        Assert.True(world.GetView(2).NatureForge);
        world = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        Assert.True(world.TryResumeGhost(1));
        for (int tick = 0; tick < 100 && world.GetView(2).NatureForge; tick++) world.Step();
        var view = world.GetView(2);
        Assert.False(view.NatureForge);
        Assert.Equal(75, view.CoreBalance);
        Assert.Equal(rules.ElementIndex("light"), view.TrainingElement);
        Assert.Equal(1, view.ReservedPopulation);
        for (int tick = 0; tick < 100; tick++) world.Step();
        Assert.Equal(0, view.TrainedCount);
        world = SimulationWorld.RestoreCheckpoint(rules, RoundTrip(world.CaptureCheckpoint()));
        world = SimulationWorld.RestoreMigrationState(rules, JsonConvert.DeserializeObject<MigrationWorldState>(
            JsonConvert.SerializeObject(world.CaptureMigrationState()))!);
        Assert.True(world.TryResumeGhost(2));
        view = world.GetView(2);
        Assert.Equal(75, view.CoreBalance);
        long remaining = view.TrainingEndTick - view.Tick;
        Assert.Equal((long)Math.Ceiling(rules.TrainingSeconds(view.TrainingElement, view.MainElement) * rules.TickRate), remaining);
        for (int tick = 0; tick < remaining - 1; tick++) world.Step();
        Assert.Equal(0, view.TrainedCount);
        world.Step();
        Assert.Equal(1, view.TrainedCount);
        Assert.Equal(0, view.ReservedPopulation);
    }

    private static WorldCheckpoint RoundTrip(WorldCheckpoint checkpoint) =>
        JsonConvert.DeserializeObject<WorldCheckpoint>(JsonConvert.SerializeObject(checkpoint))!;
}
