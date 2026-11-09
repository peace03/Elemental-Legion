using System;
using System.Linq;
using System.Numerics;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>실제 시설 피해 경보의 소유권·간격·복원 상태를 검증한다.</summary>
public sealed class DefenseAlertTests
{
    private const SimEventKind Alert = (SimEventKind)12;
    private static SimulationWorld World()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(300), new Vector2(100, 107)));
        setup.Players.Add(new PlayerSpawn(3, "earth", new Vector2(500), new Vector2(300, 307)));
        setup.Units.Add(new UnitSpawn(2, "water", new Vector2(100, 107)));
        setup.Units.Add(new UnitSpawn(3, "water", new Vector2(300, 307)));
        return new SimulationWorld(GameRulesTests.Rules(), setup, 4);
    }

    [Fact]
    public void DefenseAlertIsOwnerOnlyAndCoalesced()
    {
        var world = World();
        world.Step();
        foreach (int owner in new[] { 1, 2 })
        {
            var alert = Assert.Single(world.GetView(owner).Events, x => x.Kind == Alert);
            Assert.Equal(owner, alert.OwnerId);
            Assert.Equal(world.GetView(owner).BasePosition, alert.Position);
            Assert.True(alert.Amount > 0);
            Assert.Equal(0, alert.SourceId);
            Assert.Equal(-1, alert.Element);
            Assert.False(alert.HasSourcePosition);
            Assert.Equal(Vector2.Zero, alert.SourcePosition);
        }
        Assert.DoesNotContain(world.GetView(3).Events, x => x.Kind == Alert);
        int repeatAlerts = 0;
        for (int i = 0; i < 99; i++)
        {
            world.Step();
            Assert.DoesNotContain(world.GetView(1).Events, x => x.Kind == Alert);
        }
        for (int i = 0; i < 40; i++)
        {
            world.Step();
            repeatAlerts += world.GetView(1).Events.Count(x => x.Kind == Alert);
        }
        Assert.Equal(1, repeatAlerts);
    }

    [Fact]
    public void AlarmTimerSurvivesMigrationAndDiskAndIsRequired()
    {
        var world = World(); world.Step();
        var migration = JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState()));
        Assert.Equal(101, (long?)migration["World"]!["Players"]![0]!["NextDefenseAlarmTick"]);
        var resumed = SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), migration.ToObject<MigrationWorldState>()!);
        for (int i = 0; i < 30; i++)
        {
            world.Step(); resumed.Step();
            Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(resumed.CaptureMigrationState()));
            Assert.DoesNotContain(resumed.GetView(1).Events, x => x.Kind == Alert);
        }
        var checkpoint = JObject.Parse(JsonConvert.SerializeObject(world.CaptureCheckpoint()));
        var disk = SimulationWorld.RestoreCheckpoint(GameRulesTests.Rules(), checkpoint.ToObject<WorldCheckpoint>()!);
        Assert.Equal(101, (long?)JObject.Parse(JsonConvert.SerializeObject(disk.CaptureCheckpoint()))["Players"]![0]!["NextDefenseAlarmTick"]);
        ((JObject)checkpoint["Players"]![0]!).Remove("NextDefenseAlarmTick");
        Assert.Throws<JsonSerializationException>(() => checkpoint.ToObject<WorldCheckpoint>());
        migration["World"]!["Players"]![0]!["NextDefenseAlarmTick"] = 100000;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), migration.ToObject<MigrationWorldState>()!));
    }

    [Fact]
    public void DestroyedRemoteFacilityAlertsOwnerWithoutRevealingHiddenAttacker()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110), natureForge: true));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(500), new Vector2(400, 407)));
        setup.Units.Add(new UnitSpawn(2, "water", new Vector2(400, 407)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 7);
        var json = JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState()));
        var forge = json["World"]!["Buildings"]!.Single(x => (int)x["Kind"]! == (int)BuildingKind.NatureForge);
        forge["Position"]!["X"] = 400; forge["Position"]!["Y"] = 400; forge["Health"] = 1;
        world = SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), json.ToObject<MigrationWorldState>()!);
        world.Step();
        var owner = world.GetView(1);
        var alert = Assert.Single(owner.Events, x => x.Kind == Alert);
        Assert.Equal(new Vector2(400), alert.Position);
        Assert.Equal(1, alert.Amount);
        Assert.Equal(0, alert.SourceId);
        Assert.Equal(0, alert.Detail);
        Assert.Equal(-1, alert.Element);
        Assert.False(alert.HasSourcePosition);
        Assert.Equal(Vector2.Zero, alert.SourcePosition);
        Assert.DoesNotContain(owner.Units, x => x.OwnerId == 2);
        Assert.False(owner.CanSeePoint(new Vector2(400, 407)));
        Assert.DoesNotContain(world.GetView(2).Events, x => x.Kind == Alert);
    }

    [Fact]
    public void DifferentOwnFacilitiesShareOneAlarmWindow()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110), natureForge: true));
        setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(500), new Vector2(100, 107)));
        setup.Units.Add(new UnitSpawn(2, "water", new Vector2(100, 107)));
        setup.Units.Add(new UnitSpawn(2, "water", new Vector2(400, 407)));
        var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        var json = JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState()));
        var forge = json["World"]!["Buildings"]!.Single(x => (int)x["Kind"]! == (int)BuildingKind.NatureForge);
        forge["Position"]!["X"] = 400; forge["Position"]!["Y"] = 400;
        json["World"]!["Units"]![1]!["SquadId"] = 2;
        json["World"]!["Players"]![1]!["Squads"]![1]!["Position"] = JObject.FromObject(new Vector2(400, 407));
        json["World"]!["Players"]![1]!["Squads"]![1]!["Destination"] = JObject.FromObject(new Vector2(400, 407));
        world = SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), json.ToObject<MigrationWorldState>()!);
        world.Step();
        Assert.Single(world.GetView(1).Events, x => x.Kind == Alert);
        Assert.Equal(2, world.GetView(2).Events.Count(x => x.Kind == SimEventKind.Attack && x.TargetKind == ObservedEntityKind.Building));
    }
}
