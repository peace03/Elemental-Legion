using System.Collections;
using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>여러 완료 경계와 미적용 명령이 겹쳐도 승계 전후의 결과가 같아야 한다.</summary>
public sealed class MainWorldMigrationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MigrationPreservesEveryInFlightMainWorldOperation(bool expires)
    {
        var data = GameRulesTests.Data().Root;
        data["economy"]!["pvpRecovery"]!["instant"] = false;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(200), 4, 10000, natureForge: true));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(700), new Vector2(720), 4));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(201, 200), .01f));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(300)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(140)));
        var original = new SimulationWorld(rules, setup, 1);
        object state = State(original), player = Item(state, "PlayerById", 1);
        Set(Item(state, "UnitById", 3), "SquadId", (byte)3);
        Array squads = (Array)Get(player, "Squads");
        Set(squads.GetValue(2)!, "Position", new Vector2(300));
        Set(squads.GetValue(2)!, "Destination", new Vector2(300));
        object reinforcement = Item(state, "UnitById", 4);
        Set(reinforcement, "SquadId", (byte)0);
        Set(reinforcement, "DestinationSquadId", (byte)2);
        Set(reinforcement, "IsReinforcing", true);
        Assert.Equal(CommandAdmission.Accepted, original.TryEnqueue(1, SimCommand.Train(1, "water", 2, 3)));
        Assert.Equal(CommandAdmission.Accepted, original.TryEnqueue(1, SimCommand.Build(2, BuildingKind.Collector, 100, 121)));
        original.Step();
        Assert.Single(original.GetView(1).Candidates);
        Assert.Equal(CommandAdmission.Accepted, original.TryEnqueue(1, SimCommand.Recall(3, 3)));
        original.Step();
        Assert.Equal((byte)3, original.GetView(1).RecallSquad);

        // 장시간 실제 대기 대신, 검증 가능한 완료 직전의 서버 상태를 준비한다.
        Set(state, "Tick", 400L);
        Set(player, "RecallEndTick", 401L);
        Set(player, "TrainingStartTick", 401L - (long)Math.Ceiling(rules.TrainingSeconds(rules.ElementIndex("water"), rules.ElementIndex("water")) * rules.TickRate));
        Set(player, "TrainingEndTick", 401L);
        Set(player, "NextDefenseAlarmTick", 401L);
        original.Metrics.GetType().GetProperty("Tick")!.SetValue(original.Metrics, 400L);
        var candidates = (IList)Get(state, "Candidates");
        object candidate = candidates[0]!;
        Set(candidate, "SecureProgress", 59);
        if (!expires)
        {
            Set(candidate, "CreatedTick", 341L);
            Set(candidate, "EndTick", 341L + rules.PvpRecoveryLifetimeTicks);
        }
        candidates[0] = candidate;
        foreach (object building in (IEnumerable)Get(state, "Buildings"))
            if ((BuildingKind)Get(building, "Kind") == BuildingKind.Collector)
                Set(building, "ConstructionEndTick", 401L);
        Assert.Equal(CommandAdmission.Accepted, original.TryEnqueue(1, SimCommand.Move(4, 1, Vector2.UnitX)));
        Assert.Equal(CommandAdmission.Accepted, original.TryEnqueue(1, SimCommand.AcknowledgeJourney(5)));
        Assert.Equal(CommandAdmission.Accepted, original.TryEnqueue(1, SimCommand.Train(6, "water", 1, 2)));

        var captured = JObject.FromObject(original.CaptureMigrationState());
        Assert.Equal(expires ? 401L : 341L + rules.PvpRecoveryLifetimeTicks, (long)captured["World"]!["Candidates"]![0]!["EndTick"]!);
        Assert.Equal(59, (int)captured["World"]!["Candidates"]![0]!["SecureProgress"]!);
        var restored = SimulationWorld.RestoreMigrationState(rules, captured.ToObject<MigrationWorldState>()!);
        Assert.True(JToken.DeepEquals(captured, JObject.FromObject(restored.CaptureMigrationState())));
        Assert.Equal(CommandAdmission.StaleSequence, restored.TryEnqueue(1, SimCommand.AcknowledgeJourney(5)));

        for (int tick = 0; tick < 130; tick++)
        {
            original.Step();restored.Step();
            Assert.True(JToken.DeepEquals(JObject.FromObject(original.CaptureMigrationState()), JObject.FromObject(restored.CaptureMigrationState())));
            Assert.True(JToken.DeepEquals(JArray.FromObject(original.GetView(1).Events), JArray.FromObject(restored.GetView(1).Events)));
            if (tick != 0) continue;
            var view = restored.GetView(1);
            Assert.False(restored.HasConversionCandidates);
            Assert.Equal(!expires, view.Events.Any(item => item.Kind == SimEventKind.Converted));
            Assert.Equal(expires, view.Events.Any(item => item.Kind == SimEventKind.CoreDropped));
            Assert.Equal((byte)0, view.RecallSquad);
            Assert.Equal(view.Tick + rules.RecallCooldownTicks, view.RecallReadyTick);
            Assert.Equal(1, view.TrainedCount);
            Assert.Contains(view.Buildings, building => building.Kind == BuildingKind.Collector && !building.IsUnderConstruction);
            Assert.Equal((ulong)4, view.Squads[0].AppliedSequence);
            Assert.True(view.Squads[1].ReinforcingCount > 0);
            Assert.Equal(rules.TrainingCost(rules.ElementIndex("water"), rules.ElementIndex("water")), view.Journey.SinceLast.CoresSpent);
            Assert.Equal(expires ? rules.DropAmount(rules.ElementIndex("earth")) : 0, view.Journey.SinceLast.CoresIn);
            Assert.Equal(expires ? 0 : 1, view.Journey.SinceLast.Converted);
        }
    }

    private static object State(SimulationWorld world) => typeof(SimulationWorld).GetField("state", Hidden)!.GetValue(world)!;
    private static object Get(object value, string name) => value.GetType().GetField(name, Hidden)!.GetValue(value)!;
    private static object Item(object value, string field, int id) => ((IDictionary)Get(value, field))[id]!;
    private static void Set(object value, string name, object content) => value.GetType().GetField(name, Hidden)!.SetValue(value, content);
}
