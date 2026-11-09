using System;
using System.Linq;
using System.Numerics;
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
        public void EmptySquadDoesNotMoveAndDepartureIsOutsideBase()
        {
            var world = World();
            world.TryEnqueue(1, SimCommand.MoveTo(1, 2, new Vector2(500, 500))); world.Step();
            Vector2 empty = world.GetView(1).Squads[1].Position;
            for (int i = 0; i < 5; i++) world.Step();
            Assert.Equal(empty, world.GetView(1).Squads[1].Position);
            Assert.True(Vector2.Distance(empty, world.GetView(1).BasePosition) > Rules().BaseRadius);
            world.TryEnqueue(1, SimCommand.Transfer(2, 1, 2, 1)); world.Step();
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
        [Fact]
        public void ConstructionCannotCoverAnotherJoinedSquadsCenter()
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 100), new Vector2(100, 110), 4, 100));
            setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 110)));
            var world = new SimulationWorld(Rules(), setup, 1);
            world.TryEnqueue(1, SimCommand.Transfer(1, 1, 2, 1)); world.Step();
            var json = Newtonsoft.Json.Linq.JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(world.CaptureMigrationState()));
            json["World"]!["Players"]![0]!["Squads"]![1]!["Position"]!["X"] = 116;
            json["World"]!["Players"]![0]!["Squads"]![1]!["Position"]!["Y"] = 100;
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
