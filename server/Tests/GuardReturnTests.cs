using System.Linq;
using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Xunit;

namespace ElementalLegion.Tests
{
    /// <summary>야전 병력의 수비대 복귀와 이동 중 수비 인구 예약을 확인한다.</summary>
    public sealed class GuardReturnTests
    {
        [Fact]
        public void DistantFieldMembersWalkHomeBeforeJoiningGuards()
        {
            var world = World(2);
            var ids = world.GetView(1).Units.Select(unit => unit.Id).ToArray();
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, 2)));
            world.Step();
            var view = world.GetView(1);
            Assert.Equal(2, view.Squads[3].ReinforcingCount);
            Assert.Equal(0, view.Squads[3].JoinedCount);
            Assert.All(view.Units, unit => Assert.True(unit.IsReinforcing));
            Assert.All(view.Units, unit => Assert.Equal((byte)4, unit.DestinationSquadId));
            Assert.All(view.Units, unit => Assert.True(Vector2.Distance(unit.Position, view.BasePosition) > 40));
            for (int tick = 0; tick < 600 && view.Squads[3].JoinedCount < 2; tick++) world.Step();
            Assert.Equal(2, view.Squads[3].JoinedCount);
            Assert.Equal(0, view.Squads[3].ReinforcingCount);
            Assert.Equal(ids, view.Units.Select(unit => unit.Id));
            Assert.All(view.Units, unit => Assert.True(unit.Garrison && !unit.IsReinforcing));
            Assert.All(view.Units, unit => Assert.InRange(Vector2.Distance(unit.Position, view.BasePosition), 0, GameRulesTests.Rules().GarrisonRadius + .001f));
        }

        [Fact]
        public void ReturningMembersReserveGuardCapacityAgainstTrainingAndDuplicateReturns()
        {
            int cap = GameRulesTests.Rules().GarrisonCap(1);
            var world = World(cap);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, cap));
            world.Step();
            long cores = world.GetView(1).CoreBalance;
            world.TryEnqueue(1, SimCommand.Train(2, "water", 1, 4));
            world.Step();
            Assert.Contains(world.GetView(1).Events, item => item.Sequence == 2 && item.Kind == SimEventKind.CommandRejected && item.Detail == (int)CommandFailure.PopulationCap);
            Assert.Equal(cores, world.GetView(1).CoreBalance);
            Assert.Equal(0, world.GetView(1).ReservedPopulation);
            Assert.Equal(CommandAdmission.StaleSequence, world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, cap)));
            world.TryEnqueue(1, SimCommand.Transfer(3, 1, 4, 1));
            world.Step();
            Assert.Contains(world.GetView(1).Events, item => item.Sequence == 3 && item.Kind == SimEventKind.CommandRejected);
            Assert.Equal(cap, world.GetView(1).Squads[3].ReinforcingCount);
        }

        [Fact]
        public void OverCapacityReturnPreservesOriginalMembership()
        {
            int cap = GameRulesTests.Rules().GarrisonCap(1);
            var world = World(cap + 1);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, cap + 1));
            world.Step();
            Assert.Contains(world.GetView(1).Events, item => item.Kind == SimEventKind.CommandRejected && item.Detail == (int)CommandFailure.PopulationCap);
            Assert.Equal(cap + 1, world.GetView(1).Squads[0].JoinedCount);
            Assert.Equal(0, world.GetView(1).Squads[3].ReinforcingCount);
        }

        [Fact]
        public void MigrationPreservesAnInProgressReturnExactly()
        {
            var world = World(3);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, 3));
            world.Step();
            Assert.Equal(3, world.GetView(1).Squads[3].ReinforcingCount);
            var restored = SimulationWorld.RestoreMigrationState(GameRulesTests.Rules(), JsonConvert.DeserializeObject<MigrationWorldState>(JsonConvert.SerializeObject(world.CaptureMigrationState()))!);
            for (int tick = 0; tick < 220; tick++)
            {
                world.Step(); restored.Step();
                Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(restored.CaptureMigrationState()));
            }
        }

        [Fact]
        public void NeutralConversionOutsideBaseKeepsWalkingInsteadOfTeleportingIntoGuardRadius()
        {
            var world = World(1);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, 1)); world.Step();
            var state = State(world);
            var source = Assert.Single(state.Units);
            var neutral = state.AddUnit(-1, state.Rules.ElementIndex("earth"), source.Position + new Vector2(1, 0));
            state.Random.RestoreState(new StatefulRandom(1).CaptureState());
            CombatSystem.Damage(state, neutral, 100000, 1, source.Id, source.Element, impact: false);
            world.Step();
            var converted = world.GetView(1).Units.Single(unit => unit.Id == neutral.Id);
            Assert.True(converted.Converted);
            Assert.True(converted.IsReinforcing);
            Assert.Equal((byte)4, converted.DestinationSquadId);
            Vector2 before = converted.Position;
            world.Step();
            converted = world.GetView(1).Units.Single(unit => unit.Id == neutral.Id);
            Assert.InRange(Vector2.Distance(before, converted.Position), 0, 1);
            Assert.True(Vector2.Distance(converted.Position, world.GetView(1).BasePosition) > 40);
            SimulationWorld.RestoreMigrationState(state.Rules, world.CaptureMigrationState());
        }

        [Fact]
        public void GuardReservationIsReleasedWhenAReturningMemberDies()
        {
            int cap = GameRulesTests.Rules().GarrisonCap(1);
            var world = World(cap);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, cap)); world.Step();
            var state = State(world);
            CombatSystem.Damage(state, state.Units[0], 100000, -1, 0, state.Rules.ElementIndex("fire"), impact: false);
            world.Step();
            Assert.Equal(cap - 1, world.GetView(1).Squads[3].ReinforcingCount);
            world.TryEnqueue(1, SimCommand.Train(2, "water", 1, 4)); world.Step();
            Assert.Equal(1, world.GetView(1).ReservedPopulation);
            Assert.Equal(cap - 1, world.GetView(1).Population);
        }

        [Fact]
        public void DiskRestorePausesReturningGuardsAndResumeContinuesTheirJourney()
        {
            var world = World(2);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, 2)); world.Step();
            world = SimulationWorld.RestoreCheckpoint(GameRulesTests.Rules(), world.CaptureCheckpoint());
            Assert.Equal(2, world.GetView(1).Squads[3].ReinforcingCount);
            Vector2 before = world.GetView(1).Units[0].Position;
            world.Step();
            Assert.Equal(before, world.GetView(1).Units[0].Position);
            Assert.True(world.TryResumeGhost(1));
            world.Step();
            Assert.True(Vector2.Distance(before, world.GetView(1).Units[0].Position) > 0);
        }

        private static WorldState State(SimulationWorld world) => (WorldState)typeof(SimulationWorld)
            .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;

        private static SimulationWorld World(int members)
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(160, 100), cores: 200, natureForge: true));
            for (int index = 0; index < members; index++)
                setup.Units.Add(new UnitSpawn(1, "water", new Vector2(160 + index % 3 * 2, 100 + index / 3 * 2)));
            return new SimulationWorld(GameRulesTests.Rules(), setup, 1);
        }
    }
}
