using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests
{
    public sealed class SquadCommandTests
    {
        private static GameRules Rules() => GameRulesTests.Rules();
        private static SimulationWorld World()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 110)));
            for (int i = 0; i < 3; i++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100 + i * 2, 110)));
            return new SimulationWorld(Rules(), setup, 9);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(5, 3)]
        public void QuickTransferUsesOnlyLivingJoinedMembers(int available, int expected)
        {
            GameRules rules = Rules();
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 180)));
            setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(300), new Vector2(300, 310)));
            setup.Units.Add(new UnitSpawn(2, "water", new Vector2(300, 310)));
            for (int i = 0; i < available + 2; i++)
                setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100 + i * 2, 180)));
            var world = new SimulationWorld(rules, setup, 1);
            var state = State(world);
            var own = state.Units.Where(unit => unit.OwnerId == 1).ToArray();
            int water = rules.ElementIndex("water");
            CombatSystem.Damage(state, own[0], 10000, -1, 0, water, impact: false);
            own[1].SquadId = 0; own[1].DestinationSquadId = 1; own[1].IsReinforcing = true;
            // 출발 판정 때 다른 분대를 향한 명령과 이미 이동 중인 증원은 유지된다.
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(1, 1, Vector2.UnitY)));
            var command = SimCommand.QuickTransfer(2, 1, 2);
            Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.QuickTransfer(2, 1, 1)));
            Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.QuickTransfer(2, 5, 1)));
            Assert.Equal(CommandAdmission.UnknownPlayer, world.TryEnqueue(3, command));
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, command));
            Assert.Equal(CommandAdmission.StaleSequence, world.TryEnqueue(1, command));
            Vector2 center = world.GetView(1).Squads[0].Position;
            world.Step();

            var view = world.GetView(1);
            Assert.Equal(available + 1, view.Population);
            Assert.Equal(expected, view.Squads[1].ReinforcingCount);
            Assert.Equal(0, view.Squads[1].JoinedCount);
            Assert.Equal((byte)1, view.FocusAnchor);
            Assert.Equal(SquadOrderKind.MoveDir, view.Squads[0].Order);
            Assert.True(view.Squads[0].Position.Y > center.Y);
            Assert.Equal(1, world.GetView(2).Squads[0].JoinedCount);
            var moved = view.Units.Where(unit => unit.DestinationSquadId == 2).ToArray();
            Assert.Equal(own.Skip(2).Take(expected).Select(unit => unit.Id), moved.Select(unit => unit.Id));
            Assert.All(moved, unit =>
            {
                Assert.True(unit.IsReinforcing);
                Assert.Equal((byte)0, unit.SquadId);
                Assert.Equal(rules.Elements[water].Hp, unit.Health);
                Assert.InRange(Vector2.Distance(unit.Position, view.BasePosition), 75, 90);
            });
            var result = Assert.Single(view.Events, item => item.Sequence == 2);
            Assert.Equal(expected == 0 ? SimEventKind.CommandRejected : SimEventKind.CommandApplied, result.Kind);
            Assert.Equal(expected == 0 ? (int)CommandFailure.Busy : (int)CommandKind.QuickTransfer, result.Detail);
            Assert.Equal(expected, result.Amount);
            Assert.DoesNotContain(world.GetView(2).Events, item => item.Sequence == 2);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        public void QuickTransferCancelsSourceRecallWhenAnyMembersDepart(int count)
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 180)));
            for (int index = 0; index < count; index++)
                setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100 + index * 2, 180)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
            var view = world.GetView(1);
            Assert.Equal((byte)1, view.RecallSquad);

            world.TryEnqueue(1, SimCommand.QuickTransfer(2, 1, 2)); world.Step();
            Assert.Equal((byte)0, view.RecallSquad);
            Assert.Equal(0, view.RecallReadyTick);
            Assert.Equal(count - Math.Min(count, 3), view.Squads[0].JoinedCount);
            Assert.Contains(view.Events, result => result.Sequence == 1 && result.Kind == SimEventKind.CommandRejected &&
                result.Detail == (int)CommandFailure.RecallInterrupted);
            Assert.Contains(view.Events, result => result.Sequence == 2 && result.Kind == SimEventKind.CommandApplied &&
                result.Amount == Math.Min(count, 3));
        }

        [Fact]
        public void QuickTransferRespectsGuardReservationBeforeCancellingRecall()
        {
            GameRules rules = Rules();
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 180), cores: 100, natureForge: true));
            for (int index = 0; index < rules.GarrisonCap(1) - 2; index++)
                setup.Units.Add(new UnitSpawn(1, "water", new Vector2(94 + index * 2, 115), garrison: true));
            for (int index = 0; index < 3; index++)
                setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100 + index * 2, 180)));
            var world = new SimulationWorld(rules, setup, 1);
            world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 4));
            world.TryEnqueue(1, SimCommand.Recall(2, 1)); world.Step();
            var view = world.GetView(1);
            world.TryEnqueue(1, SimCommand.QuickTransfer(3, 1, 4)); world.Step();
            Assert.Equal((byte)1, view.RecallSquad);
            Assert.Equal(1, view.ReservedPopulation);
            Assert.Equal(0, view.Squads[3].ReinforcingCount);
            Assert.Equal(3, view.Squads[0].JoinedCount);
            var result = Assert.Single(view.Events, item => item.Sequence == 3);
            Assert.Equal(SimEventKind.CommandRejected, result.Kind);
            Assert.Equal((int)CommandFailure.PopulationCap, result.Detail);
        }

        [Fact]
        public void QuickTransferFollowsDestinationAfterRecallWithoutTeleporting()
        {
            GameRules rules = Rules();
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 240)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 240)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
            var world = new SimulationWorld(rules, setup, 1);
            var view = world.GetView(1);
            int dispatchedId = view.Units[0].Id;
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 2, 1)); world.Step();
            world.TryEnqueue(1, SimCommand.MoveTo(2, 2, new Vector2(100, 125))); world.Step();
            Assert.Equal(SquadOrderKind.MoveTo, view.Squads[1].Order);
            world.TryEnqueue(1, SimCommand.Recall(3, 2));
            world.TryEnqueue(1, SimCommand.QuickTransfer(4, 1, 2)); world.Step();
            Assert.Equal(1, view.Squads[1].ReinforcingCount);
            Assert.Equal((byte)2, view.RecallSquad);
            Vector2 previous = view.Units.Single(unit => unit.Id == dispatchedId).Position;
            for (int tick = 0; tick < rules.RecallCastTicks; tick++) world.Step();
            var dispatched = view.Units.Single(unit => unit.Id == dispatchedId);
            Assert.True(dispatched.IsReinforcing);
            Assert.Equal((byte)2, dispatched.DestinationSquadId);
            Assert.Equal(0U, dispatched.MovementRevision);
            Assert.Equal(1U, view.Squads[1].MovementRevision);
            Assert.True(dispatched.Position.Y < previous.Y);
            Assert.True(Vector2.Distance(dispatched.Position, view.BasePosition) > 70);
            for (int tick = 0; tick < 600 && view.Squads[1].ReinforcingCount > 0; tick++) world.Step();
            Assert.Equal(2, view.Squads[1].JoinedCount);
            Assert.Equal(2, view.Population);
            Assert.Equal((byte)2, view.Units.Single(unit => unit.Id == dispatchedId).SquadId);
        }

        [Fact]
        public void QuickTransferMembersCanBeHitAndLoseMembershipWhenKilled()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 180)));
            setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(250), new Vector2(100, 181)));
            setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(100, 180), health: 1));
            setup.Units.Add(new UnitSpawn(2, "water", new Vector2(100, 181)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.QuickTransfer(1, 1, 4)); world.Step();
            var view = world.GetView(1);
            Assert.Contains(view.Events, item => item.Kind == SimEventKind.CommandApplied && item.Sequence == 1 && item.Amount == 1);
            Assert.Equal(0, view.Population);
            Assert.Equal(0, view.Squads[3].ReinforcingCount);
            Assert.Equal(0, view.Squads[3].JoinedCount);
            Assert.Equal(0, view.GarrisonArrivals);
        }

        [Fact]
        public void QuickTransferGuardReturnReservesTheLastSlotAndEndsWithItsBase()
        {
            GameRules rules = Rules();
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110), cores: 100, natureForge: true));
            setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(250), new Vector2(250, 260)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(2, "water", new Vector2(250, 260)));
            for (int index = 0; index < rules.GarrisonCap(1) - 1; index++)
                setup.Units.Add(new UnitSpawn(1, "water", new Vector2(94 + index * 2, 115), garrison: true));
            var world = new SimulationWorld(rules, setup, 1);
            var view = world.GetView(1);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 2, 1)); world.Step();
            world.TryEnqueue(1, SimCommand.MoveTo(2, 2, new Vector2(40, 110)));
            for (int tick = 0; tick < 200; tick++) world.Step();
            var field = Assert.Single(view.Units, unit => unit.SquadId == 2);
            Assert.True(Vector2.Distance(field.Position, view.BasePosition) > rules.GarrisonRadius,
                $"병력 {field.Position}, 분대 중심 {view.Squads[1].Position}");
            world.TryEnqueue(1, SimCommand.QuickTransfer(3, 2, 4));
            world.TryEnqueue(1, SimCommand.Train(4, "water", 1, 4)); world.Step();

            var returning = Assert.Single(view.Units, unit => unit.IsReinforcing);
            Assert.Equal((byte)4, returning.DestinationSquadId);
            Assert.True(Vector2.Distance(returning.Position, view.BasePosition) > rules.GarrisonRadius);
            Assert.Contains(view.Events, item => item.Sequence == 4 && item.Kind == SimEventKind.CommandRejected &&
                item.Detail == (int)CommandFailure.PopulationCap);
            Assert.Equal(0, view.ReservedPopulation);

            var state = State(world);
            var core = Assert.Single(state.Buildings, building => building.OwnerId == 1 && building.Kind == BuildingKind.Base);
            core.Health = 1;
            SiegeSystem.Attack(state, Assert.Single(state.Units, unit => unit.OwnerId == 2), core, 10);
            world.Step();
            Assert.False(world.TryGetView(1, out _));
            Assert.DoesNotContain(state.Units, unit => unit.Id == returning.Id);
            Assert.Equal(1, world.GetView(2).Population);
        }

        private static WorldState State(SimulationWorld world) =>
            (WorldState)typeof(SimulationWorld).GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;

        [Fact]
        public void GuardOrdersStayWithinBaseRadius()
        {
            var setup = new WorldSetup { CameraVisibility = true };
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 107.5f), garrison: true));
            var world = new SimulationWorld(Rules(), setup, 1);
            Vector2 start = Assert.Single(world.GetView(1).Units).Position;
            Assert.True(Vector2.Distance(world.GetView(1).Squads[3].Position, new Vector2(100)) > Rules().BaseRadius);
            world.TryEnqueue(1, SimCommand.Focus(1, 4));
            world.TryEnqueue(1, SimCommand.MoveTo(2, 8, new Vector2(100, 500)));
            world.Step();
            Assert.True(Assert.Single(world.GetView(1).Units).Position.Y > start.Y);
            Assert.InRange(Vector2.Distance(start, Assert.Single(world.GetView(1).Units).Position), 0, 1);
            for (int i = 0; i < 200; i++)
            {
                world.Step();
                Assert.InRange(Vector2.Distance(world.GetView(1).Squads[3].Position, new Vector2(100)), 0, 25.0001f);
                Assert.InRange(Vector2.Distance(Assert.Single(world.GetView(1).Units).Position, new Vector2(100)), 0, 25.0001f);
            }
            Assert.InRange(world.GetView(1).Squads[3].Position.Y, 124.99f, 125.0001f);
            Assert.False(world.GetView(1).CanSeePoint(new Vector2(100, 500)));
            world.TryEnqueue(1, SimCommand.Move(3, 8, Vector2.UnitX)); world.Step();
            Assert.True(world.GetView(1).Squads[3].Position.X > 100);
        }
        [Fact]
        public void RallyMovesOnlyRequestedCountAndAllowsAnotherWave()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
            for (int i = 0; i < 4; i++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(115, 100 + i), garrison: true));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Rally(1, 2, 2)); world.Step();
            Assert.Equal(2, world.GetView(1).Squads[3].JoinedCount);
            Assert.Equal(2, world.GetView(1).Squads[1].ReinforcingCount);
            Assert.Equal(0, world.GetView(1).Squads[1].JoinedCount);
            world.TryEnqueue(1, SimCommand.Rally(2, 3, 1)); world.Step();
            Assert.Equal(1, world.GetView(1).Squads[3].JoinedCount);
            Assert.Equal(1, world.GetView(1).Squads[2].ReinforcingCount);
            world.TryEnqueue(1, SimCommand.Rally(3, 2, 2)); world.Step();
            Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Detail == (int)CommandFailure.Busy);
            for (int i = 0; i < 100; i++) world.Step();
            Assert.Equal(2, world.GetView(1).Squads[1].JoinedCount);
            Assert.Equal(1, world.GetView(1).Squads[2].JoinedCount);
            Assert.Equal(4, world.GetView(1).Population);
        }
        [Fact]
        public void EmptyGuardCanMoveAndHoldPreservesAuthoredPositions()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
            var world = new SimulationWorld(Rules(), setup, 1);
            Vector2 center = world.GetView(1).Squads[3].Position;
            world.TryEnqueue(1, SimCommand.MoveTo(1, 8, new Vector2(500))); world.Step();
            Assert.True(world.GetView(1).Squads[3].Position.Y > center.Y);
            world.TryEnqueue(1, SimCommand.Transfer(2, 1, 4, 1)); world.Step();
            world.TryEnqueue(1, SimCommand.Move(3, 8, Vector2.Zero)); world.Step();
            var guard = world.GetView(1).Units.Single(x => x.SquadId == 4);
            Vector2 position = guard.Position;
            for (int i = 0; i < 10; i++) world.Step();
            Assert.Equal(position, world.GetView(1).Units.Single(x => x.Id == guard.Id).Position);
            Assert.Equal(SquadOrderKind.Hold, world.GetView(1).Squads[3].Order);
        }
        [Fact]
        public void GuardMovementCannotCrossBaseAndNeverLeavesMap()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(10), new Vector2(10, 17.5f)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(10, 17.5f), garrison: true));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.MoveTo(1, 8, new Vector2(10, 0)));
            for (int i = 0; i < 100; i++)
            {
                Vector2 before = Assert.Single(world.GetView(1).Units).Position;
                world.Step();
                var view = world.GetView(1);
                Assert.InRange(Vector2.Distance(view.Squads[3].Position, new Vector2(10)), 5.999f, 25.001f);
                Assert.InRange(Vector2.Distance(before, Assert.Single(view.Units).Position), 0, 1);
                Assert.InRange(Assert.Single(view.Units).Position.Y, 0, Rules().MapSize);
            }
            world.TryEnqueue(1, SimCommand.Move(2, 8, -Vector2.UnitX));
            for (int i = 0; i < 200; i++) world.Step();
            Assert.InRange(world.GetView(1).Squads[3].Position.X, 0, 0.001f);
            Assert.InRange(Assert.Single(world.GetView(1).Units).Position.X, 0, Rules().MapSize);
        }
        [Fact]
        public void GuardMoveToCurrentCenterContinuesCatchupAfterCenterHolds()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(115, 110), garrison: true));
            var world = new SimulationWorld(Rules(), setup, 1);
            Vector2 center = world.GetView(1).Squads[3].Position;
            Vector2 before = Assert.Single(world.GetView(1).Units).Position;
            world.TryEnqueue(1, SimCommand.MoveTo(1, 8, center)); world.Step();
            Assert.True(Vector2.Distance(Assert.Single(world.GetView(1).Units).Position, center) < Vector2.Distance(before, center));
            Assert.Equal(SquadOrderKind.Hold, world.GetView(1).Squads[3].Order);
            world.TryEnqueue(1, SimCommand.Move(2, 8, Vector2.Zero)); world.Step();
            Vector2 stopped = Assert.Single(world.GetView(1).Units).Position;
            world.Step();
            Assert.Equal(stopped, Assert.Single(world.GetView(1).Units).Position);
            world.TryEnqueue(1, SimCommand.MoveTo(3, 8, center));
            for (int i = 0; i < 100; i++) world.Step();
            Assert.Equal(center, Assert.Single(world.GetView(1).Units).Position);
            Assert.Equal(SquadOrderKind.Hold, world.GetView(1).Squads[3].Order);
        }
        [Fact]
        public void GuardFormationAtRadiusStopsOrderAndExplicitHoldStopsCatchup()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
            for (int i = 0; i < 9; i++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(97 + i, 109), garrison: true));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.MoveTo(1, 8, new Vector2(100, 125)));
            for (int i = 0; i < 150; i++) world.Step();
            Assert.Equal(SquadOrderKind.Hold, world.GetView(1).Squads[3].Order);
            Assert.All(world.GetView(1).Units, x => Assert.InRange(Vector2.Distance(x.Position, new Vector2(100)), 0, 25.001f));
            world.TryEnqueue(1, SimCommand.MoveTo(2, 8, new Vector2(110, 110))); world.Step();
            world.TryEnqueue(1, SimCommand.Move(3, 8, Vector2.Zero)); world.Step();
            Assert.Equal(SquadOrderKind.Hold, world.GetView(1).Squads[3].Order);
            Assert.Equal(Vector2.Zero, world.GetView(1).Squads[3].Direction);
        }
        [Fact]
        public void ReinforcementDeathDoesNotJoinOrReserveAnotherRallySlot()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(100, 110)));
            setup.Players.Add(new PlayerSpawn(2, "earth", new Vector2(300), new Vector2(115, 101)));
            setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(115, 100), health: 1, garrison: true));
            setup.Units.Add(new UnitSpawn(2, "water", new Vector2(115, 101)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Rally(1, 2, 1)); world.Step();
            Assert.Equal(0, world.GetView(1).Population);
            Assert.Equal(0, world.GetView(1).Squads[1].JoinedCount);
            Assert.Equal(0, world.GetView(1).Squads[1].ReinforcingCount);
            Assert.Equal(0, world.GetView(1).GarrisonArrivals);
            world.TryEnqueue(1, SimCommand.Rally(2, 2, 1)); world.Step();
            Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Detail == (int)CommandFailure.Busy);
        }
        [Fact]
        public void TransferToGuardRespectsQueuedTrainingReservation()
        {
            var setup = WorldCoreTests.Setup(cores: 1000, level: 1);
            for (int i = 0; i < Rules().GarrisonCap(1) - 1; i++)
                setup.Units.Add(new UnitSpawn(1, "water", new Vector2(190 + i, 215), garrison: true));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200, 210)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Train(1, "water"));
            world.TryEnqueue(1, SimCommand.Transfer(2, 1, 4, 1)); world.Step();
            Assert.Equal(1, world.GetView(1).ReservedPopulation);
            Assert.Equal(1, world.GetView(1).Squads[0].JoinedCount);
            Assert.Equal(Rules().GarrisonCap(1) - 1, world.GetView(1).Squads[3].JoinedCount);
            Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Detail == (int)CommandFailure.PopulationCap);
        }
        [Fact]
        public void SquadsKeepIndependentOrders()
        {
            var world = World();
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Transfer(1, 1, 2, 1)));
            world.Step();
            world.TryEnqueue(1, SimCommand.MoveTo(2, 2, new Vector2(140, 115)));
            world.TryEnqueue(1, SimCommand.Move(3, 1, Vector2.UnitY));
            world.Step();
            var view = world.GetView(1);
            Assert.Equal(SquadOrderKind.MoveDir, view.Squads[0].Order);
            Assert.Equal(SquadOrderKind.MoveTo, view.Squads[1].Order);
            Assert.Equal(SquadOrderKind.Hold, view.Squads[3].Order);
            Assert.Equal(2, view.Squads[0].JoinedCount);
            Assert.Equal(1, view.Squads[1].JoinedCount);
            Assert.Equal(3UL, view.Squads[0].AppliedSequence);
            Assert.Equal(2UL, view.Squads[1].AppliedSequence);
        }
        [Fact]
        public void MoveToStopsAndHolds()
        {
            var world = World();
            Vector2 destination = world.GetView(1).LegionPosition + Vector2.UnitY;
            world.TryEnqueue(1, SimCommand.MoveTo(1, 7, destination));
            for (int i = 0; i < 30; i++) world.Step();
            var view = world.GetView(1);
            Assert.Equal(destination, view.Squads[0].Position);
            Assert.Equal(Vector2.Zero, view.Squads[0].Direction);
            Assert.Equal(SquadOrderKind.Hold, view.Squads[0].Order);
            Assert.Equal(0UL, view.Squads[3].AppliedSequence);
            Assert.Equal(CommandAdmission.UnknownPlayer, world.TryEnqueue(2, SimCommand.Move(1, 1, Vector2.UnitX)));
        }
        [Fact]
        public void InvalidIntentDoesNotConsumeSequenceOrChangeOrders()
        {
            var world = World();
            Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.Move(1, 0, Vector2.UnitX)));
            Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.Move(1, 16, Vector2.UnitX)));
            Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.MoveTo(1, 1, new Vector2(float.NaN, 3))));
            Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.MoveTo(1, 1, new Vector2(1, float.PositiveInfinity))));
            Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, -1)));
            Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Focus(1, 2)));
            world.Step();
            Assert.Equal((byte)2, world.GetView(1).FocusAnchor);
        }
        [Fact]
        public void TransferPreservesIdentityHealthAndPopulation()
        {
            var world = World();
            var before = world.GetView(1).Units.ToDictionary(x => x.Id, x => x.Health);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, 2)); world.Step();
            var view = world.GetView(1);
            Assert.Equal(3, view.Population);
            Assert.Equal(2, view.Units.Count(x => x.SquadId == 4));
            Assert.All(view.Units, x => Assert.Equal(before[x.Id], x.Health));
            Assert.All(view.Units.Where(x => x.SquadId == 4), x => Assert.InRange(Vector2.Distance(x.Position, view.BasePosition), 0, Rules().GarrisonRadius + 0.001f));
        }
        [Fact]
        public void EmptySquadMovesAndKeepsItsCenterWhenMembersTransfer()
        {
            var world = World();
            world.TryEnqueue(1, SimCommand.MoveTo(1, 2, new Vector2(500, 500))); world.Step();
            Vector2 empty = world.GetView(1).Squads[1].Position;
            for (int i = 0; i < 5; i++) world.Step();
            Assert.True(world.GetView(1).Squads[1].Position.X > empty.X);
            Assert.True(Vector2.Distance(empty, world.GetView(1).BasePosition) > Rules().BaseRadius);
            world.TryEnqueue(1, SimCommand.Transfer(2, 1, 2, 1)); world.Step();
            empty = world.GetView(1).Squads[1].Position;
            world.TryEnqueue(1, SimCommand.Move(3, 2, Vector2.UnitY)); world.Step();
            Assert.True(world.GetView(1).Squads[1].Position.Y > empty.Y);
        }
        [Fact]
        public void GuardCapacityFailureDoesNotPartiallyTransfer()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 110)));
            int cap = Rules().GarrisonCap(1);
            for (int i = 0; i < cap; i++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(90 + i, 110), garrison: true));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 4, 1)); world.Step();
            Assert.Equal(cap, world.GetView(1).Squads[3].JoinedCount);
            Assert.Equal(1, world.GetView(1).Squads[0].JoinedCount);
            Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Detail == (int)CommandFailure.PopulationCap);
        }
        [Fact]
        public void RallyHasDestinationBeforeJoiningAndNeverDoubleCountsPopulation()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(115, 100), garrison: true));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Rally(1, 2, 1)); world.Step();
            var moving = Assert.Single(world.GetView(1).Units);
            Assert.True(moving.IsReinforcing);
            Assert.Equal((byte)0, moving.SquadId);
            Assert.Equal((byte)2, moving.DestinationSquadId);
            Assert.Equal(0, world.GetView(1).Squads[1].JoinedCount);
            Assert.Equal(1, world.GetView(1).Squads[1].ReinforcingCount);
            for (int i = 0; i < 100; i++) world.Step();
            var joined = Assert.Single(world.GetView(1).Units);
            Assert.False(joined.IsReinforcing);
            Assert.Equal((byte)2, joined.SquadId);
            Assert.Equal(1, world.GetView(1).Population);
            Assert.Equal(1, world.GetView(1).Squads[1].JoinedCount);
        }
        [Fact]
        public void EmptyFocusUsesBaseCameraAndGuardRemainsInsideRadiusAfterSeparation()
        {
            var setup = new WorldSetup { CameraVisibility = true };
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(500, 500)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(125, 100), garrison: true));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(125, 100), garrison: true));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.MoveTo(1, 7, new Vector2(500, 500)));
            world.TryEnqueue(1, SimCommand.Focus(2, 2)); world.Step();
            Assert.False(world.GetView(1).CanSeePoint(new Vector2(500, 500)));
            Assert.All(world.GetView(1).Units, unit => Assert.InRange(Vector2.Distance(unit.Position, new Vector2(100, 100)), 0, Rules().GarrisonRadius + 0.0001f));
        }
        [Fact]
        public void TransferOutsideBaseRejectsWholeCountAndCannotTakeOtherOwnersUnits()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 110)));
            setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200, 200), new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 150)));
            setup.Units.Add(new UnitSpawn(2, "water", new Vector2(100, 110)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 2, 2)); world.Step();
            Assert.Equal(2, world.GetView(1).Squads[0].JoinedCount);
            Assert.Equal(0, world.GetView(1).Squads[1].JoinedCount);
            Assert.Equal((byte)1, world.GetView(2).Units.Single(x => x.OwnerId == 2).SquadId);
            Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected);
        }
        [Fact]
        public void ReinforcementDestinationIsPrivateToItsOwner()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 110)));
            setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200, 200), new Vector2(112, 100)));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(115, 100), garrison: true));
            setup.Units.Add(new UnitSpawn(2, "water", new Vector2(112, 100)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Rally(1, 2, 1)); world.Step();
            Assert.Equal((byte)2, world.GetView(1).Units.Single(x => x.OwnerId == 1).DestinationSquadId);
            Assert.Equal((byte)0, world.GetView(2).Units.Single(x => x.OwnerId == 1).DestinationSquadId);
        }
        [Theory]
        [InlineData((byte)2)]
        [InlineData((byte)4)]
        public void ConstructionCannotCoverAnotherJoinedSquadsCenter(byte squadId)
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 110), 4, 100));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, squadId, 1)); world.Step();
            var json = Newtonsoft.Json.Linq.JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(world.CaptureMigrationState()));
            json["World"]!["Players"]![0]!["Squads"]![squadId - 1]!["Position"]!["X"] = 116;
            json["World"]!["Players"]![0]!["Squads"]![squadId - 1]!["Position"]!["Y"] = 100;
            json["World"]!["Units"]![0]!["Position"]!["X"] = 130;
            json["World"]!["Units"]![0]!["Position"]!["Y"] = 100;
            world = SimulationWorld.RestoreMigrationState(Rules(), json.ToObject<MigrationWorldState>()!);
            world.TryEnqueue(1, SimCommand.Build(2, BuildingKind.Collector, (int)(116 / Rules().BuildGridSize), (int)(100 / Rules().BuildGridSize)));
            world.Step();
            Assert.DoesNotContain(world.GetView(1).Buildings, x => x.Kind == BuildingKind.Collector);
            Assert.Equal(100, world.GetView(1).CoreBalance);
            Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Detail == (int)CommandFailure.InvalidPlacement);
        }
    }
}
