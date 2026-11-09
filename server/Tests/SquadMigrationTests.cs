using System;
using System.Linq;
using System.Numerics;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests
{
    /// <summary>독립 명령과 분대 출처가 승계·디스크 복원에서 누락되지 않는지 검증한다.</summary>
    public sealed class SquadMigrationTests
    {
        private static SimulationWorld World()
        {
            var setup = new WorldSetup { CameraVisibility = true };
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(99, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(102, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 105), garrison: true));
            return new SimulationWorld(GameRulesTests.Rules(), setup, 23);
        }
        [Fact]
        public void MigrationPreservesIndependentOrdersAndPendingSquadCommands()
        {
            var world = World();
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 2, 1)); world.Step();
            world.TryEnqueue(1, SimCommand.MoveTo(2, 2, new Vector2(150, 150)));
            world.TryEnqueue(1, SimCommand.Move(3, 1, Vector2.UnitX)); world.Step();
            world.TryEnqueue(1, SimCommand.Rally(4, 3, 1));
            world.TryEnqueue(1, SimCommand.Focus(5, 2));
            var resumed = SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), JsonConvert.DeserializeObject<MigrationWorldState>(JsonConvert.SerializeObject(world.CaptureMigrationState()))!);
            for (int i = 0; i < 30; i++)
            {
                world.Step(); resumed.Step();
                Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(resumed.CaptureMigrationState()));
            }
            Assert.Equal((byte)2, resumed.GetView(1).FocusAnchor);
            Assert.Equal(3, resumed.GetView(1).Population);
        }
        [Theory]
        [InlineData("SquadId", 5)]
        [InlineData("DestinationSquadId", 3)]
        public void RestoreRejectsInvalidMembershipWithoutChangingSource(string field, int value)
        {
            var world = World();
            string before = JsonConvert.SerializeObject(world.CaptureMigrationState());
            var json = JObject.Parse(before);
            json["World"]!["Units"]![0]![field] = value;
            Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), json.ToObject<MigrationWorldState>()!));
            Assert.Equal(before, JsonConvert.SerializeObject(world.CaptureMigrationState()));
        }
        [Fact]
        public void RestoreRejectsDuplicateSquadsAndNonFiniteDestinations()
        {
            var world = World();
            var json = JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState()));
            json["World"]!["Players"]![0]!["Squads"]![1]!["Id"] = 1;
            Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), json.ToObject<MigrationWorldState>()!));
            json = JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState()));
            json["World"]!["Players"]![0]!["Squads"]![1]!["Destination"]!["X"] = float.NaN;
            Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), json.ToObject<MigrationWorldState>()!));
        }
        [Fact]
        public void DiskRestoreStopsEverySquadAndPreservesMembers()
        {
            var world = World();
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 2, 1)); world.Step();
            world.TryEnqueue(1, SimCommand.Move(2, 3, Vector2.UnitX)); world.Step();
            var restored = SimulationWorld.RestoreCheckpoint(GameRulesTests.Rules(), JsonConvert.DeserializeObject<WorldCheckpoint>(JsonConvert.SerializeObject(world.CaptureCheckpoint()))!);
            Assert.All(restored.GetView(1).Squads, x => Assert.Equal(SquadOrderKind.Hold, x.Order));
            Assert.Equal(world.GetView(1).Units.Select(x => (x.Id, x.SquadId)), restored.GetView(1).Units.Select(x => (x.Id, x.SquadId)));
        }
        [Fact]
        public void GuardOrderAndCenterSurviveMigrationButDiskStopsOrder()
        {
            var world = World();
            world.TryEnqueue(1, SimCommand.MoveTo(1, 8, new Vector2(100, 200))); world.Step();
            Assert.Equal(SquadOrderKind.MoveTo, world.GetView(1).Squads[3].Order);
            var resumed = SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), JsonConvert.DeserializeObject<MigrationWorldState>(JsonConvert.SerializeObject(world.CaptureMigrationState()))!);
            for (int i = 0; i < 20; i++)
            {
                world.Step(); resumed.Step();
                Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(resumed.CaptureMigrationState()));
            }
            Vector2 center = world.GetView(1).Squads[3].Position;
            var disk = SimulationWorld.RestoreCheckpoint(GameRulesTests.Rules(), world.CaptureCheckpoint());
            Assert.Equal(center, disk.GetView(1).Squads[3].Position);
            Assert.Equal(SquadOrderKind.Hold, disk.GetView(1).Squads[3].Order);
            var json = JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState()));
            json["World"]!["Players"]![0]!["Squads"]![3]!["Position"]!["X"] = 126;
            Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), json.ToObject<MigrationWorldState>()!));
        }
        [Fact]
        public void RestoreRejectsGuardCenterInsideBuilding()
        {
            var world = World();
            var json = JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState()));
            json["World"]!["Players"]![0]!["Squads"]![3]!["Position"] = JObject.FromObject(new Vector2(100));
            Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), json.ToObject<MigrationWorldState>()!));
        }
        [Fact]
        public void GuardCatchupAfterCenterHoldsSurvivesMigrationAndStopsOnDiskRestore()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 120), garrison: true));
            var world = new SimulationWorld(GameRulesTests.Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.MoveTo(1, 8, world.GetView(1).Squads[3].Position)); world.Step();
            Assert.Equal(SquadOrderKind.Hold, world.GetView(1).Squads[3].Order);
            var resumed = SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), world.CaptureMigrationState());
            var disk = SimulationWorld.RestoreCheckpoint(GameRulesTests.Rules(), world.CaptureCheckpoint());
            Vector2 diskPosition = Assert.Single(disk.GetView(1).Units).Position;
            for (int i = 0; i < 30; i++)
            {
                world.Step(); resumed.Step(); disk.Step();
                Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(resumed.CaptureMigrationState()));
                Assert.Equal(diskPosition, Assert.Single(disk.GetView(1).Units).Position);
            }
            Assert.Equal(world.GetView(1).Squads[3].Position, Assert.Single(world.GetView(1).Units).Position);
        }
    }
}
