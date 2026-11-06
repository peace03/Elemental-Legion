using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>AI 난이도의 데이터 검증·판단 주기와 관측 기반 전술을 확인한다.</summary>
public sealed class AiDifficultyTests
{
    [Theory]
    [InlineData("easy.decisionSeconds", -1)]
    [InlineData("hard.retreatHealthRatio", 2)]
    public void InvalidDifficultyRulesFailAtLoading(string path, double value)
    {
        JObject root = GameRulesTests.Data().Root;
        root.SelectToken("ai." + path)!.Replace(value);
        Assert.Throws<ArgumentException>(() => new GameRules(new GameDataDocument(root.ToString())));
    }

    [Fact]
    public void MatchAiWaitsForItsNextDecisionWithoutChangingBenchmarkBots()
    {
        JObject root = GameRulesTests.Data().Root;
        root["ai"]!["normal"]!["decisionSeconds"] = 1;
        var rules = new GameRules(new GameDataDocument(root.ToString()));
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var matchBot = new BotBrain(rules, 1, playMatch: true);
        var benchmarkBot = new BotBrain(rules, 1);
        Assert.NotNull(matchBot.Decide(world.GetView(1)));
        Assert.NotNull(benchmarkBot.Decide(world.GetView(1)));
        world.Step();
        Assert.Null(matchBot.Decide(world.GetView(1)));
        Assert.NotNull(benchmarkBot.Decide(world.GetView(1)));
    }

    [Fact]
    public void DifficultyStateAndCommandsRejectInvalidOrTruncatedPackets()
    {
        var writer = new NetDataWriter();
        var rules = GameRulesTests.Rules();
        AiDifficulty[] levels = [AiDifficulty.Easy, AiDifficulty.Normal, AiDifficulty.Hard];
        M5Protocol.WriteLobbyState(writer, "각기 다른 AI", 0, 2, 4, [new LobbyMember("방장", 0, true)], 3, levels);
        byte[] packet = writer.CopyData();
        Assert.True(M5Protocol.TryReadLobbyState(new NetDataReader(packet), rules, out var lobby));
        Assert.Equal(levels, lobby.AiDifficulties);
        for (int length = 0; length < packet.Length; length++)
            Assert.False(M5Protocol.TryReadLobbyState(new NetDataReader(packet[..length]), rules, out _));
        packet[6] = 3;
        Assert.False(M5Protocol.TryReadLobbyState(new NetDataReader(packet), rules, out _));

        M5Protocol.WriteLobbyAiDifficulty(writer, 2, AiDifficulty.Hard);
        packet = writer.CopyData();
        Assert.True(M5Protocol.TryReadLobbyAiDifficulty(new NetDataReader(packet), out int index, out var level));
        Assert.Equal(2, index);
        Assert.Equal(AiDifficulty.Hard, level);
        for (int length = 0; length < packet.Length; length++)
            Assert.False(M5Protocol.TryReadLobbyAiDifficulty(new NetDataReader(packet[..length]), out _, out _));
        packet[2] = 255;
        Assert.False(M5Protocol.TryReadLobbyAiDifficulty(new NetDataReader(packet), out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => M5Protocol.WriteLobbyAiDifficulty(writer, 0, (AiDifficulty)255));
    }

    [Fact]
    public void DifficultyChangesDecisionCadenceAndSurvivesBrainRestore()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, WorldCoreTests.Setup(), 1);
        var bots = new[] { AiDifficulty.Easy, AiDifficulty.Normal, AiDifficulty.Hard }
            .Select(level => new BotBrain(rules, 1, playMatch: true, difficulty: level)).ToArray();
        int[] decisions = new int[bots.Length];
        for (int tick = 0; tick < rules.TickRate; tick++)
        {
            for (int index = 0; index < bots.Length; index++)
                if (bots[index].Decide(world.GetView(1)).HasValue) decisions[index]++;
            world.Step();
        }
        Assert.True(decisions[0] < decisions[1] && decisions[1] < decisions[2]);
        var restored = bots.Select(bot => BotBrain.RestoreState(rules,
            JsonConvert.DeserializeObject<BotBrainState>(JsonConvert.SerializeObject(bot.CaptureState()))!)).ToArray();
        for (int tick = 0; tick < rules.TickRate; tick++)
        {
            for (int index = 0; index < bots.Length; index++)
            {
                Assert.Equal(bots[index].Difficulty, restored[index].Difficulty);
                Assert.Equal(bots[index].Decide(world.GetView(1)), restored[index].Decide(world.GetView(1)));
            }
            world.Step();
        }
    }

    [Fact]
    public void HardAiCountersOnlyObservedEnemiesAndNormalAiTrainsItsMainElement()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup();
        setup.Units.Add(CombatTests.Unit(1, "water", 50, garrison: false));
        setup.Units.Add(CombatTests.Unit(-1, "water", 60));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 180, 200)));
        world.Step();
        Assert.Contains(world.GetView(1).Buildings, building => building.Kind == BuildingKind.Collector);
        Assert.Contains(world.GetView(1).Units, unit => unit.OwnerId < 0);
        var normal = new BotBrain(rules, 1, playMatch: true);
        var hard = new BotBrain(rules, 1, playMatch: true, difficulty: AiDifficulty.Hard);
        SimCommand main = normal.Decide(world.GetView(1))!.Value;
        SimCommand counter = hard.Decide(world.GetView(1))!.Value;
        Assert.Equal(CommandKind.Train, main.Kind);
        Assert.Equal("water", main.Element);
        Assert.Equal(CommandKind.Train, counter.Kind);
        Assert.True(rules.Matchup(rules.ElementIndex(counter.Element), rules.ElementIndex("water")) > 1);

        var hidden = WorldCoreTests.Setup();
        hidden.Units.Add(CombatTests.Unit(1, "water", 50, garrison: false));
        hidden.Units.Add(CombatTests.Unit(-1, "water", rules.MapSize - 10));
        var unseen = new SimulationWorld(rules, hidden, 1);
        unseen.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 180, 200));
        unseen.Step();
        Assert.Equal("water", new BotBrain(rules, 1, true, AiDifficulty.Hard).Decide(unseen.GetView(1))!.Value.Element);
    }

    [Fact]
    public void InjuredHardAiRetreatsAndPreservesTheRetreatAcrossMigration()
    {
        var rules = GameRulesTests.Rules();
        var setup = WorldCoreTests.Setup(cores: 0);
        setup.Units.Add(CombatTests.Unit(1, "water", 50, hp: 1, garrison: false));
        setup.Units.Add(CombatTests.Unit(-1, "earth", 60));
        var world = new SimulationWorld(rules, setup, 1);
        var hard = new BotBrain(rules, 1, true, AiDifficulty.Hard);
        SimCommand retreat = hard.Decide(world.GetView(1))!.Value;
        Assert.Equal(CommandKind.MoveDir, retreat.Kind);
        Assert.True(retreat.Direction.X > 0 && retreat.Direction.Y > 0);
        var restored = BotBrain.RestoreState(rules, JsonConvert.DeserializeObject<BotBrainState>(JsonConvert.SerializeObject(hard.CaptureState()))!);
        for (int tick = 0; tick < 20; tick++)
        {
            world.Step();
            Assert.Equal(hard.Decide(world.GetView(1)), restored.Decide(world.GetView(1)));
        }
    }
}
