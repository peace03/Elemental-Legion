using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>AI별 난이도의 디스크·승계 복원과 손상된 저장 입력 거절을 확인한다.</summary>
public sealed class AiDifficultyPersistenceTests
{
    [Fact]
    public void SelectedDifficultiesSurviveDiskRestoreAndHostMigrationInOwnerOrder()
    {
        var (match, rules, settings, human) = CreateRoom();
        Assert.Null(match.CaptureAiOwners());
        Assert.Null(match.CaptureAiDifficulties());
        AiDifficulty[] selected = [AiDifficulty.Easy, AiDifficulty.Hard];
        Assert.Equal(JoinFailure.None, match.AddAiOpponents(selected.Length, rules.StartingElements[0], selected));
        int[] owners = match.CaptureAiOwners()!;
        var directory = Directory.CreateTempSubdirectory("elemental-ai-difficulty-");
        try
        {
            var store = new FileWorldStore(directory.FullName, GameRulesTests.Rules());
            store.Save(Capture(match, human));
            WorldSave saved = Assert.IsType<WorldSave>(store.Load("ai"));
            Assert.Equal(owners, saved.AiOwners);
            Assert.Equal(selected, saved.AiDifficulties);
            var restored = new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId,
                aiOwners: saved.AiOwners, aiDifficulties: saved.AiDifficulties);
            Assert.Equal(owners, restored.CaptureAiOwners());
            Assert.Equal(selected, restored.CaptureAiDifficulties());
            Assert.Equal(selected, restored.CaptureAiMigration().Select(brain => brain.Difficulty));
            Assert.True(restored.TryResume(human));

            var migrated = new NetworkWorld(rules, settings, restored.World.CaptureMigrationState(),
                restored.NextPlayerId, restored.CaptureAiMigration(), [human], [human]);
            Assert.Equal(owners, migrated.CaptureAiOwners());
            Assert.Equal(selected, migrated.CaptureAiDifficulties());
            Assert.Equal(2, migrated.LivingAiCount);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void LegacySaveWithoutDifficultyFieldRestoresEveryAiAsNormal()
    {
        var (match, rules, settings, human) = CreateRoom();
        Assert.Equal(JoinFailure.None, match.AddAiOpponents(2, rules.StartingElements[0]));
        Assert.Equal(new[] { AiDifficulty.Normal, AiDifficulty.Normal }, match.CaptureAiDifficulties());
        var directory = Directory.CreateTempSubdirectory("elemental-ai-legacy-");
        try
        {
            var store = new FileWorldStore(directory.FullName, GameRulesTests.Rules());
            store.Save(Capture(match, human) with { AiDifficulties = null });
            var json = JObject.Parse(File.ReadAllText(Path.Combine(directory.FullName, "world-ai.json")));
            Assert.Null(json["Save"]!["AiDifficulties"]);
            WorldSave saved = Assert.IsType<WorldSave>(store.Load("ai"));
            Assert.Null(saved.AiDifficulties);
            var restored = new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId, aiOwners: saved.AiOwners);
            Assert.Equal(match.CaptureAiOwners(), restored.CaptureAiOwners());
            Assert.Equal(new[] { AiDifficulty.Normal, AiDifficulty.Normal }, restored.CaptureAiDifficulties());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("length")]
    [InlineData("enum")]
    [InlineData("orphan")]
    [InlineData("orphan-empty")]
    public void InvalidDifficultyMetadataIsRejectedWithoutReplacingThePreviousSave(string corruption)
    {
        var (match, rules, _, human) = CreateRoom();
        Assert.Equal(JoinFailure.None, match.AddAiOpponents(2, rules.StartingElements[0]));
        WorldSave valid = corruption.StartsWith("orphan", StringComparison.Ordinal)
            ? new WorldSave("ai", new SimulationWorld(GameRulesTests.Rules(), new WorldSetup(), 1).CaptureCheckpoint(), new(), 1)
            : Capture(match, human);
        AiDifficulty[] invalid = corruption switch
        {
            "length" => [AiDifficulty.Normal],
            "enum" => [AiDifficulty.Easy, (AiDifficulty)3],
            "orphan" => [AiDifficulty.Normal],
            _ => []
        };
        var directory = Directory.CreateTempSubdirectory("elemental-ai-invalid-");
        try
        {
            var store = new FileWorldStore(directory.FullName, GameRulesTests.Rules());
            store.Save(valid);
            string path = Path.Combine(directory.FullName, "world-ai.json");
            string original = File.ReadAllText(path);
            Assert.Throws<InvalidDataException>(() => store.Save(valid with { AiDifficulties = invalid }));
            Assert.Equal(original, File.ReadAllText(path));
            var json = JObject.Parse(original);
            json["Save"]!["AiDifficulties"] = new JArray(invalid.Select(difficulty => (int)difficulty));
            File.WriteAllText(path, json.ToString());
            Assert.Throws<InvalidDataException>(() => store.Load("ai"));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void AddAiValidatesTheEntireDifficultyListBeforeCreatingAnyOpponent()
    {
        var (match, rules, _, _) = CreateRoom();
        int nextOwner = match.NextPlayerId;
        int players = match.World.PlayerCount;
        Assert.Throws<ArgumentException>(() => match.AddAiOpponents(2, rules.StartingElements[0], [AiDifficulty.Easy]));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.AddAiOpponents(2, rules.StartingElements[0],
            [AiDifficulty.Easy, (AiDifficulty)255]));
        Assert.Equal(nextOwner, match.NextPlayerId);
        Assert.Equal(players, match.World.PlayerCount);
        Assert.Null(match.CaptureAiOwners());
        Assert.Null(match.CaptureAiDifficulties());
    }

    [Fact]
    public void CheckpointRestoreRejectsMismatchedInvalidAndOrphanDifficultyLists()
    {
        var (match, rules, settings, human) = CreateRoom();
        Assert.Equal(JoinFailure.None, match.AddAiOpponents(2, rules.StartingElements[0]));
        WorldSave saved = Capture(match, human);
        Assert.Throws<ArgumentException>(() => new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId,
            aiOwners: saved.AiOwners, aiDifficulties: [AiDifficulty.Normal]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId,
            aiOwners: saved.AiOwners, aiDifficulties: [AiDifficulty.Easy, (AiDifficulty)3]));
        Assert.Throws<ArgumentException>(() => new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId,
            aiDifficulties: []));
    }

    private static (NetworkWorld Match, GameRules Rules, NetworkSettings Settings, int Human) CreateRoom()
    {
        GameDataDocument data = GameRulesTests.Data();
        var rules = new GameRules(data).ForRoom(3);
        var settings = new NetworkSettings(data);
        var match = new NetworkWorld(rules, settings);
        Assert.Equal(JoinFailure.None, match.TryJoin(rules.StartingElements[0], out int human));
        return (match, rules, settings, human);
    }

    private static WorldSave Capture(NetworkWorld match, int human) => new("ai", match.World.CaptureCheckpoint(),
        new() { [Guid.NewGuid().ToString("N")] = human }, match.NextPlayerId, "난이도 저장", 2, 3,
        AiOwners: match.CaptureAiOwners(), AiDifficulties: match.CaptureAiDifficulties());
}
