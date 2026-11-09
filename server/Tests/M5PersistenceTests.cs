using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class M5PersistenceTests
{
    [Fact]
    public void FileFormatTwoPreservesOrdersAndRefusesVersionOneWithoutOverwriting()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var rules = GameRulesTests.Rules();
            var world = DiskTrainingPauseTests.TrainingWorld(rules);
            var store = new FileWorldStore(directory.FullName, rules);
            var save = new WorldSave("test", world.CaptureCheckpoint(), new(), 2);
            store.Save(save);
            var file = Assert.Single(directory.GetFiles("*.json"));
            var json = JObject.Parse(File.ReadAllText(file.FullName));
            Assert.Equal(2, (int)json["Format"]!);
            Assert.Equal(3, (int)json["Save"]!["World"]!["SchemaVersion"]!);
            var loaded = SimulationWorld.RestoreCheckpoint(rules, store.Load("test")!.World);
            Assert.Equal(2, loaded.GetView(1).ReservedPopulation);
            Assert.Equal(world.GetView(1).CoreBalance, loaded.GetView(1).CoreBalance);
            json["Format"] = 1;
            string legacy = json.ToString();
            File.WriteAllText(file.FullName, legacy);
            Assert.Throws<InvalidDataException>(() => store.Load("test"));
            Assert.Throws<InvalidDataException>(() => store.Save(save));
            Assert.Equal(legacy, File.ReadAllText(file.FullName));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void RestartPreservesPaidOrdersRefundsConstructionAndResetsCommandSequenceOnResume()
    {
        var rules = GameRulesTests.Rules();
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 225),
            cores: 300, natureForge: true));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 225)));
        var original = new SimulationWorld(rules, setup, 7);
        Assert.Equal(CommandAdmission.Accepted, original.TryEnqueue(1, SimCommand.Train(7, "water")));
        Assert.Equal(CommandAdmission.Accepted, original.TryEnqueue(1, SimCommand.Build(8, BuildingKind.Collector, 200, 215)));
        original.Step();
        var previous = original.GetView(1);
        Assert.Equal(1, previous.ReservedPopulation);
        Assert.Contains(previous.Buildings, building => building.IsUnderConstruction);

        var restored = SimulationWorld.RestoreCheckpoint(rules, original.CaptureCheckpoint());
        var current = restored.GetView(1);
        Assert.Equal(previous.Tick, current.Tick);
        Assert.Equal(previous.Vision.Explored.ToArray(), current.Vision.Explored.ToArray());
        Assert.Equal(300 - rules.TrainingCost(rules.ElementIndex("water"), rules.ElementIndex("water")), current.CoreBalance);
        Assert.Equal(1, current.ReservedPopulation);
        Assert.DoesNotContain(current.Buildings, building => building.IsUnderConstruction);
        Assert.Equal(previous.Journey.Total.CoresSpent, current.Journey.Total.CoresSpent);
        Assert.Equal(320, current.Journey.Total.CoresIn);
        Assert.Equal(CommandAdmission.UnknownPlayer, restored.TryEnqueue(1, SimCommand.Rally(1)));

        restored.Step();
        var reloaded = SimulationWorld.RestoreCheckpoint(rules, restored.CaptureCheckpoint());
        Assert.Equal(current.CoreBalance, reloaded.GetView(1).CoreBalance);
        Assert.Equal(current.Journey, reloaded.GetView(1).Journey);
        Assert.True(reloaded.TryResumeGhost(1));
        Assert.False(reloaded.TryResumeGhost(1));
        Assert.Equal(0UL, reloaded.GetView(1).AppliedSequence);
        Assert.Equal(CommandAdmission.Accepted, reloaded.TryEnqueue(1, SimCommand.Rally(1)));
    }

    [Fact]
    public void ExistingGhostKeepsRemainingTicks()
    {
        var json = GameRulesTests.Data().Root;
        json["ghost"]!["decaySeconds"] = 0.15;
        json["ghost"]!["reconnectWindowSeconds"] = 0.15;
        var rules = new GameRules(new GameDataDocument(json.ToString()));
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 225)));
        var original = new SimulationWorld(rules, setup, 7);
        Assert.True(original.BeginGhost(1));
        original.Step();

        var restored = SimulationWorld.RestoreCheckpoint(rules, original.CaptureCheckpoint());
        restored.Step();
        Assert.True(restored.HasGhosts);
        restored.Step();
        Assert.False(restored.TryResumeGhost(1));
        Assert.False(restored.TryGetView(1, out _));
    }

    [Fact]
    public void RadiationKeepsItsSourceOwnerAfterReloadEvenIfTheSourceDied()
    {
        var json = GameRulesTests.Data().Root;
        var decay = json["elements"]!.Single(element => (string?)element["id"] == "decay");
        decay["effect"]!["damagePerSecond"] = 100;
        var rules = new GameRules(new GameDataDocument(json.ToString()));
        var setup = CombatTests.Battle(
            CombatTests.Unit(1, "decay", 50, hp: 1, converted: true),
            CombatTests.Unit(2, "earth", 51, hp: 10, converted: true),
            CombatTests.Unit(1, "water", 40, 40));
        var world = new SimulationWorld(rules, setup, 7);
        world.Step();
        Assert.DoesNotContain(world.GetView(1).Units, unit => unit.Id == 1);
        Assert.Contains(world.GetView(1).Units, unit => unit.Id == 2 &&
            (unit.StatusMask & (1 << (int)StatusKind.Radiation)) != 0);

        var restored = SimulationWorld.RestoreCheckpoint(rules, world.CaptureCheckpoint());
        bool credited = false;
        for (int tick = 0; tick < 5 && !credited; tick++)
        {
            restored.Step();
            credited = restored.GetView(1).Events.Any(item =>
                item.Kind == SimEventKind.Death && item.OwnerId == 1 && item.TargetId == 2);
        }
        Assert.True(credited);
    }

    [Fact]
    public void CorruptPrimaryUsesBackupAndClosedWorldNeverReopens()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var rules = GameRulesTests.Rules();
            var world = new SimulationWorld(rules, new WorldSetup(), 3);
            var store = new FileWorldStore(directory.FullName, rules);
            store.Save(new WorldSave("test", world.CaptureCheckpoint(), new(), 1));
            world.Step();
            store.Save(new WorldSave("test", world.CaptureCheckpoint(), new(), 1));
            var primary = Assert.Single(directory.GetFiles("*.json"));
            File.WriteAllText(primary.FullName, "{");

            Assert.Equal(0, store.Load("test")!.World.Tick);
            store.Save(new WorldSave("test", world.CaptureCheckpoint(), new(), 1));
            Assert.Equal(1, store.Load("test")!.World.Tick);
            File.WriteAllText(primary.FullName, "{");
            Assert.Equal(0, store.Load("test")!.World.Tick);
            store.Close("test");

            var restarted = new FileWorldStore(directory.FullName, rules);
            Assert.Empty(restarted.ListIds());
            Assert.Null(restarted.Load("test"));
            Assert.Throws<InvalidOperationException>(() => restarted.Save(new WorldSave("test", world.CaptureCheckpoint(), new(), 1)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PrimaryWithExcessCandidateReservationUsesValidBackup()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var rules = ReactionEconomyTests.CertainConversion();
            var world = ConversionRecoveryTests.Create(); world.Step();
            var store = new FileWorldStore(directory.FullName, rules);
            store.Save(new WorldSave("candidate-budget", world.CaptureCheckpoint(), new(), 3));
            world.Step(); store.Save(new WorldSave("candidate-budget", world.CaptureCheckpoint(), new(), 3));
            var primary = Assert.Single(directory.GetFiles("*.json"));
            var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(primary.FullName));
            var saved = json["Save"]!["World"]!;
            var units = (Newtonsoft.Json.Linq.JArray)saved["Units"]!;
            var template = units[0]!.DeepClone();
            template["Owner"] = -1; template["SquadId"] = 0; template["WasNeutral"] = true;
            template["Position"] = Newtonsoft.Json.Linq.JObject.FromObject(new Vector2(1500));
            int next = saved["NextId"]!.Value<int>();
            for (int index = 1; index < 4096; index++)
            {
                var unit = template.DeepClone(); unit["Id"] = next++; units.Add(unit);
            }
            saved["NextId"] = next;
            File.WriteAllText(primary.FullName, json.ToString());
            Assert.Equal(1, store.Load("candidate-budget")!.World.Tick);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void EcologyRoundTripContinuesTheCoreScheduleAndReseedsDeterministically()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var json = GameRulesTests.Data().Root;
            json["economy"]!["mapCoreSpawnPerPlayerPerMinute"] = 600;
            json["economy"]!["opening"]!["nearbyBundlesPerMinute"] = 148;
            var data = new GameDataDocument(json.ToString());
            var rules = new GameRules(data);
            var original = new SimulationWorld(rules, new WorldSetup(), 1);
            original.EnableEcology(new NetworkSettings(data).MinBaseDistance);
            Assert.True(original.TryChooseSpawn(out Vector2 home, out Vector2 legion));
            Assert.True(original.TryAddPlayer(new PlayerSpawn(1, "water", home, legion, nickname: "복귀")));
            original.Step();
            Assert.Equal(1, original.Metrics.DroppedCores);
            Assert.True(original.BeginGhost(1));
            var store = new FileWorldStore(directory.FullName, rules);
            string account = Guid.NewGuid().ToString("N");
            store.Save(new WorldSave("test", original.CaptureCheckpoint(), new() { [account] = 1 }, 2, "저장된 방", 1, 2));

            WorldSave saved = Assert.IsType<WorldSave>(store.Load("test"));
            Assert.Equal(1, saved.Players[account]);
            var restored = SimulationWorld.RestoreCheckpoint(rules, saved.World);
            var reloaded = SimulationWorld.RestoreCheckpoint(rules, saved.World);
            Assert.Equal(original.Metrics.NeutralUnits, restored.Metrics.NeutralUnits);
            Assert.True(reloaded.TryResumeGhost(1));
            Assert.True(restored.TryResumeGhost(1));
            for (int tick = 0; tick < 8; tick++)
            {
                reloaded.Step();
                restored.Step();
                Assert.Equal(reloaded.GetView(1).Cores.ToArray(), restored.GetView(1).Cores.ToArray());
                Assert.Equal(reloaded.Metrics.DroppedCores, restored.Metrics.DroppedCores);
                if (tick == 0) Assert.NotEmpty(restored.GetView(1).Cores);
            }
            Assert.True(restored.Metrics.DroppedCores > 0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DataHashMismatchPreservesPrimaryAndBackup()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var rules = GameRulesTests.Rules();
            var world = new SimulationWorld(rules, new WorldSetup(), 3);
            var store = new FileWorldStore(directory.FullName, rules);
            store.Save(new WorldSave("test", world.CaptureCheckpoint(), new(), 1));
            world.Step();
            store.Save(new WorldSave("test", world.CaptureCheckpoint(), new(), 1));
            var primary = Assert.Single(directory.GetFiles("*.json"));
            string otherHash = new('a', rules.DataHash.Length);
            string mismatched = File.ReadAllText(primary.FullName).Replace(rules.DataHash, otherHash);
            File.WriteAllText(primary.FullName, mismatched);

            Assert.Throws<InvalidDataException>(() => store.Load("test"));
            Assert.Throws<InvalidDataException>(() => store.Save(new WorldSave("test", world.CaptureCheckpoint(), new(), 1)));
            Assert.Equal(mismatched, File.ReadAllText(primary.FullName));
            Assert.Single(directory.GetFiles("*.bak"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
