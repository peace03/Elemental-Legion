using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Net;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>후보의 확보·구출과 예약 및 저장 경계를 실제 틱으로 확인한다.</summary>
public sealed class ConversionRecoveryTests
{
    [Fact]
    public void NeutralConversionStillJoinsImmediately()
    {
        var world = Create(neutral: true);
        world.Step();
        Assert.Empty(world.GetView(1).Candidates);
        Assert.Equal(1, CombatTests.Find(world, 2).OwnerId);
        Assert.Equal(160, CombatTests.Find(world, 2).Health);
    }

    [Fact]
    public void PvpConversionRequiresUncontested60Ticks()
    {
        var world = Create();
        world.Step();
        var candidate = Assert.Single(world.GetView(1).Candidates);
        Assert.Equal(2, candidate.Id);
        Assert.Equal(RecoveryRole.Secure, candidate.Role);
        Assert.Equal(0, candidate.ProgressTicks);
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
        Assert.Equal(1, world.GetView(1).Population);
        for (int tick = 0; tick < 59; tick++) world.Step();
        Assert.Equal(59, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
        world.Step();
        Assert.Empty(world.GetView(1).Candidates);
        Assert.Equal(1, CombatTests.Find(world, 2).OwnerId);
        Assert.Equal(80, CombatTests.Find(world, 2).Health);
        Assert.Equal(0, world.GetView(1).ReservedPopulation);
        Assert.Contains(world.GetView(1).Events, item => item.Kind == SimEventKind.Converted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyRescueKeepsThePriorOwnersTrainingBonus(bool rescue)
    {
        var world = Create();
        Set(Unit(world, 2), "TrainingAttackBonus", 0.25f);
        world.Step();
        Assert.Equal(0.25f, JObject.FromObject(world.CaptureCheckpoint())["Candidates"]![0]!["TrainingAttackBonus"]!.Value<float>());
        if (rescue)
        {
            AddRescuer(ref world);
            Move(world, 1, new Vector2(200));
        }
        else world = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());

        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.False(world.HasConversionCandidates);
        object unit = Unit(world, 2);
        Assert.Equal(rescue ? 2 : 1, (int)Get(unit, "OwnerId"));
        Assert.Equal(rescue ? 0.25f : 0f, (float)Get(unit, "TrainingAttackBonus"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RescueIsOnceAndTransfersReservation(bool migrate)
    {
        var world = Create(); world.Step();
        AddRescuer(ref world);
        Move(world, 1, new Vector2(200));
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.Empty(world.GetView(2).Candidates);
        var unit = CombatTests.Find(world, 2, 2);
        Assert.Equal(2, unit.OwnerId); Assert.Equal(40, unit.Health); Assert.False(unit.Converted);
        Assert.True((bool)JObject.FromObject(world.CaptureCheckpoint())["Units"]![1]!["RescuedOnce"]!);
        Assert.Equal(0, world.GetView(1).ReservedPopulation);
        Assert.Contains(world.GetView(2).Events, item => item.Kind == SimEventKind.Rescued);
        if (migrate)
        {
            world = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());
            Assert.True((bool)JObject.FromObject(world.CaptureCheckpoint())["Units"]![1]!["RescuedOnce"]!);
        }
        Move(world, 1, new Vector2(100));
        Kill(world, 2, 1, 1);
        world.Step();
        Assert.Equal(RecoveryRole.Observer, Assert.Single(world.GetView(2).Candidates).Role);
    }

    [Fact]
    public void EnemyEntryPausesAndOwnDepartureResetsProgress()
    {
        var world = Create(); world.Step();
        for (int tick = 0; tick < 10; tick++) world.Step();
        AddRescuer(ref world);
        for (int tick = 0; tick < 5; tick++) world.Step();
        Assert.Equal(10, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
        Move(world, 1, new Vector2(150)); world.Step();
        var json = JObject.FromObject(world.CaptureCheckpoint());
        Assert.Equal(0, json["Candidates"]![0]!["SecureProgress"]!.Value<int>());
        Assert.Equal(1, json["Candidates"]![0]!["RescueProgress"]!.Value<int>());
    }

    [Fact]
    public void ExpiryWinsAgainstCompletionOnTick400()
    {
        var world = Create(); world.Step();
        Move(world, 1, new Vector2(200));
        for (int tick = 0; tick < 340; tick++) world.Step();
        Move(world, 1, new Vector2(100));
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.False(world.HasConversionCandidates);
        Assert.Equal(0, world.Metrics.Conversions);
        Assert.Equal(1, world.Metrics.Drops);
        Assert.Equal(0, world.GetView(1).ReservedPopulation);
        world.Step(); Assert.Equal(1, world.Metrics.Drops);
    }

    [Fact]
    public void RecoveryCannotDuplicateAfterOwnerLoss()
    {
        var world = Create(); world.Step();
        Set(Player(world, 1), "BaseDestroyed", true); world.Step();
        Assert.False(world.TryGetView(1, out _)); Assert.True(world.HasConversionCandidates);
        var migrated = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());
        for (int tick = 0; tick < 402; tick++) migrated.Step();
        Assert.False(migrated.HasConversionCandidates);
        Assert.Equal(1, migrated.Metrics.Drops);
        Assert.Equal(0, migrated.Metrics.Conversions);
    }

    [Fact]
    public void RemovedCandidateParticipantIdCannotBeReusedBeforeExpiry()
    {
        var world = Create(); world.Step();
        Set(Player(world, 1), "BaseDestroyed", true); Set(Player(world, 2), "BaseDestroyed", true); world.Step();
        Assert.Equal(2, world.CaptureCheckpoint().MaxPlayerId);
        Assert.False(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(100, 120), new Vector2(100))));
        Assert.False(world.TryAddPlayer(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720))));
    }

    [Fact]
    public void MigrationAndDiskKeepProgressReservationsAndPendingCommands()
    {
        var world = Create(); world.Step();
        for (int tick = 0; tick < 19; tick++) world.Step();
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Focus(1, 1)));
        var migrated = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());
        var disk = SimulationWorld.RestoreCheckpoint(ReactionEconomyTests.CertainConversion(), world.CaptureCheckpoint());
        Assert.Equal(19, Assert.Single(disk.GetView(1).Candidates).ProgressTicks);
        Assert.Equal(1, disk.GetView(1).ReservedPopulation);
        Assert.True(disk.TryResumeGhost(1));
        for (int tick = 0; tick < 41; tick++) { world.Step(); migrated.Step(); disk.Step(); }
        Assert.True(JToken.DeepEquals(JObject.FromObject(world.CaptureMigrationState()), JObject.FromObject(migrated.CaptureMigrationState())));
        Assert.Empty(disk.GetView(1).Candidates);
        Assert.Equal(80, CombatTests.Find(disk, 2).Health);
    }

    [Theory]
    [InlineData("Id", 0)] [InlineData("Id", -1)] [InlineData("Element", 14)]
    [InlineData("SecureProgress", -1)] [InlineData("SecureProgress", 61)]
    [InlineData("RescueProgress", 1)] [InlineData("SecureSquad", 0)] [InlineData("SecureSquad", 5)]
    [InlineData("EndTick", 400)] [InlineData("Source", 0)]
    public void InvalidCandidateDtoIsRejected(string field, int value)
    {
        var world = Create(); world.Step();
        JObject json = JObject.FromObject(world.CaptureCheckpoint());
        json["Candidates"]![0]![field] = value;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(ReactionEconomyTests.CertainConversion(), json.ToObject<WorldCheckpoint>()!));
    }

    [Fact]
    public void MissingAndDuplicatedCandidatesAreRejected()
    {
        var world = Create(); world.Step();
        JObject json = JObject.FromObject(world.CaptureCheckpoint());
        ((JArray)json["Candidates"]!).Add(json["Candidates"]![0]!.DeepClone());
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(ReactionEconomyTests.CertainConversion(), json.ToObject<WorldCheckpoint>()!));
        json.Remove("Candidates");
        Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => json.ToObject<WorldCheckpoint>());
    }

    [Fact]
    public void ActualGuardLastHitReservesGuardCapacityBeforeTrainingAndTransfer()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 120), new Vector2(100), 1, 100, true));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720)));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100), garrison: true));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(101, 100), health: 1));
        int cap = GameRulesTests.Rules().GarrisonCap(1);
        for (int index = 1; index < cap - 1; index++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100 + index, 130), garrison: true));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100, 130)));
        var world = new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1); world.Step();
        Assert.Equal(1, world.GetView(1).Squads[3].ReservedPopulation);
        Assert.Equal(4, JObject.FromObject(world.CaptureCheckpoint())["Candidates"]![0]!["SecureSquad"]!.Value<int>());
        world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 4));
        world.TryEnqueue(1, SimCommand.Transfer(2, 1, 4, 1)); world.Step();
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
        Assert.Equal(2, world.GetView(1).Events.Count(item => item.Kind == SimEventKind.CommandRejected));
        Assert.Equal(1, SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState()).GetView(1).Squads[3].ReservedPopulation);
        for (int tick = 0; tick < 59; tick++) world.Step();
        Assert.Empty(world.GetView(1).Candidates);
        Assert.Equal((byte)4, CombatTests.Find(world, 2).SquadId);
        Assert.True(CombatTests.Find(world, 2).Garrison);
    }

    [Fact]
    public void DotKeepsGuardSquadEvenAfterTransferAndMigration()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "fire", new Vector2(100, 120), new Vector2(100), 4));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720)));
        setup.Units.Add(new UnitSpawn(1, "fire", new Vector2(100), garrison: true));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(101, 100), health: 30));
        var world = new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1); world.Step();
        Set(Unit(world, 1), "AttackRemaining", 100d);
        var burn = JObject.FromObject(world.CaptureCheckpoint())["Units"]![1]!["Statuses"]!.Single(status => status["Dps"]!.Value<float>() > 0);
        Assert.Equal(4, burn["SourceSquad"]!.Value<int>());
        Assert.Equal(-1, burn["LegionOwner"]!.Value<int>());
        Set(Unit(world, 2), "Health", 0.01f);
        world.TryEnqueue(1, SimCommand.Transfer(1, 4, 2, 1));
        world = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());
        world.Step();
        var candidate = Assert.Single(JObject.FromObject(world.CaptureCheckpoint())["Candidates"]!);
        Assert.Equal(4, candidate["SecureSquad"]!.Value<int>());
        Assert.Equal((byte)2, CombatTests.Find(world, 1).SquadId);
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.Equal(0, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OnlyPlayerContendersBlockRecovery(bool neutral)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(100, 120), new Vector2(100), 4));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720), 4));
        setup.Players.Add(new PlayerSpawn(3, "water", new Vector2(900), new Vector2(104, 100), 4));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(101, 100), health: 1));
        setup.Units.Add(new UnitSpawn(neutral ? -1 : 3, "life", new Vector2(104, 100)));
        var world = new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1); world.Step();
        Set(Unit(world, 1), "AttackRemaining", 100d); Set(Unit(world, 3), "AttackRemaining", 100d);
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.Equal(neutral ? 1 : 0, world.Metrics.Conversions);
        if (!neutral)
        {
            Assert.Equal(0, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
            var observed = Assert.Single(world.GetView(3).Candidates);
            Assert.Equal(RecoveryRole.Observer, observed.Role); Assert.Equal(0, observed.ProgressTicks);
        }
    }

    [Fact]
    public void HiddenCandidateKeepsOnlyOwnerReservationAndReturnsWhenVisible()
    {
        var world = Create(); world.Step();
        var buffer = world.GetView(1).Candidates;
        Move(world, 1, new Vector2(300)); world.Step();
        Assert.Empty(world.GetView(1).Candidates); Assert.Same(buffer, world.GetView(1).Candidates);
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
        Move(world, 1, new Vector2(100)); world.Step();
        Assert.Equal(1, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
    }

    [Fact]
    public void CandidatePopulationReservationRejectsTrainingWithoutSpending()
    {
        var world = Create(); world.Step();
        Set(Player(world, 1), "PopulationCap", 2);
        long cores = world.GetView(1).CoreBalance;
        world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1)); world.Step();
        Assert.Equal(cores, world.GetView(1).CoreBalance);
        Assert.False(world.GetView(1).IsTraining); Assert.Equal(1, world.GetView(1).ReservedPopulation);
        Assert.Contains(world.GetView(1).Events, item => item.Kind == SimEventKind.CommandRejected && item.Detail == (int)CommandFailure.PopulationCap);
    }

    [Fact]
    public void DestroyedLastHitSquadCannotBeReplacedByAnotherOwnSquad()
    {
        var world = Create(); world.Step(); AddRescuer(ref world, 1);
        object substitute = Unit(world, 3); Set(substitute, "SquadId", (byte)2);
        Move(world, 3, new Vector2(102, 100));
        Kill(world, 1, 2, 1); Set(Unit(world, 1), "KillerTower", true);
        for (int tick = 0; tick < 61; tick++) world.Step();
        Assert.Equal(0, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
        Assert.Equal(0, world.Metrics.Conversions);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void EliminatedSquadNeverRegainsCandidateAuthorityAfterRefillOrRestore(int restore)
    {
        var world = Create(); world.Step(); AddRescuer(ref world, 1);
        Set(Unit(world, 3), "SquadId", (byte)2); Move(world, 3, new Vector2(102, 100));
        Kill(world, 1, 2, 1); Set(Unit(world, 1), "KillerTower", true); world.Step();
        if (restore == 1) world = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());
        if (restore == 2)
        {
            world = SimulationWorld.RestoreCheckpoint(ReactionEconomyTests.CertainConversion(), world.CaptureCheckpoint());
            Assert.True(world.TryResumeGhost(1));
        }
        Move(world, 3, new Vector2(300, 320));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Transfer(1, 2, 1, 1))); world.Step();
        Assert.Equal((byte)1, CombatTests.Find(world, 3).SquadId);
        Move(world, 3, new Vector2(102, 100));
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.True(world.HasConversionCandidates);
        Assert.Equal(0, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
        Assert.Equal(0, world.Metrics.Conversions);
    }

    [Fact]
    public void TrainingReinforcementDoesNotRestoreAnEliminatedSquadsAuthority()
    {
        var world = Create(); world.Step();
        Kill(world, 1, 2, 1); Set(Unit(world, 1), "KillerTower", true); world.Step();
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1))); world.Step();
        for (int tick = 0; tick < 200 && world.GetView(1).IsTraining; tick++) world.Step();
        var reinforcement = Assert.Single(world.GetView(1).Units, unit => unit.OwnerId == 1);
        Assert.True(reinforcement.IsReinforcing);
        // 빈 분대의 원격 중심은 유지된다. 이 검사는 보행 시간과 분리해 도착 뒤의 확보 권한만 확인한다.
        Set(Unit(world, reinforcement.Id), "Position", world.GetView(1).Squads[0].Position);
        Set(Unit(world, reinforcement.Id), "AttackRemaining", 100d);
        world.Step();
        reinforcement = Assert.Single(world.GetView(1).Units, unit => unit.OwnerId == 1);
        Assert.False(reinforcement.IsReinforcing); Assert.Equal((byte)1, reinforcement.SquadId);
        Move(world, reinforcement.Id, new Vector2(102, 100));
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.True(world.HasConversionCandidates);
        Assert.Equal(0, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
    }

    [Fact]
    public void TransferBeforeEliminationKeepsTheLivingSquadsAuthority()
    {
        var world = Create(); world.Step(); AddRescuer(ref world, 1);
        Set(Unit(world, 3), "SquadId", (byte)2); Move(world, 3, new Vector2(300, 320));
        world.TryEnqueue(1, SimCommand.Transfer(1, 2, 1, 1)); world.Step();
        Assert.Equal((byte)1, CombatTests.Find(world, 3).SquadId);
        Move(world, 3, new Vector2(102, 100));
        Kill(world, 1, 2, 1); Set(Unit(world, 1), "KillerTower", true);
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.False(world.HasConversionCandidates); Assert.Equal(1, world.Metrics.Conversions);
    }

    [Fact]
    public void MissingAuthorityMarkerAndLostAuthorityWithProgressAreRejected()
    {
        var world = Create(); world.Step(); world.Step();
        JObject json = JObject.FromObject(world.CaptureCheckpoint());
        json["Candidates"]![0]!["SecureAuthorityLost"] = true;
        Assert.Throws<ArgumentException>(() => SimulationWorld.RestoreCheckpoint(ReactionEconomyTests.CertainConversion(), json.ToObject<WorldCheckpoint>()!));
        ((JObject)json["Candidates"]![0]!).Remove("SecureAuthorityLost");
        Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => json.ToObject<WorldCheckpoint>());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ReinforcementKillReservesEmptyDestinationUntilOriginalSourceJoins(int restore)
    {
        var world = ReinforcementWorld(); world.Step();
        Assert.Equal(1, world.GetView(1).Squads[1].ReservedPopulation);
        Assert.Equal(2, JObject.FromObject(world.CaptureCheckpoint())["Candidates"]![0]!["SecureSquad"]!.Value<int>());
        for (int tick = 0; tick < 10; tick++) world.Step();
        Assert.Equal(0, Assert.Single(world.GetView(1).Candidates).ProgressTicks);
        Assert.False(JObject.FromObject(world.CaptureCheckpoint())["Candidates"]![0]!["SecureAuthorityLost"]!.Value<bool>());
        world = Restore(world, restore);
        using var receiver = new GameplayReplica(ReactionEconomyTests.CertainConversion());
        var frame = RecoveryProtocolTests.Publish(ReactionEconomyTests.CertainConversion(), world.GetView(1), receiver);
        Assert.Equal(1, frame.ReservedPopulation);
        Assert.Equal(frame.ReservedPopulation, frame.Squads.ToArray().Sum(squad => squad.ReservedPopulation));
        Assert.Equal(1, frame.Squads[1].ReservedPopulation); Assert.Empty(frame.TrainingQueue);
        Vector2 candidate = JObject.FromObject(world.CaptureCheckpoint())["Candidates"]![0]!["Position"]!.ToObject<Vector2>();
        Set(Unit(world, 1), "Position", world.GetView(1).Squads[1].Position);
        Set(Unit(world, 1), "AttackRemaining", 100d); world.Step();
        Assert.False(CombatTests.Find(world, 1).IsReinforcing); Assert.Equal((byte)2, CombatTests.Find(world, 1).SquadId);
        Move(world, 1, candidate + Vector2.UnitX);
        long attacks = world.Metrics.Attacks;
        for (int tick = 0; tick < 60; tick++) world.Step();
        var converted = CombatTests.Find(world, 2);
        Assert.Equal((byte)2, converted.SquadId); Assert.False(converted.IsReinforcing);
        Assert.Equal(80, converted.Health); Assert.Equal(attacks, world.Metrics.Attacks);
        Assert.False(world.HasConversionCandidates);
        AddRescuer(ref world);
        Move(world, 3, converted.Position + Vector2.UnitX * 0.5f); world.Step();
        Assert.Contains(world.GetView(1).Events, item => item.Kind == SimEventKind.Attack && item.SourceId == 2);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ReinforcementDotKeepsItsOriginalDestinationAfterRetargetAndRestore(int restore)
    {
        var world = ReinforcementWorld("fire", health: 30); world.Step();
        var burn = JObject.FromObject(world.CaptureCheckpoint())["Units"]![1]!["Statuses"]!.Single(status => status["Dps"]!.Value<float>() > 0);
        Assert.Equal(2, burn["SourceSquad"]!.Value<int>()); Assert.Equal(-1, burn["LegionOwner"]!.Value<int>());
        Set(Unit(world, 1), "DestinationSquadId", (byte)3); Set(Unit(world, 1), "AttackRemaining", 100d);
        Set(Unit(world, 2), "Health", 0.01f);
        world = Restore(world, restore); world.Step();
        var candidate = Assert.Single(JObject.FromObject(world.CaptureCheckpoint())["Candidates"]!);
        Assert.Equal(2, candidate["SecureSquad"]!.Value<int>());
        Assert.Equal(1, world.GetView(1).Squads[1].ReservedPopulation); Assert.Equal(0, world.GetView(1).Squads[2].ReservedPopulation);
        Assert.True(candidate["SecureAuthorityLost"]!.Value<bool>());
        using var receiver = new GameplayReplica(ReactionEconomyTests.CertainConversion());
        var frame = RecoveryProtocolTests.Publish(ReactionEconomyTests.CertainConversion(), world.GetView(1), receiver);
        Assert.Equal(1, frame.ReservedPopulation); Assert.Equal(1, frame.Squads[1].ReservedPopulation);
    }

    [Fact]
    public void ReinforcementNeutralKillJoinsTheDestinationImmediately()
    {
        var world = ReinforcementWorld(neutral: true); world.Step();
        Assert.Empty(world.GetView(1).Candidates);
        var converted = CombatTests.Find(world, 2);
        Assert.Equal((byte)2, converted.SquadId); Assert.Equal(160, converted.Health);
        Assert.False(converted.IsReinforcing); Assert.False(converted.Garrison);
    }

    [Fact]
    public void ReinforcementStatusDoesNotRetroactivelyJoinDestinationSynergy()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(300), new Vector2(100), 4));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720), 4));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(103, 100)));
        setup.Units.Add(new UnitSpawn(1, "lightning", new Vector2(100)));
        var world = new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1);
        Set(Unit(world, 1), "SquadId", (byte)0); Set(Unit(world, 1), "DestinationSquadId", (byte)2); Set(Unit(world, 1), "IsReinforcing", true);
        Set(Unit(world, 3), "SquadId", (byte)2); Move(world, 3, new Vector2(100));
        world.Step(); Assert.Equal(0, world.Metrics.Reactions[(int)ReactionKind.Electrocute]);
        Assert.False(CombatTests.Find(world, 1).IsReinforcing);
        Set(Unit(world, 1), "AttackRemaining", 100d); Set(Unit(world, 3), "AttackRemaining", 0d);
        world = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());
        world.Step(); Assert.Equal(0, world.Metrics.Reactions[(int)ReactionKind.Electrocute]);
    }

    private static SimulationWorld Restore(SimulationWorld world, int restore)
    {
        if (restore == 1) return SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());
        if (restore != 2) return world;
        world = SimulationWorld.RestoreCheckpoint(ReactionEconomyTests.CertainConversion(), world.CaptureCheckpoint());
        Assert.True(world.TryResumeGhost(1)); return world;
    }

    private static SimulationWorld ReinforcementWorld(string element = "water", bool neutral = false, float health = 1)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(300), new Vector2(100), 4));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720), 4));
        setup.Units.Add(new UnitSpawn(1, element, new Vector2(100)));
        setup.Units.Add(new UnitSpawn(neutral ? -1 : 2, "earth", new Vector2(101, 100), health));
        var world = new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1);
        Set(Unit(world, 1), "SquadId", (byte)0); Set(Unit(world, 1), "DestinationSquadId", (byte)2);
        Set(Unit(world, 1), "IsReinforcing", true); return world;
    }

    [Fact]
    public void RemovedSecureOwnerStillAllowsRescueAndReleasesGlobalReservationOnce()
    {
        var world = Create(); world.Step(); AddRescuer(ref world);
        Set(Player(world, 1), "BaseDestroyed", true);
        for (int tick = 0; tick < 60; tick++) world.Step();
        Assert.False(world.HasConversionCandidates);
        Assert.Equal(2, CombatTests.Find(world, 2, 2).OwnerId);
        Assert.Equal(0, (int)State(world).GetType().GetProperty("ReservedUnitSlots", Internal)!.GetValue(State(world))!);
        world.Step(); Assert.Equal(0, world.Metrics.Drops);
    }

    [Fact]
    public void RescueRechecksPopulationAndDoesNotConsumeTheCandidate()
    {
        var world = Create(); world.Step(); AddRescuer(ref world);
        Move(world, 1, new Vector2(200)); Set(Player(world, 2), "PopulationCap", 1);
        for (int tick = 0; tick < 61; tick++) world.Step();
        Assert.True(world.HasConversionCandidates);
        Assert.Equal(60, Assert.Single(world.GetView(2).Candidates).ProgressTicks);
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
        Assert.Equal(1, world.GetView(2).Population);
        Set(Player(world, 2), "PopulationCap", 2); world.Step();
        Assert.False(world.HasConversionCandidates); Assert.Equal(2, world.GetView(2).Population);
    }

    [Fact]
    public void LastGlobalSlotRemainsReservedAcrossMigrationAndRejectsTraining()
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(300), new Vector2(100), 4, 100, true));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720), 4));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        setup.Units.Add(new UnitSpawn(2, "earth", new Vector2(101, 100), health: 1));
        for (int index = 2; index < Protocol.MaxUnits; index++) setup.Units.Add(new UnitSpawn(-1, "earth", new Vector2(1500)));
        var world = new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1); world.Step();
        world = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), world.CaptureMigrationState());
        world.TryEnqueue(1, SimCommand.Train(1, "water", 1, 1)); world.Step();
        Assert.Equal(1, world.GetView(1).ReservedPopulation); Assert.False(world.GetView(1).IsTraining);
        Assert.Equal(Protocol.MaxUnits - 1, JObject.FromObject(world.CaptureCheckpoint())["Units"]!.Count());
        Assert.Equal(1, (int)State(world).GetType().GetProperty("ReservedUnitSlots", Internal)!.GetValue(State(world))!);
    }

    private static void AddRescuer(ref SimulationWorld world, int owner = 2)
    {
        JObject json = JObject.FromObject(world.CaptureMigrationState());
        var units = (JArray)json["World"]!["Units"]!;
        JObject unit = (JObject)units[0]!.DeepClone(); unit["Id"] = 3; unit["Owner"] = owner;
        unit["Element"] = GameRulesTests.Rules().ElementIndex("life");
        unit["Position"] = JObject.FromObject(new Vector2(102, 100)); unit["Health"] = 70; unit["AttackRemaining"] = 100;
        units.Add(unit); json["World"]!["NextId"] = 4;
        var runtime = (JObject)json["Units"]![0]!.DeepClone(); runtime["Id"] = 3;
        runtime["Ghost"] = (long)Get(Player(world, owner), "GhostEndTick") != 0;
        ((JArray)json["Units"]!).Add(runtime);
        world = SimulationWorld.RestoreMigrationState(ReactionEconomyTests.CertainConversion(), json.ToObject<MigrationWorldState>()!);
        Move(world, 3, new Vector2(102, 100));
        Set(Unit(world, 1), "AttackRemaining", 100d);
    }

    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object State(SimulationWorld world) => typeof(SimulationWorld).GetField("state", Internal)!.GetValue(world)!;
    private static object Player(SimulationWorld world, int id) => ((System.Collections.IEnumerable)State(world).GetType().GetField("Players", Internal)!.GetValue(State(world))!).Cast<object>().Single(player => (int)Get(player, "Id") == id);
    private static object Unit(SimulationWorld world, int id) => ((System.Collections.IEnumerable)State(world).GetType().GetField("Units", Internal)!.GetValue(State(world))!).Cast<object>().Single(unit => (int)Get(unit, "Id") == id);
    private static object Get(object target, string field) => target.GetType().GetField(field, Internal)!.GetValue(target)!;
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Internal)!.SetValue(target, value);
    internal static void Move(SimulationWorld world, int id, Vector2 position)
    {
        object unit = Unit(world, id); Set(unit, "Position", position); Set(unit, "AttackRemaining", 100d);
        object player = Player(world, (int)Get(unit, "OwnerId"));
        object squad = ((Array)Get(player, "Squads")).GetValue((byte)Get(unit, "SquadId") - 1)!;
        Set(squad, "Position", position); Set(squad, "Destination", position);
    }
    private static void Kill(SimulationWorld world, int id, int owner, byte squad)
    {
        object unit = Unit(world, id); Set(unit, "Health", 0f); Set(unit, "Dead", true);
        Set(unit, "KillerOwner", owner); Set(unit, "KillerSource", 1); Set(unit, "KillerSquad", squad);
    }

    internal static SimulationWorld Create(bool neutral = false)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(300), new Vector2(100), 4, 100, true));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(720), 4));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(100)));
        setup.Units.Add(new UnitSpawn(neutral ? -1 : 2, "earth", new Vector2(101, 100), health: 1));
        return new SimulationWorld(ReactionEconomyTests.CertainConversion(), setup, 1);
    }
}
