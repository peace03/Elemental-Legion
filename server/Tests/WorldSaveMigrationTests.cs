using System.Numerics;
using System.Diagnostics;
using System.Reflection;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>명시적인 구형 이관이 상태와 원본 파일을 보존하는지 검사한다.</summary>
public sealed class WorldSaveMigrationTests
{
    private static string Root => FindRoot();
    private static string SourceData => Path.Combine(Root, "server/Tests/Fixtures/main-world-v1-gamedata.json");
    private static string TargetData => Path.Combine(Root, "shared/Data/gamedata.json");

    [Fact]
    public void UpgradePreservesExistingAssetsWithoutRegrantingRewards()
    {
        var rules = GameRulesTests.Rules();
        JObject source = LegacyWorld(rules);
        source["Units"]![0]!["Health"] = rules.Elements[rules.ElementIndex("water")].Hp - 5;
        source["Units"]![0]!["Statuses"]![0] = new JObject { ["Expires"] = 6.25, ["Amount"] = 0.25, ["Dps"] = 0.5,
            ["Owner"] = 1, ["Source"] = 1, ["Element"] = rules.ElementIndex("water"), ["LegionOwner"] = 1 };
        source["Units"]![0]!["LastHitTimes"]![0] = 4.5;
        source["Units"]![0]!["LastHitOwners"]![0] = 1;
        var before = (JObject)source.DeepClone();
        var result = JObject.FromObject(WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), rules));
        Assert.Equal(WorldCheckpoint.CurrentSchema, (int)result["SchemaVersion"]!);
        Assert.Equal(before["Tick"], result["Tick"]);
        Assert.Equal(before["NextId"], result["NextId"]);
        Assert.Equal(before["NextBuildingId"], result["NextBuildingId"]);
        Assert.Equal(before["NextAreaId"], result["NextAreaId"]);
        Assert.True(JToken.DeepEquals(before["Cores"], result["Cores"]));
        Assert.Equal(before["Buildings"]![0]!["Position"], result["Buildings"]![0]!["Position"]);
        foreach (JProperty field in ((JObject)before["Units"]![0]!["Statuses"]![0]!).Properties())
            Assert.Equal(field.Value, result["Units"]![0]!["Statuses"]![0]![field.Name]);
        Assert.Equal(before["Units"]![0]!["LastHitTimes"], result["Units"]![0]!["LastHitTimes"]);
        Assert.Equal(before["Units"]![0]!["LastHitOwners"], result["Units"]![0]!["LastHitOwners"]);
        var player = result["Players"]![0]!;
        Assert.Equal(before["Players"]![0]!["Cores"], player["Cores"]);
        Assert.Equal(before["Players"]![0]!["Explored"], player["Explored"]);
        Assert.Equal(10UL, (ulong)player["NextTrainingOrderId"]!);
        Assert.Equal(7UL, (ulong)player["Training"]![0]!["Id"]!);
        Assert.Equal(9UL, (ulong)player["Training"]![1]!["Id"]!);
        Assert.Equal((long)before["Tick"]!, (long)player["TrainingStartTick"]!);
        Assert.Equal((long)Math.Ceiling(rules.TrainingSeconds(rules.ElementIndex("water"), rules.ElementIndex("water")) * rules.TickRate),
            (long)player["TrainingEndTick"]! - (long)player["TrainingStartTick"]!);
        var oldUnits = (JArray)before["Units"]!;
        var units = (JArray)result["Units"]!;
        Assert.Equal(oldUnits.Count, units.Count);
        for (int i = 0; i < units.Count; i++)
        {
            Assert.Equal(oldUnits[i]["Position"], units[i]["Position"]);
            Assert.Equal(oldUnits[i]["Health"], units[i]["Health"]);
            Assert.False((bool)units[i]["FreeOrigin"]!);
            Assert.False((bool)units[i]["RescuedOnce"]!);
        }
        var guards = units.Where(unit => (bool)unit["Garrison"]!).ToArray();
        Assert.Equal(rules.GarrisonCap(1), guards.Length);
        Assert.Equal(oldUnits.Take(guards.Length).Select(unit => (int)unit["Id"]!), guards.Select(unit => (int)unit["Id"]!));
        Assert.All(units.Skip(guards.Length), unit =>
        {
            Assert.Equal(0, (int)unit["SquadId"]!);
            Assert.Equal(1, (int)unit["DestinationSquadId"]!);
            Assert.True((bool)unit["IsReinforcing"]!);
        });
        var checkpoint = result.ToObject<WorldCheckpoint>()!;
        var restored = SimulationWorld.RestoreCheckpoint(rules, checkpoint);
        Assert.Equal(2, restored.GetView(1).ReservedPopulation);
        JObject paused = JObject.FromObject(restored.CaptureCheckpoint());
        Assert.Equal((long)before["Tick"]!, (long)paused["Players"]![0]!["TrainingPausedAtTick"]!);
        restored.Step();
        Assert.Equal(paused["Players"]![0]!["TrainingEndTick"], JObject.FromObject(restored.CaptureCheckpoint())["Players"]![0]!["TrainingEndTick"]);
        Assert.True(restored.TryResumeGhost(1));
        Assert.Equal(2, restored.GetView(1).ReservedPopulation);
        JObject resumed = JObject.FromObject(restored.CaptureCheckpoint());
        Assert.Equal((long)player["TrainingEndTick"]! - (long)player["TrainingStartTick"]!,
            (long)resumed["Players"]![0]!["TrainingEndTick"]! - (long)resumed["Tick"]!);
        Assert.Equal((long)before["Players"]![0]!["Cores"]!, restored.GetView(1).CoreBalance);
        Assert.Equal(CommandAdmission.Accepted, restored.TryEnqueue(1, SimCommand.Move(1, Vector2.Zero)));
    }

    [Theory]
    [InlineData("0fede127a2cb748364987ba4f524dd291cf443c76a4040bcae2c08f2b503a934")]
    [InlineData("86f70df9af522e56337634f3fb9599f73903a1f86f4f51b7a53ca4babca26a6c")]
    public void KnownUnsupportedSaveHashesAreRejectedWithoutChangingSource(string hash)
    {
        JObject json = LegacyWorld(GameRulesTests.Rules());
        json["DataHash"] = hash;
        string before = json.ToString();
        Assert.Throws<ArgumentException>(() => WorldCheckpointUpgrade.Upgrade(before, SourceRules(), GameRulesTests.Rules()));
        Assert.Equal(before, json.ToString());
    }

    [Fact]
    public void SourceRulesAndDuplicateOrMissingFieldsAreRejected()
    {
        string data = File.ReadAllText(SourceData).Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(WorldCheckpointUpgrade.SupportedSourceHash, new WorldCheckpointUpgrade.LegacyRules(data.Replace("\n", "\r\n")).DataHash);
        Assert.Throws<ArgumentException>(() => new WorldCheckpointUpgrade.LegacyRules(File.ReadAllText(SourceData) + " "));
        JObject source = LegacyWorld(GameRulesTests.Rules());
        Assert.ThrowsAny<JsonException>(() => WorldCheckpointUpgrade.Upgrade(source.ToString().Replace("\"Tick\": 100", "\"Tick\": 100, \"Tick\": 100"), SourceRules(), GameRulesTests.Rules()));
        source["Units"]![0]!["Statuses"]![0]!["Expires"]!.Parent!.Remove();
        Assert.ThrowsAny<JsonException>(() => WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), GameRulesTests.Rules()));
        source = LegacyWorld(GameRulesTests.Rules());
        source["Units"]![0]!["Position"]!["X"]!.Parent!.Remove();
        Assert.ThrowsAny<JsonException>(() => WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), GameRulesTests.Rules()));
    }

    [Theory]
    [InlineData("Players")]
    [InlineData("Players[0].Training")]
    [InlineData("Players[0].Nickname")]
    [InlineData("Units[0].Position")]
    public void NoncanonicalFieldCasingIsRejectedBeforeMapping(string path)
    {
        JObject source = LegacyWorld(GameRulesTests.Rules());
        LowerFieldName(source, path);
        string before = source.ToString();
        Assert.ThrowsAny<JsonException>(() => WorldCheckpointUpgrade.Upgrade(before, SourceRules(), GameRulesTests.Rules()));
        Assert.Equal(before, source.ToString());
    }

    [Fact]
    public void CanonicalKeyValidationPreservesOptionalLegacyDefaultsAndExactCoordinates()
    {
        var rules = GameRulesTests.Rules();
        JObject source = LegacyWorld(rules);
        source.Remove("RoomPlayers");
        ((JObject)source["Players"]![0]!).Remove("ColorId");
        var position = new JObject { ["X"] = 202.125, ["Y"] = 220.375 };
        source["Units"]![0]!["Position"] = position.DeepClone();
        JObject result = JObject.FromObject(WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), rules));
        Assert.True(JToken.DeepEquals(position, result["Units"]![0]!["Position"]));
        Assert.Equal(source["Players"]![0]!["Explored"], result["Players"]![0]!["Explored"]);
        Assert.Equal(source["Players"]![0]!["Cores"], result["Players"]![0]!["Cores"]);
        Assert.Equal(0, (int)result["RoomPlayers"]!);
    }

    [Theory]
    [InlineData("Players")]
    [InlineData("Players[0].Training")]
    public void ActualCliRejectsNoncanonicalFieldCasingAndPreservesPrimaryAndBackup(string path)
    {
        using var directory = new TestDirectory();
        string source = directory.Child("source"), destination = directory.PathFor("result");
        JObject save = LegacySave();
        string backupText = save.ToString();
        LowerFieldName((JObject)save["Save"]!["World"]!, path);
        string primary = Path.Combine(source, "world-test.json"), backup = primary + ".bak";
        File.WriteAllText(primary, save.ToString()); File.WriteAllText(backup, backupText);
        byte[] primaryBytes = File.ReadAllBytes(primary), backupBytes = File.ReadAllBytes(backup);
        string tests = typeof(WorldSaveMigrationTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(tests, ".runtimeconfig.json"), "--depsfile", Path.ChangeExtension(tests, ".deps.json"),
            typeof(ServerOptions).Assembly.Location, "--migrate-world", source, "--migration-output", destination, "--source-data", SourceData, "--target-data", TargetData })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("이관 CLI가 종료하지 않았습니다."); }
        string error = process.StandardError.ReadToEnd();
        Assert.Equal(2, process.ExitCode);
        Assert.DoesNotContain("Unhandled exception", error);
        Assert.DoesNotContain("NullReferenceException", error);
        Assert.False(Directory.Exists(destination));
        Assert.Equal(primaryBytes, File.ReadAllBytes(primary));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
        Assert.Empty(Directory.GetDirectories(directory.FullName, ".world-migration-*"));
    }

    [Fact]
    public void SourceValidationUsesOldHealthPopulationAndPaidCostBeforeMapping()
    {
        var rules = GameRulesTests.Rules();
        foreach (string field in new[] { "health", "cost", "order", "next", "population" })
        {
            JObject source = LegacyWorld(rules);
            if (field == "health") source["Units"]![0]!["Health"] = 999999;
            else if (field == "cost") source["Players"]![0]!["Training"]![0]!["Cost"] = 0;
            else if (field == "order") source["Players"]![0]!["Training"]![1]!["Id"] = 7;
            else if (field == "population")
            {
                var units = (JArray)source["Units"]!;
                while (units.Count < 19) { JToken added = units[0].DeepClone(); added["Id"] = units.Count + 1; units.Add(added); }
                source["NextId"] = 20;
            }
            else source["NextId"] = 1;
            Assert.Throws<ArgumentException>(() => WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), rules));
        }
    }

    [Fact]
    public void ExistingGhostExpiryIsKeptOnDiskRestart()
    {
        var rules = GameRulesTests.Rules();
        JObject source = LegacyWorld(rules);
        source["Players"]![0]!["Training"] = new JArray();
        source["Players"]![0]!["GhostEndTick"] = 500;
        var result = WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), rules);
        JObject checkpoint = JObject.FromObject(result);
        Assert.Equal(500, (long)checkpoint["Players"]![0]!["GhostEndTick"]!);
        var restored = SimulationWorld.RestoreCheckpoint(rules, result);
        Assert.Equal(500, (long)JObject.FromObject(restored.CaptureCheckpoint())["Players"]![0]!["GhostEndTick"]!);
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(11L, 3)]
    [InlineData(long.MaxValue, int.MaxValue)]
    public void UpgradePreservesKnownJourneyHistoryWithoutInventingCoreTransactions(long converted, int wins)
    {
        var rules = GameRulesTests.Rules();
        JObject source = LegacyWorld(rules);
        JToken player = source["Players"]![0]!;
        long trained = converted == long.MaxValue ? 0 : 17;
        player["Converted"] = converted; player["BattleWins"] = wins; player["Trained"] = trained;
        var checkpoint = WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), rules);
        JObject result = JObject.FromObject(checkpoint);
        JToken upgraded = result["Players"]![0]!;
        var total = (JObject)upgraded["JourneyTotal"]!;
        Assert.Equal(7, total.Count);
        Assert.Equal(converted, (long)total["Converted"]!);
        Assert.Equal(wins, (long)total["SiegeWins"]!);
        foreach (string field in new[] { "Rescued", "Lost", "CoresIn", "CoresSpent", "BuildingsDestroyed" })
            Assert.Equal(0, (long)total[field]!);
        Assert.True(JToken.DeepEquals(total, upgraded["JourneyAcknowledged"]));
        Assert.Equal(trained, (long)upgraded["Trained"]!);
        Assert.Equal(player["Cores"], upgraded["Cores"]);
        var restored = SimulationWorld.RestoreCheckpoint(rules, checkpoint);
        JToken savedAgain = JObject.FromObject(restored.CaptureCheckpoint())["Players"]![0]!;
        Assert.True(JToken.DeepEquals(total, savedAgain["JourneyTotal"]));
        Assert.True(JToken.DeepEquals(total, savedAgain["JourneyAcknowledged"]));
        Assert.Equal((long)player["Cores"]!, restored.GetView(1).CoreBalance);
    }

    [Theory]
    [InlineData(long.MaxValue, 1L)]
    [InlineData(1L, long.MaxValue)]
    public void SourceAcquisitionCounterOverflowIsRejectedWithoutChangingValues(long converted, long trained)
    {
        JObject source = LegacyWorld(GameRulesTests.Rules());
        source["Players"]![0]!["Converted"] = converted;
        source["Players"]![0]!["Trained"] = trained;
        string before = source.ToString();
        var failure = Assert.Throws<ArgumentException>(() => WorldCheckpointUpgrade.Upgrade(before, SourceRules(), GameRulesTests.Rules()));
        Assert.Contains("원본 획득 합계", failure.Message);
        Assert.Equal(before, source.ToString());
    }

    [Fact]
    public void PaidOrdersReserveRemainingGuardCapacityThenUseSquadOneWithoutRefund()
    {
        var rules = GameRulesTests.Rules();
        JObject source = LegacyWorld(rules);
        int remainingGuard = rules.GarrisonCap(1) - 1;
        var units = (JArray)source["Units"]!;
        for (int i = 0; i < units.Count; i++) units[i]["Garrison"] = i < remainingGuard;
        var result = JObject.FromObject(WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), rules));
        Assert.Equal(4, (int)result["Players"]![0]!["Training"]![0]!["TargetSquad"]!);
        Assert.Equal(1, (int)result["Players"]![0]!["Training"]![1]!["TargetSquad"]!);
        Assert.Equal(source["Players"]![0]!["Cores"], result["Players"]![0]!["Cores"]);
        Assert.Equal(source["Players"]![0]!["Training"]![0]!["Id"], result["Players"]![0]!["Training"]![0]!["Id"]);
        Assert.Equal(source["Players"]![0]!["Training"]![1]!["Id"], result["Players"]![0]!["Training"]![1]!["Id"]);
        Assert.Equal(1, (int)result["Units"]![remainingGuard]!["SquadId"]!);
    }

    [Fact]
    public void TargetReactionIdentityChangesAreRejectedBeforeReassigningSavedIndices()
    {
        var rules = GameRulesTests.Rules();
        JObject source = LegacyWorld(rules);
        JObject data = GameRulesTests.Data().Root;
        var reactions = (JArray)data["reactions"]!;
        JToken first = reactions[0].DeepClone();
        reactions[0] = reactions[1].DeepClone(); reactions[1] = first;
        var reordered = new GameRules(new GameDataDocument(data.ToString()));
        Assert.Throws<ArgumentException>(() => WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), reordered));
    }

    [Fact]
    public void ExistingBuildingCoordinatesAreNeverMovedToMakeRecallFit()
    {
        var rules = GameRulesTests.Rules();
        JObject source = LegacyWorld(rules);
        source["Players"]![0]!["BasePosition"] = new JObject { ["X"] = 0, ["Y"] = 0 };
        source["Buildings"]![0]!["Position"] = source["Players"]![0]!["BasePosition"]!.DeepClone();
        // 원본에서는 합법인 모서리 배치가 목표 귀환 슬롯을 수용하지 못하면 이관을 중단한다.
        var targetData = GameRulesTests.Data().Root;
        targetData["construction"]!["buildRadius"] = 7;
        var narrow = new GameRules(new GameDataDocument(targetData.ToString()));
        string before = source.ToString();
        Assert.Throws<ArgumentException>(() => WorldCheckpointUpgrade.Upgrade(before, SourceRules(), narrow));
        Assert.Equal(before, source.ToString());
    }

    [Fact]
    public void ReparsePathsAreRejectedAndLinkTargetsRemainUnchanged()
    {
        using var directory = new TestDirectory();
        string source = directory.Child("source"), linked = directory.PathFor("linked"), destination = directory.PathFor("result");
        File.WriteAllText(Path.Combine(source, "world-test.json"), LegacySave().ToString());
        if (OperatingSystem.IsWindows())
        {
            // Windows의 일반 사용자도 만들 수 있는 junction으로 실제 재분석 지점을 검사한다.
            var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "/d", "/c", "mklink", "/J", linked, source }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(linked, source);
        try
        {
            Assert.Throws<IOException>(() => WorldSaveMigration.Run(linked, destination, SourceData, TargetData));
            Assert.False(Directory.Exists(destination));
            Assert.True(File.Exists(Path.Combine(source, "world-test.json")));
        }
        finally { Directory.Delete(linked); }
    }

    [Fact]
    public void EcologyMapsCreditByActualSiteAndNeverRegrantsOpeningBundle()
    {
        var rules = GameRulesTests.Rules();
        var simulation = new SimulationWorld(rules, new WorldSetup(), 41);
        simulation.EnableEcology(300, legacyPlacement: true);
        Assert.True(simulation.TryChooseSpawn(out Vector2 origin, out Vector2 legion));
        Assert.True(simulation.TryAddPlayer(new PlayerSpawn(1, "water", origin, legion)));
        JObject source = ToLegacy(JObject.FromObject(simulation.CaptureCheckpoint()));
        var ecology = (JObject)source["Ecology"]!;
        var schedules = new JArray();
        for (int i = 0; i < rules.MaxPlayers; i++) schedules.Add(new JObject { ["Owner"] = 0, ["Credit"] = 0, ["GlobalNext"] = false });
        source["Players"]![0]!["Slot"] = 7;
        schedules[7]!["Owner"] = 1; schedules[7]!["Credit"] = 1160; schedules[7]!["GlobalNext"] = true;
        ecology["Schedules"] = schedules;
        foreach (JObject group in ecology["Groups"]!)
            foreach (JObject slot in group["Slots"]!) slot.Remove("Reserved");
        JObject before = (JObject)source.DeepClone();
        JObject result = JObject.FromObject(WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), rules));
        Assert.Equal(before["Ecology"]!["Sites"], result["Ecology"]!["Sites"]);
        Assert.Equal(before["Units"], RemoveNewUnitFields((JArray)result["Units"]!));
        Assert.All(result["Ecology"]!["Schedules"]!, schedule => Assert.True((bool)schedule["FirstGranted"]!));
        int site = ((JArray)ecology["Sites"]!).Select((value, index) => (value, index)).Single(pair => JToken.DeepEquals(pair.value, source["Players"]![0]!["BasePosition"])).index;
        Assert.Equal(1160, (long)result["Ecology"]!["Schedules"]![site]!["Credit"]!);
        Assert.Equal(2, (int)result["Ecology"]!["Schedules"]![site]!["Phase"]!);
        Assert.NotEqual(7, site);
        ((JObject)schedules[0]!)["Owner"] = 999;
        ((JObject)schedules[0]!)["Credit"] = 20;
        Assert.Throws<ArgumentException>(() => WorldCheckpointUpgrade.Upgrade(source.ToString(), SourceRules(), rules));
    }

    [Theory]
    [InlineData("--migrate-world", "source")]
    [InlineData("--migration-output", "destination")]
    [InlineData("--source-data", "old.json")]
    [InlineData("--target-data", "new.json")]
    public void CliRequiresAllFourMigrationOptions(string option, string value)
        => Assert.Throws<ArgumentException>(() => ServerOptions.Parse([option, value], 15));

    [Fact]
    public void CliMigrationModeRejectsServerModesAndDuplicates()
    {
        string[] arguments = ["--migrate-world", "source", "--migration-output", "destination", "--source-data", "old.json", "--target-data", "new.json"];
        Assert.Equal("source", ServerOptions.Parse(arguments, 15).MigrationSource);
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse([.. arguments, "--listen"], 15));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse([.. arguments, "--source-data", "other.json"], 15));
    }

    [Fact]
    public void CliReturnsFailureCodeForUnsupportedSaveWithoutUnhandledException()
    {
        using var directory = new TestDirectory();
        string source = directory.Child("source"), destination = directory.PathFor("result");
        JObject save = LegacySave(); save["DataHash"] = new string('0', 64);
        File.WriteAllText(Path.Combine(source, "world-test.json"), save.ToString());
        var main = typeof(ServerOptions).Assembly.GetType("ElementalLegion.Server.Program")!.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
        string[] arguments = ["--migrate-world", source, "--migration-output", destination, "--source-data", SourceData, "--target-data", TargetData];
        Assert.Equal(2, (int)main.Invoke(null, [arguments])!);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void MigrationPublishesValidatedCopyAndPreservesNormalBackup()
    {
        using var directory = new TestDirectory();
        string source = directory.Child("source"), destination = directory.PathFor("result");
        JObject wrapper = LegacySave();
        string primary = Path.Combine(source, "world-test.json"), backup = primary + ".bak";
        File.WriteAllText(primary, wrapper.ToString());
        File.WriteAllText(backup, wrapper.ToString());
        byte[] oldPrimary = File.ReadAllBytes(primary), oldBackup = File.ReadAllBytes(backup);
        Assert.Equal(0, WorldSaveMigration.Run(source, destination, SourceData, TargetData));
        Assert.NotNull(new FileWorldStore(destination, GameRulesTests.Rules()).Load("test"));
        var migrated = new FileWorldStore(destination, GameRulesTests.Rules()).Load("test")!;
        Assert.Equal(1, migrated.Players["00112233445566778899aabbccddeeff"]);
        Assert.Equal(2, migrated.NextPlayerId);
        Assert.Equal(oldPrimary, File.ReadAllBytes(primary));
        Assert.Equal(oldBackup, File.ReadAllBytes(backup));
        string report = File.ReadAllText(Path.Combine(destination, "migration-report.json"));
        Assert.DoesNotContain("00112233445566778899aabbccddeeff", report);
        Assert.True(File.Exists(Path.Combine(destination, "world-test.json.bak")));
    }

    [Fact]
    public void CurrentSchemaMigrationChangesOnlyHashesAndKeepsPausedOrdersAndBackup()
    {
        using var directory = new TestDirectory();
        var input = BeforeQuickTransfer(directory);
        string destination = directory.PathFor("result"), backup = input.Primary + ".bak";
        JObject expected = JObject.Parse(File.ReadAllText(input.Primary)), expectedBackup = (JObject)expected.DeepClone();
        expectedBackup["Save"]!["World"]!["Players"]![0]!["Cores"] = 104;
        File.WriteAllText(backup, expectedBackup.ToString());
        byte[] source = File.ReadAllBytes(input.Primary), sourceBackup = File.ReadAllBytes(backup);
        Assert.Throws<InvalidDataException>(() => new FileWorldStore(input.Folder, input.Rules).Load("test"));

        Assert.Equal(0, WorldSaveMigration.Run(input.Folder, destination, input.SourceData, input.TargetData));

        string output = Path.Combine(destination, "world-test.json");
        foreach (var (path, contents) in new[] { (output, expected), (output + ".bak", expectedBackup) })
        {
            contents["DataHash"] = input.Rules.DataHash;
            contents["Save"]!["World"]!["DataHash"] = input.Rules.DataHash;
            Assert.Equal(contents.ToString(Formatting.None), JObject.Parse(File.ReadAllText(path)).ToString(Formatting.None));
        }
        WorldSave saved = Assert.IsType<WorldSave>(new FileWorldStore(destination, input.Rules).Load("test"));
        var restored = SimulationWorld.RestoreCheckpoint(input.Rules, saved.World);
        Assert.True(restored.TryResumeGhost(1));
        Assert.Equal(3, restored.GetView(1).ReservedPopulation);
        Assert.Equal(105, restored.GetView(1).CoreBalance);
        Assert.Equal(source, File.ReadAllBytes(input.Primary));
        Assert.Equal(sourceBackup, File.ReadAllBytes(backup));
    }

    [Fact]
    public void InstantConversionMigrationPreservesAnExistingCandidatesProgress()
    {
        using var directory = new TestDirectory();
        GameRules target = GameRulesTests.Rules();
        JObject previousData = GameRulesTests.Data().Root;
        previousData.SelectToken("economy.pvpRecovery.instant")!.Parent!.Remove();
        var previous = new GameRules(new GameDataDocument(previousData.ToString()));
        Assert.False(previous.PvpConversionInstant);
        Assert.True(target.PvpConversionInstant);
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(300), new Vector2(100), 4));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720), 4));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(101, 100), health: 1));
        var world = new SimulationWorld(previous, setup, 1);
        world.Step();
        for (int tick = 0; tick < 19; tick++) world.Step();
        var candidate = Assert.Single(world.GetView(1).Candidates);
        Assert.Equal(19, candidate.ProgressTicks);
        string source = directory.Child("before"), output = directory.PathFor("after");
        string oldData = directory.PathFor("before.json");
        File.WriteAllText(oldData, previousData.ToString());
        new FileWorldStore(source, previous).Save(new WorldSave("test", world.CaptureCheckpoint(),
            new() { ["00112233445566778899aabbccddeeff"] = 1, ["11223344556677889900aabbccddeeff"] = 2 }, 3));

        Assert.Equal(0, WorldSaveMigration.Run(source, output, oldData, TargetData));

        var restored = SimulationWorld.RestoreCheckpoint(target, new FileWorldStore(output, target).Load("test")!.World);
        Assert.True(restored.TryResumeGhost(1));
        Assert.Equal(19, Assert.Single(restored.GetView(1).Candidates).ProgressTicks);
        Assert.Equal(1, restored.GetView(1).ReservedPopulation);
        for (int tick = 19; tick < target.PvpRecoverySecureTicks - 1; tick++) restored.Step();
        Assert.True(restored.HasConversionCandidates);
        restored.Step();
        Assert.False(restored.HasConversionCandidates);
        Assert.Equal(1, CombatTests.Find(restored, candidate.Id).OwnerId);
        Assert.Equal(0, restored.GetView(1).ReservedPopulation);
    }

    [Fact]
    public void ExplorationSettingsMigrationPreservesNeutralSitesAndExistingCoreTimers()
    {
        using var directory = new TestDirectory();
        GameRules target = GameRulesTests.Rules();
        JObject previousData = GameRulesTests.Data().Root;
        previousData["neutral"]!["walkSpacingSeconds"] = 60;
        previousData.SelectToken("economy.opening.explorationBundlesPerMinute")!.Parent!.Remove();
        previousData.SelectToken("economy.opening.explorationCoreLifetimeSeconds")!.Parent!.Remove();
        var previous = new GameRules(new GameDataDocument(previousData.ToString()));
        GameRules room = previous.ForRoom(2);
        var world = OpeningRouteTests.Fresh(room, 41);
        Assert.True(world.TryChooseSpawn(out Vector2 home, out Vector2 start));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", home, start)));
        world.Step();
        string source = directory.Child("before"), output = directory.PathFor("after");
        string oldData = directory.PathFor("before.json");
        File.WriteAllText(oldData, previousData.ToString());
        new FileWorldStore(source, previous).Save(new WorldSave("test", world.CaptureCheckpoint(),
            new() { ["00112233445566778899aabbccddeeff"] = 1 }, 2, "탐색 전 월드", 1, 2));
        string original = Path.Combine(source, "world-test.json");
        byte[] unchanged = File.ReadAllBytes(original);

        Assert.Equal(0, WorldSaveMigration.Run(source, output, oldData, TargetData));
        var restored = SimulationWorld.RestoreCheckpoint(target, new FileWorldStore(output, target).Load("test")!.World);
        JObject before = OpeningRouteTests.Snapshot(world);
        JObject after = OpeningRouteTests.Snapshot(restored);
        Assert.True(JToken.DeepEquals(before["Ecology"], after["Ecology"]));
        Assert.True(JToken.DeepEquals(before["Cores"], after["Cores"]));
        Assert.Equal(unchanged, File.ReadAllBytes(original));
    }

    [Theory]
    [InlineData(2, 240)]
    [InlineData(15, 657)]
    public void FacilityMigrationPreservesLegacyRoomGeometryAndStartsCollectorSchedule(int players, float mapSize)
    {
        using var directory = new TestDirectory();
        GameDataDocument data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var oldData = (JObject)data.Root.DeepClone();
        foreach (string path in new[] { "neutral.walkSpacingSeconds", "buildings.collector.production", "facilityUpgrades" })
            oldData.SelectToken(path)!.Parent!.Remove();
        oldData["population"]!["max"] = 100;
        oldData["world"]!["roomMapSizeAtTwoPlayers"] = 240;
        var levels = (JArray)oldData["baseLevels"]!;
        while (levels.Count > 4) levels.RemoveAt(levels.Count - 1);
        var originalRules = new GameDataDocument(oldData.ToString());
        string originalData = directory.PathFor("old-data.json");
        File.WriteAllText(originalData, oldData.ToString());
        string folder = directory.Child("source"), output = directory.PathFor("result");
        var roomRules = rules.ForRoom(players, mapSize);
        var world = new SimulationWorld(roomRules, new WorldSetup(), seed: 41);
        world.EnableEcology(roomRules.RoomMinBaseDistance, legacyPlacement: true);
        Assert.True(world.TryChooseSpawn(out Vector2 home, out Vector2 legion));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", home, legion, 4, 100, natureForge: true)));
        BuildingState collector = AddTestCollector(world, home + new Vector2(0, -18));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1)));
        world.Step();
        var store = new FileWorldStore(folder, rules);
        store.Save(new WorldSave("test", world.CaptureCheckpoint(), new() { ["00112233445566778899aabbccddeeff"] = 1 }, 2,
            "이전 방", 1, players));
        string pathToSave = Path.Combine(folder, "world-test.json");
        JObject wrapper = JObject.Parse(File.ReadAllText(pathToSave));
        var original = (JObject)wrapper["Save"]!["World"]!;
        RemoveFacilityFields(original);
        original["SchemaVersion"] = 3;
        original["DataHash"] = wrapper["DataHash"] = originalRules.Sha256;
        File.WriteAllText(pathToSave, wrapper.ToString());
        byte[] input = File.ReadAllBytes(pathToSave);

        Assert.Equal(0, WorldSaveMigration.Run(folder, output, originalData, TargetData));

        var save = new FileWorldStore(output, rules).Load("test")!;
        Assert.Equal(mapSize, save.World.MapSize);
        JObject migrated = JObject.Parse(JsonConvert.SerializeObject(save.World));
        Assert.True(JToken.DeepEquals(original["Ecology"], migrated["Ecology"]));
        Assert.True(JToken.DeepEquals(original["Players"]![0]!["Explored"], migrated["Players"]![0]!["Explored"]));
        var network = new NetworkWorld(rules, new NetworkSettings(data), save.World, save.NextPlayerId);
        Assert.Equal(mapSize, network.Rules.MapSize);
        Assert.True(network.TryResume(1));
        WorldView view = network.World.GetView(1);
        Assert.Equal(4, view.BaseLevel);
        Assert.Equal(100, view.PopulationCap);
        Assert.Equal(world.GetView(1).CoreBalance, view.CoreBalance);
        Assert.Equal(1, view.ReservedPopulation);
        BuildingView producer = Assert.Single(view.Buildings, building => building.Id == collector.Id);
        Assert.Equal(0, producer.UpgradeRank);
        Assert.Equal(view.Tick + rules.CollectorIntervalTicks, producer.NextProductionTick);
        Assert.Equal(input, File.ReadAllBytes(pathToSave));

        oldData.SelectToken("legion.squads.quickTransferCount")!.Replace(4);
        var differentRules = new GameDataDocument(oldData.ToString());
        File.WriteAllText(originalData, oldData.ToString());
        original["DataHash"] = wrapper["DataHash"] = differentRules.Sha256;
        File.WriteAllText(pathToSave, wrapper.ToString());
        Assert.Throws<InvalidDataException>(() => WorldSaveMigration.Run(folder, directory.PathFor("different"), originalData, TargetData));
        Assert.False(Directory.Exists(directory.PathFor("different")));
    }

    private static BuildingState AddTestCollector(SimulationWorld world, Vector2 position)
    {
        var state = (WorldState)typeof(SimulationWorld).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        state.AddBuilding(state.PlayerById[1], BuildingKind.Collector, position);
        return state.Buildings[^1];
    }

    [Theory]
    [InlineData("envelope")]
    [InlineData("checkpoint")]
    [InlineData("schema")]
    [InlineData("invalidState")]
    [InlineData("balance")]
    [InlineData("tinyBalance")]
    public void CurrentSchemaMigrationRejectsIncompatibleInputWithoutPublishing(string changed)
    {
        using var directory = new TestDirectory();
        var input = BeforeQuickTransfer(directory);
        string destination = directory.PathFor("result");
        if (changed is "balance" or "tinyBalance")
        {
            JObject target = JObject.Parse(File.ReadAllText(input.TargetData));
            var economy = (JObject)target["economy"]!;
            double chance = economy["conversionChance"]!.Value<double>();
            // Newtonsoft는 값 교체 시 근소한 실수 차이를 무시할 수 있다.
            economy.Remove("conversionChance");
            economy.Add("conversionChance", chance + (changed == "balance" ? 0.1 : 1e-15));
            File.WriteAllText(input.TargetData, target.ToString());
        }
        else
        {
            JObject wrapper = JObject.Parse(File.ReadAllText(input.Primary));
            JToken world = wrapper["Save"]!["World"]!;
            switch (changed)
            {
                case "envelope": wrapper["DataHash"] = new string('0', 64); break;
                case "checkpoint": world["DataHash"] = new string('0', 64); break;
                case "schema": world["SchemaVersion"] = 2; break;
                case "invalidState": world["Units"]![0]!["Health"] = -1; break;
            }
            File.WriteAllText(input.Primary, wrapper.ToString());
        }
        byte[] source = File.ReadAllBytes(input.Primary);

        Assert.Throws<InvalidDataException>(() => WorldSaveMigration.Run(input.Folder, destination, input.SourceData, input.TargetData));

        Assert.False(Directory.Exists(destination));
        Assert.Equal(source, File.ReadAllBytes(input.Primary));
        Assert.Empty(Directory.GetDirectories(directory.FullName, ".world-migration-*"));
    }

    [Fact]
    public void BackupOnlySourceRemainsLoadableWithoutCreatingOrChangingSourcePrimary()
    {
        using var directory = new TestDirectory();
        string source = directory.Child("source"), destination = directory.PathFor("result");
        string backup = Path.Combine(source, "world-test.json.bak");
        File.WriteAllText(backup, LegacySave().ToString());
        byte[] bytes = File.ReadAllBytes(backup);
        Assert.Equal(0, WorldSaveMigration.Run(source, destination, SourceData, TargetData));
        Assert.NotNull(new FileWorldStore(destination, GameRulesTests.Rules()).Load("test"));
        Assert.False(File.Exists(Path.Combine(source, "world-test.json")));
        Assert.Equal(bytes, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("nested")]
    [InlineData("parent")]
    [InlineData("exists")]
    public void ConflictingPathsNeverModifySourceOrExistingOutput(string mode)
    {
        using var directory = new TestDirectory();
        string source = directory.Child("source");
        string primary = Path.Combine(source, "world-test.json");
        File.WriteAllText(primary, LegacySave().ToString());
        string destination = mode switch { "same" => source, "nested" => Path.Combine(source, "new"), "parent" => directory.FullName, _ => directory.Child("exists") };
        string? sentinel = mode == "exists" ? Path.Combine(destination, "sentinel.txt") : null;
        if (sentinel != null) File.WriteAllText(sentinel, "keep");
        byte[] bytes = File.ReadAllBytes(primary);
        Assert.ThrowsAny<Exception>(() => WorldSaveMigration.Run(source, destination, SourceData, TargetData));
        Assert.Equal(bytes, File.ReadAllBytes(primary));
        if (sentinel != null) Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void FileSystemRootsAreRejectedBeforeReadingWorlds()
    {
        using var directory = new TestDirectory();
        string root = Path.GetPathRoot(directory.FullName)!;
        Assert.Throws<ArgumentException>(() => WorldSaveMigration.Run(root, directory.PathFor("result"), SourceData, TargetData));
        Assert.Throws<ArgumentException>(() => WorldSaveMigration.Run(directory.Child("source"), root, SourceData, TargetData));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    [InlineData("hash")]
    [InlineData("format")]
    [InlineData("counterOverflow")]
    public void InvalidInputsLeaveSourceAndBackupAndNoDestination(string mode)
    {
        using var directory = new TestDirectory();
        string source = directory.Child("source"), destination = directory.PathFor("result");
        string primary = Path.Combine(source, "world-test.json"), backup = primary + ".bak";
        string json = LegacySave().ToString();
        File.WriteAllText(backup, json);
        if (mode == "duplicate") File.WriteAllText(primary, json.Replace("\"Format\": 1", "\"Format\": 1, \"Format\": 1"));
        else if (mode == "hash") File.WriteAllText(primary, json.Replace(WorldCheckpointUpgrade.SupportedSourceHash, new string('0', 64)));
        else if (mode == "format") File.WriteAllText(primary, json.Replace("\"Format\": 1", "\"Format\": \"1\""));
        else if (mode == "counterOverflow")
        {
            JObject invalid = JObject.Parse(json);
            invalid["Save"]!["World"]!["Players"]![0]!["Converted"] = long.MaxValue;
            invalid["Save"]!["World"]!["Players"]![0]!["Trained"] = 1L;
            File.WriteAllText(primary, invalid.ToString());
        }
        else { using var stream = File.Create(primary); stream.SetLength(32L * 1024 * 1024 + 1); }
        byte[] bytes = File.ReadAllBytes(primary), backupBytes = File.ReadAllBytes(backup);
        Assert.ThrowsAny<Exception>(() => WorldSaveMigration.Run(source, destination, SourceData, TargetData));
        Assert.False(Directory.Exists(destination));
        Assert.Equal(bytes, File.ReadAllBytes(primary));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
        Assert.Empty(Directory.GetDirectories(directory.FullName, ".world-migration-*"));
    }

    private static WorldCheckpointUpgrade.LegacyRules SourceRules() => new(File.ReadAllText(SourceData));

    private static (string Folder, string Primary, string SourceData, string TargetData, GameRules Rules) BeforeQuickTransfer(TestDirectory directory)
    {
        string targetJson = File.ReadAllText(TargetData);
        var targetData = new GameDataDocument(targetJson);
        var rules = new GameRules(targetData);
        var source = (JObject)targetData.Root.DeepClone();
        Assert.True(((JObject)source["legion"]!["squads"]!).Remove("quickTransferCount"));
        source.SelectToken("economy.pvpRecovery.instant")!.Parent!.Remove();
        string sourceData = directory.PathFor("source.json"), targetPath = directory.PathFor("target.json");
        File.WriteAllText(sourceData, source.ToString());
        File.WriteAllText(targetPath, targetJson);
        string sourceHash = new GameDataDocument(source.ToString()).Sha256;

        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 220), cores: 129, natureForge: true));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(202, 220), health: 10));
        var world = new SimulationWorld(rules, setup, 7);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(1, "water", 2, 2)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(2, "water", 1, 4)));
        world.Step();
        world = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        string folder = directory.Child("source");
        var store = new FileWorldStore(folder, rules);
        store.Save(new WorldSave("test", world.CaptureCheckpoint(), new() { ["00112233445566778899aabbccddeeff"] = 1 }, 2));
        string primary = Path.Combine(folder, "world-test.json");
        JObject wrapper = JObject.Parse(File.ReadAllText(primary));
        wrapper["DataHash"] = sourceHash;
        wrapper["Save"]!["World"]!["DataHash"] = sourceHash;
        File.WriteAllText(primary, wrapper.ToString());
        return (folder, primary, sourceData, targetPath, rules);
    }

    private static void LowerFieldName(JObject source, string path)
    {
        var property = (JProperty)source.SelectToken(path)!.Parent!;
        property.Replace(new JProperty(property.Name.ToLowerInvariant(), property.Value.DeepClone()));
    }

    // 동결 샘플의 해시는 원본 데이터와 다르므로 성공 사례는 명시적으로 만든 v1 스키마 fixture를 쓴다.
    private static JObject LegacyWorld(GameRules rules)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 220), cores: 123, natureForge: true));
        setup.Cores.Add(new CoreSpawn(new Vector2(250), 7));
        for (int i = 0; i < rules.GarrisonCap(1) + 2; i++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(202 + i, 220), garrison: i < rules.GarrisonCap(1)));
        var world = ToLegacy(JObject.FromObject(new SimulationWorld(rules, setup, 7).CaptureCheckpoint()));
        foreach (JObject player in world["Players"]!)
            player["Training"] = new JArray(new JObject { ["Id"] = 7UL, ["Element"] = rules.ElementIndex("water"), ["Cost"] = 8 }, new JObject { ["Id"] = 9UL, ["Element"] = rules.ElementIndex("fire"), ["Cost"] = 10 });
        foreach (JObject unit in world["Units"]!) unit["Garrison"] = true;
        return world;
    }

    private static JObject ToLegacy(JObject world)
    {
        RemoveFacilityFields(world);
        world.Remove("SchemaVersion"); world.Remove("Candidates"); world["DataHash"] = WorldCheckpointUpgrade.SupportedSourceHash; world["Tick"] = 100;
        foreach (JObject player in world["Players"]!)
        {
            foreach (string field in new[] { "FocusAnchor", "Squads", "TrainingStartTick", "TrainingEndTick", "TrainingPausedAtTick", "NextTrainingOrderId", "RecallSquad", "RecallEndTick", "RecallReadyTick", "RecallSequence", "RecallArrivalSlots", "NextDefenseAlarmTick", "JourneyTotal", "JourneyAcknowledged" }) player.Remove(field);
            foreach (JObject order in player["Training"]!) order.Remove("TargetSquad");
        }
        foreach (JObject unit in world["Units"]!)
        {
            foreach (string field in new[] { "SquadId", "DestinationSquadId", "IsReinforcing", "MovementRevision", "KillerSquad", "FreeOrigin", "RescuedOnce", "LastHitSquads" }) unit.Remove(field);
            foreach (JObject status in unit["Statuses"]!) status.Remove("SourceSquad");
        }
        foreach (JObject building in world["Buildings"]!) { building.Remove("FreeOrigin"); building.Remove("KillerSquad"); }
        foreach (JObject area in world["Areas"]!) area.Remove("SourceSquad");
        return world;
    }

    private static void RemoveFacilityFields(JObject world)
    {
        world.Remove("MapSize");
        foreach (JObject player in world["Players"]!)
            foreach (JObject order in player["Training"]!) order.Remove("AttackBonus");
        foreach (JObject unit in world["Units"]!) unit.Remove("TrainingAttackBonus");
        if (world["Candidates"] is JArray candidates)
            foreach (JObject candidate in candidates) candidate.Remove("TrainingAttackBonus");
        foreach (JObject building in world["Buildings"]!)
            foreach (string field in new[] { "UpgradeBranch", "UpgradeRank", "NextProductionTick", "ProductionPausedAtTick" })
                building.Remove(field);
    }

    private static JArray RemoveNewUnitFields(JArray units)
    {
        var wrapper = new JObject { ["Players"] = new JArray(), ["Units"] = units.DeepClone(), ["Buildings"] = new JArray(), ["Areas"] = new JArray() };
        return (JArray)ToLegacy(wrapper)["Units"]!;
    }

    private static JObject LegacySave() => new() { ["Format"] = 1, ["DataHash"] = WorldCheckpointUpgrade.SupportedSourceHash,
        ["Save"] = new JObject { ["WorldId"] = "test", ["World"] = LegacyWorld(GameRulesTests.Rules()), ["Players"] = new JObject { ["00112233445566778899aabbccddeeff"] = 1 }, ["NextPlayerId"] = 2, ["Title"] = null, ["MinimumPlayers"] = 0, ["Capacity"] = 0 } };

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("테스트 저장소 루트를 찾지 못했습니다.");
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("world-upgrade-");
        public string FullName => directory.FullName;
        public string PathFor(string child) => Path.Combine(FullName, child);
        public string Child(string child) => Directory.CreateDirectory(PathFor(child)).FullName;
        public void Dispose() => directory.Delete(true);
    }
}
