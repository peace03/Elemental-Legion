using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class RecallTests
{
    private static GameRules Rules(int capacity = 50)
    {
        var json = GameRulesTests.Data().Root;
        json["population"]!["max"] = capacity;
        json["baseLevels"]![3]!["populationCap"] = capacity;
        return new GameRules(new GameDataDocument(json.ToString()));
    }

    private static SimulationWorld World(GameRules rules, int count = 1, Vector2? home = null, bool forges = false)
    {
        Vector2 origin = home ?? new Vector2(100);
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", origin, new Vector2(200), baseLevel: 4,
            natureForge: forges, energyForge: forges, cores: 10000));
        for (int i = 0; i < count; i++) setup.Units.Add(new UnitSpawn(1, "water", new Vector2(200 + i % 10 * 2, 200 + i / 10 * 2)));
        return new SimulationWorld(rules, setup, 31);
    }

    [Theory]
    [InlineData(1, 100, 100)]
    [InlineData(30, 100, 100)]
    [InlineData(100, 100, 100)]
    [InlineData(100, 0, 0)]
    [InlineData(100, 1000, 1000)]
    public void RecallLayoutIsFixedLegalAndPreservesFacilities(int count, float x, float z)
    {
        var rules = Rules(100);
        var world = World(rules, count, new Vector2(x, z), forges: true);
        var view = world.GetView(1);
        var slots = view.RecallArrivalSlots.ToArray();
        Assert.Equal(rules.MaxPopulation, slots.Length);
        Assert.Equal(3, view.Buildings.Count);
        Assert.Equal(new Vector2(x, z), view.Buildings.Single(b => b.Kind == BuildingKind.Base).Position);
        Assert.Equal(Vector2.Clamp(new Vector2(x + rules.BaseRadius + rules.NatureForgeRadius + rules.SlotSpacing, z),
            Vector2.Zero, new Vector2(rules.MapSize)), view.Buildings.Single(b => b.Kind == BuildingKind.NatureForge).Position);
        Assert.Equal(Vector2.Clamp(new Vector2(x - rules.BaseRadius - rules.EnergyForgeRadius - rules.SlotSpacing, z),
            Vector2.Zero, new Vector2(rules.MapSize)), view.Buildings.Single(b => b.Kind == BuildingKind.EnergyForge).Position);
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.InRange(slots[i].X, 0, rules.MapSize);
            Assert.InRange(slots[i].Y, 0, rules.MapSize);
            Assert.InRange(Vector2.Distance(slots[i], view.BasePosition), rules.BaseRadius, rules.BuildRadius);
            foreach (var building in view.Buildings)
                Assert.True(Vector2.Distance(slots[i], building.Position) >= building.Radius);
            for (int j = 0; j < i; j++) Assert.True(Vector2.Distance(slots[i], slots[j]) >= rules.SlotSpacing - 0.0001f);
            if (i > 0) Assert.True(Vector2.DistanceSquared(slots[i - 1], view.BasePosition) <= Vector2.DistanceSquared(slots[i], view.BasePosition));
        }
        world.Step();
        Assert.Equal(slots, world.GetView(1).RecallArrivalSlots);
        Assert.Equal(slots, World(rules, count, new Vector2(x, z), forges: true).GetView(1).RecallArrivalSlots);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(100)]
    public void RecallIsAtomicAndUsesSharedCooldown(int count)
    {
        var rules = Rules(100);
        var world = World(rules, count);
        var statusUnit = State(world).Units[0];
        statusUnit.Shield = 7;
        statusUnit.Statuses[(int)StatusKind.Chill] = new TimedStatus { Expires = 100, Amount = -0.5f };
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Recall(1, 1)));
        world.Step();
        long start = world.GetView(1).Tick;
        var before = world.GetView(1).Units.ToArray();
        Assert.Equal((byte)1, world.GetView(1).RecallSquad);
        Assert.Equal(start + 60, world.GetView(1).RecallEndTick);
        for (int i = 0; i < 59; i++) world.Step();
        Assert.Equal(before.Select(x => x.Position), world.GetView(1).Units.Select(x => x.Position));
        Assert.Equal(0, world.GetView(1).RecallReadyTick);
        world.Step();
        var after = world.GetView(1);
        Assert.Equal((byte)0, after.RecallSquad);
        Assert.Equal(0, after.RecallEndTick);
        Assert.Equal(after.Tick + 1200, after.RecallReadyTick);
        Assert.Equal(before.Select(x => x.Id), after.Units.Select(x => x.Id));
        Assert.Equal(before.Select(x => x.Health), after.Units.Select(x => x.Health));
        Assert.Equal(before.Select(x => x.Shield), after.Units.Select(x => x.Shield));
        Assert.Equal(before.Select(x => x.StatusMask), after.Units.Select(x => x.StatusMask));
        Assert.All(after.Units, x => Assert.Contains(x.Position, after.RecallArrivalSlots));
        Assert.All(after.Units, x => Assert.Equal(1U, x.MovementRevision));
        Assert.Equal(1U, after.Squads[0].MovementRevision);
        Assert.Contains(after.Squads[0].Position, after.RecallArrivalSlots);
        Assert.True(Vector2.Distance(after.Squads[0].Position, after.BasePosition) > rules.BaseRadius);
        world.TryEnqueue(1, SimCommand.Recall(2, 1)); world.Step();
        Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Detail == (int)CommandFailure.Busy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseAndOtherSquadCommandsDoNotCancelButCasterMovementDoes(bool moveTo)
    {
        var world = World(Rules());
        world.TryEnqueue(1, SimCommand.Recall(1, 1));
        world.TryEnqueue(1, SimCommand.Move(2, Vector2.Zero));
        world.TryEnqueue(1, SimCommand.Move(3, 2, Vector2.UnitX));
        world.TryEnqueue(1, SimCommand.Focus(4, 4));
        world.Step();
        Assert.Equal((byte)1, world.GetView(1).RecallSquad);
        world.TryEnqueue(1, moveTo ? SimCommand.MoveTo(5, 1, new Vector2(200)) : SimCommand.Move(5, Vector2.UnitX));
        world.Step();
        Assert.Equal((byte)0, world.GetView(1).RecallSquad);
        var failure = Assert.Single(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected);
        Assert.Equal((int)CommandFailure.RecallInterrupted, failure.Detail);
        Assert.Equal(1UL, failure.Sequence);
        Assert.Equal(0, world.GetView(1).RecallReadyTick);
    }

    private static WorldState State(SimulationWorld world) => (WorldState)typeof(SimulationWorld)
        .GetField("state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(100)]
    public void BlockedRecallNeverPartiallyMovesSquad(int count)
    {
        var rules = Rules(count == 100 ? 100 : 50);
        var world = World(rules, count);
        var state = State(world);
        for (int i = 0; i <= rules.MaxPopulation - count; i++)
            state.AddUnit(-1, rules.ElementIndex("water"), world.GetView(1).RecallArrivalSlots[i]);
        world.TryEnqueue(1, SimCommand.Recall(7, 1)); world.Step();
        var before = world.GetView(1).Units.Where(x => x.OwnerId == 1).ToArray();
        for (int i = 0; i < 60; i++) world.Step();
        var after = world.GetView(1);
        Assert.Equal(before.Select(x => x.Position), after.Units.Where(x => x.OwnerId == 1).Select(x => x.Position));
        Assert.All(after.Units.Where(x => x.OwnerId == 1), x => Assert.Equal(0U, x.MovementRevision));
        Assert.Equal(0, after.RecallReadyTick);
        var failure = Assert.Single(after.Events, x => x.Kind == SimEventKind.CommandRejected);
        Assert.Equal((int)CommandFailure.InvalidPlacement, failure.Detail);
        Assert.Equal(7UL, failure.Sequence);
        Assert.Equal(0, failure.SourceId); Assert.Equal(0, failure.TargetId);
    }

    [Theory]
    [InlineData(-1, 10, false, true)]
    [InlineData(-1, 10, true, true)]
    [InlineData(1, 10, false, false)]
    [InlineData(-1, 0, false, false)]
    public void OnlyPositiveEnemyOrNeutralHpOrShieldDamageInterrupts(int owner, float damage, bool shield, bool interrupted)
    {
        var world = World(Rules());
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        var state = State(world);
        var target = state.Units.Single();
        if (shield) target.Shield = 100;
        float beforeHealth = target.Health;
        CombatSystem.Damage(state, target, damage, owner, 0, target.Element);
        VisibilitySystem.Refresh(state);
        Assert.Equal(interrupted ? (byte)0 : (byte)1, world.GetView(1).RecallSquad);
        if (shield) Assert.Equal(beforeHealth, target.Health);
        if (interrupted)
            Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Sequence == 1 && x.Detail == (int)CommandFailure.RecallInterrupted);
    }

    [Fact]
    public void DamageAndBaseLossWinOnCompletionTick()
    {
        foreach (bool baseLoss in new[] { false, true })
        {
            var rules = Rules();
            var world = World(rules);
            world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
            for (int i = 0; i < 59; i++) world.Step();
            var state = State(world);
            if (baseLoss)
            {
                Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "water", new Vector2(500), new Vector2(500, 510))));
                var attacker = state.Units.First(x => x.OwnerId == 2);
                attacker.SquadId = 2; attacker.Position = new Vector2(100, 107);
                state.PlayerById[2].Squads[1].Position = attacker.Position;
                var home = state.Buildings.Single(x => x.OwnerId == 1);
                home.Health = 1;
                Assert.True(SiegeSystem.InRange(attacker.Position, home, rules.Elements[attacker.Element].Range));
            }
            else
            {
                var unit = state.Units.Single();
                unit.Health = 1;
                unit.Statuses[(int)StatusKind.Burn] = new TimedStatus
                { Expires = state.Time + 2, Dps = 1000, Owner = -1, Source = 0, Element = unit.Element };
            }
            world.Step();
            if (baseLoss) Assert.False(world.TryGetView(1, out _));
            else
            {
                Assert.DoesNotContain(world.GetView(1).Units, x => x.OwnerId == 1);
                Assert.Equal((byte)0, world.GetView(1).RecallSquad);
                Assert.Equal(0, world.GetView(1).RecallReadyTick);
            }
            Assert.All(state.Units, unit => Assert.Equal(0U, unit.MovementRevision));
        }
    }

    [Fact]
    public void CastingStopsAttacksAndPursuitButPreservesPassiveAndStatusTimers()
    {
        var world = World(Rules());
        var state = State(world);
        var caster = state.Units.Single();
        var neutral = state.AddUnit(-1, caster.Element, caster.Position + new Vector2(0, 8));
        caster.Target = neutral;
        caster.Statuses[(int)StatusKind.Chill] = new TimedStatus { Expires = 1, Amount = -0.5f };
        caster.Shield = 7;
        Vector2 position = caster.Position;
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        for (int i = 0; i < 22; i++) world.Step();
        Assert.Equal(position, caster.Position);
        Assert.Null(caster.Target); Assert.Null(caster.BuildingTarget);
        Assert.Equal(7, caster.Shield);
        Assert.Equal(0, world.GetView(1).Units.Single(x => x.OwnerId == 1).StatusMask);
        Assert.DoesNotContain(world.GetView(1).Events, x => x.Kind == SimEventKind.Attack && x.OwnerId == 1);
    }

    [Fact]
    public void OneCastAndCooldownAreSharedAcrossSquadsAndDisconnectKeepsCooldown()
    {
        var rules = Rules();
        var world = World(rules, 2);
        var state = State(world);
        state.Units[1].SquadId = 2;
        state.Players[0].Squads[1].Position = new Vector2(210);
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.TryEnqueue(1, SimCommand.Recall(2, 2)); world.Step();
        Assert.Equal((byte)1, world.GetView(1).RecallSquad);
        Assert.Contains(world.GetView(1).Events, x => x.Sequence == 2 && x.Detail == (int)CommandFailure.Busy);
        world.StopPlayer(1);
        Assert.Equal((byte)0, world.GetView(1).RecallSquad);
        world.TryEnqueue(1, SimCommand.Recall(3, 2)); world.Step();
        for (int i = 0; i < 60; i++) world.Step();
        long ready = world.GetView(1).RecallReadyTick;
        Assert.True(ready > world.GetView(1).Tick);
        world.BeginGhost(1);
        Assert.True(world.TryResumeGhost(1));
        Assert.Equal(ready, world.GetView(1).RecallReadyTick);
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        Assert.Contains(world.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Detail == (int)CommandFailure.Busy);
    }

    [Fact]
    public void CompletionChoosesCurrentJoinedSurvivorsAndExcludesTravellingReinforcements()
    {
        var rules = Rules();
        var world = World(rules, 3);
        var state = State(world);
        var pending = state.Units[2];
        pending.SquadId = 0; pending.DestinationSquadId = 1; pending.IsReinforcing = true; pending.Garrison = true;
        pending.Position = new Vector2(100, 110);
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        for (int i = 0; i < 59; i++) world.Step();
        var departed = state.Units[1];
        departed.SquadId = 2;
        var joined = state.AddUnit(1, pending.Element, new Vector2(215));
        joined.SquadId = 1;
        Vector2 pendingPosition = pending.Position;
        world.Step();
        Assert.Equal(1U, state.Units[0].MovementRevision);
        Assert.Equal(1U, joined.MovementRevision);
        Assert.Equal(0U, departed.MovementRevision);
        Assert.Equal(0U, pending.MovementRevision);
        Assert.True(Vector2.Distance(pendingPosition, pending.Position) < 2);
    }

    [Fact]
    public void RecallMigrationKeepsPendingCommandsCastAtFiftyNineAndCooldownExactly()
    {
        var rules = Rules();
        var world = World(rules, 3);
        world.TryEnqueue(1, SimCommand.Recall(1, 1));
        var queued = SimulationWorld.RestoreMigrationState(rules,
            JsonConvert.DeserializeObject<MigrationWorldState>(JsonConvert.SerializeObject(world.CaptureMigrationState()))!);
        for (int i = 0; i < 60; i++) { world.Step(); queued.Step(); }
        Assert.Equal(world.GetView(1).Tick + 1, world.GetView(1).RecallEndTick);
        Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(queued.CaptureMigrationState()));
        var casting = SimulationWorld.RestoreMigrationState(rules,
            JsonConvert.DeserializeObject<MigrationWorldState>(JsonConvert.SerializeObject(world.CaptureMigrationState()))!);
        for (int i = 0; i < 5; i++)
        {
            world.Step(); queued.Step(); casting.Step();
            Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(casting.CaptureMigrationState()));
            Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(queued.CaptureMigrationState()));
        }
        var cooldown = SimulationWorld.RestoreMigrationState(rules,
            JsonConvert.DeserializeObject<MigrationWorldState>(JsonConvert.SerializeObject(world.CaptureMigrationState()))!);
        long ready = cooldown.GetView(1).RecallReadyTick;
        while (world.GetView(1).Tick < ready - 1) { world.Step(); cooldown.Step(); }
        world.TryEnqueue(1, SimCommand.Recall(2, 1)); cooldown.TryEnqueue(1, SimCommand.Recall(2, 1));
        world.Step(); cooldown.Step();
        Assert.Equal((byte)1, cooldown.GetView(1).RecallSquad);
        Assert.Equal(JsonConvert.SerializeObject(world.CaptureMigrationState()), JsonConvert.SerializeObject(cooldown.CaptureMigrationState()));
    }

    [Fact]
    public void DiskGhostCancelsCastKeepsFixedLayoutAndEarnedCooldown()
    {
        var rules = Rules();
        var world = World(rules);
        var layout = world.GetView(1).RecallArrivalSlots.ToArray();
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        var disk = SimulationWorld.RestoreCheckpoint(rules,
            JsonConvert.DeserializeObject<WorldCheckpoint>(JsonConvert.SerializeObject(world.CaptureCheckpoint()))!);
        Assert.Equal((byte)0, disk.GetView(1).RecallSquad);
        Assert.Equal(layout, disk.GetView(1).RecallArrivalSlots);
        Assert.True(disk.TryResumeGhost(1));
        disk.TryEnqueue(1, SimCommand.Recall(1, 1)); disk.Step();
        for (int i = 0; i < 60; i++) disk.Step();
        long ready = disk.GetView(1).RecallReadyTick;
        var saved = disk.CaptureCheckpoint();
        var restored = SimulationWorld.RestoreCheckpoint(rules, saved);
        Assert.Equal(ready, restored.GetView(1).RecallReadyTick);
        Assert.Equal(layout, restored.GetView(1).RecallArrivalSlots);
        Assert.True(restored.TryResumeGhost(1));
        restored.TryEnqueue(1, SimCommand.Recall(1, 1)); restored.Step();
        Assert.Contains(restored.GetView(1).Events, x => x.Kind == SimEventKind.CommandRejected && x.Detail == (int)CommandFailure.Busy);
        Assert.Equal(layout, restored.GetView(1).RecallArrivalSlots);
    }

    [Fact]
    public void RestoreRejectsOldSchemaMissingTamperedOversizedOrBlockedRecallState()
    {
        var rules = Rules();
        var world = World(rules);
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        var source = JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState()));
        void Reject(Action<JObject> change)
        {
            var copy = (JObject)source.DeepClone(); change(copy);
            Assert.ThrowsAny<ArgumentException>(() => SimulationWorld.RestoreMigrationState(rules, copy.ToObject<MigrationWorldState>()!));
        }
        foreach (string field in new[] { "RecallSquad", "RecallEndTick", "RecallReadyTick", "RecallSequence", "RecallArrivalSlots" })
            Reject(json => ((JObject)json["World"]!["Players"]![0]!).Remove(field));
        Reject(json => json["World"]!["SchemaVersion"] = 2);
        Reject(json => json["World"]!["SchemaVersion"] = 1);
        Reject(json => json["World"]!["Players"]![0]!["RecallSquad"] = 4);
        Reject(json => json["World"]!["Players"]![0]!["RecallEndTick"] = 0);
        Reject(json => json["World"]!["Players"]![0]!["RecallReadyTick"] = 5000);
        Reject(json => json["World"]!["Players"]![0]!["RecallSequence"] = 2);
        Reject(json => ((JArray)json["World"]!["Players"]![0]!["RecallArrivalSlots"]!).RemoveAt(0));
        Reject(json => ((JArray)json["World"]!["Players"]![0]!["RecallArrivalSlots"]!).Add(JObject.FromObject(new Vector2(150))));
        Reject(json => json["World"]!["Players"]![0]!["RecallArrivalSlots"]![0] = JObject.FromObject(new Vector2(100)));
        Reject(json => json["World"]!["Players"]![0]!["RecallArrivalSlots"]![1] = json["World"]!["Players"]![0]!["RecallArrivalSlots"]![0]!.DeepClone());
        Reject(json => json["World"]!["Players"]![0]!["RecallArrivalSlots"]![0]!["X"] = float.NaN);
        Assert.Equal(source.ToString(), JObject.Parse(JsonConvert.SerializeObject(world.CaptureMigrationState())).ToString());
    }

    [Fact]
    public void ReservedSlotsBlockConstructionForEveryOwnerWithoutSpendingCores()
    {
        var rules = Rules();
        var world = World(rules);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "water", new Vector2(125, 100), new Vector2(150, 110), 4, 10000)));
        var view = world.GetView(1);
        var slot = view.RecallArrivalSlots.OrderByDescending(x => x.X).First();
        int x = (int)MathF.Round(slot.X / rules.BuildGridSize), z = (int)MathF.Round(slot.Y / rules.BuildGridSize);
        foreach (int owner in new[] { 1, 2 })
        {
            Vector2 position = new Vector2(x, z) * rules.BuildGridSize;
            float radius = rules.BuildingRadius(BuildingKind.Collector);
            Assert.True(Vector2.Distance(position, world.GetView(owner).BasePosition) <= rules.BuildRadius - radius);
            Assert.All(State(world).Buildings.Where(b => b.BlocksMovement), b => Assert.True(Vector2.Distance(position, b.Position) >= b.Radius + radius));
            long balance = world.GetView(owner).CoreBalance;
            world.TryEnqueue(owner, SimCommand.Build(1, BuildingKind.Collector, x, z)); world.Step();
            Assert.Equal(balance, world.GetView(owner).CoreBalance);
            Assert.Contains(world.GetView(owner).Events, item => item.Kind == SimEventKind.CommandRejected && item.Detail == (int)CommandFailure.InvalidPlacement);
        }
    }

    [Fact]
    public void InsufficientLayoutRejectsSetupAndNewPlayerWithoutPartialChanges()
    {
        var json = GameRulesTests.Data().Root;
        json["construction"]!["buildRadius"] = 7;
        var rules = new GameRules(new GameDataDocument(json.ToString()));
        Assert.Throws<ArgumentException>(() => World(rules));
        var empty = new SimulationWorld(rules, new WorldSetup(), 1);
        string before = JsonConvert.SerializeObject(empty.CaptureMigrationState());
        Assert.False(empty.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(100), new Vector2(200), 4)));
        Assert.Equal(before, JsonConvert.SerializeObject(empty.CaptureMigrationState()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostCastAttackCadenceDoesNotAccumulateAttacks(bool cancel)
    {
        var rules = Rules();
        var world = World(rules);
        var state = State(world);
        var caster = state.Units.Single();
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        for (int i = 0; i < (cancel ? 58 : 60); i++) world.Step();
        if (cancel) { world.TryEnqueue(1, SimCommand.MoveTo(2, 1, caster.Position)); world.Step(); }
        var target = state.AddUnit(-1, caster.Element, caster.Position + Vector2.UnitY);
        target.AttackRemaining = 100;
        world.Step();
        Assert.Single(world.GetView(1).Events, x => x.Kind == SimEventKind.Attack && x.OwnerId == 1);
        int interval = (int)Math.Floor(rules.TickRate / rules.Elements[caster.Element].AttacksPerSecond);
        for (int i = 0; i < interval - 1; i++)
        {
            world.Step();
            Assert.DoesNotContain(world.GetView(1).Events, x => x.Kind == SimEventKind.Attack && x.OwnerId == 1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewPlayerCannotInvalidateExistingFixedLayout(bool forge)
    {
        var rules = Rules();
        var world = World(rules);
        var slot = world.GetView(1).RecallArrivalSlots.OrderByDescending(x => x.X).First();
        Vector2 home = forge ? slot + new Vector2(rules.BaseRadius + rules.EnergyForgeRadius + rules.SlotSpacing, 0) : slot;
        string before = JsonConvert.SerializeObject(world.CaptureMigrationState());
        Assert.False(world.TryAddPlayer(new PlayerSpawn(2, "water", home, new Vector2(200), 4, natureForge: false, energyForge: forge)));
        Assert.Equal(before, JsonConvert.SerializeObject(world.CaptureMigrationState()));
        Assert.Single(world.GetView(1).Buildings);
    }

    [Fact]
    public void DamageToOtherSquadsAndReinforcementsDoesNotInterrupt()
    {
        var world = World(Rules(), 3);
        var state = State(world);
        state.Units[1].SquadId = 2;
        state.Units[2].SquadId = 0; state.Units[2].DestinationSquadId = 1;
        state.Units[2].IsReinforcing = true; state.Units[2].Garrison = true;
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        CombatSystem.Damage(state, state.Units[1], 1, -1, 0, 0);
        CombatSystem.Damage(state, state.Units[2], 1, -1, 0, 0);
        VisibilitySystem.Refresh(state);
        Assert.Equal((byte)1, world.GetView(1).RecallSquad);
    }

    [Fact]
    public void GuardsAndTravellingReinforcementsOccupySlotsAndSuccessfulRecallUsesTheRemainingSlots()
    {
        var world = World(Rules());
        var state = State(world);
        var slots = world.GetView(1).RecallArrivalSlots;
        var guard = state.AddUnit(1, 0, slots[0], garrison: true);
        var travelling = state.AddUnit(1, 0, slots[1], garrison: true);
        travelling.SquadId = 0; travelling.IsReinforcing = true; travelling.DestinationSquadId = 2;
        state.Players[0].Squads[1].Position = new Vector2(400);
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        for (int i = 0; i < 59; i++) world.Step();
        travelling.Position = slots[1];
        // 완료 전의 이동 한 걸음 뒤에도 최소 간격 안에 남는다.
        world.Step();
        var caster = state.Units.Single(x => x.OwnerId == 1 && x.SquadId == 1);
        Assert.Equal(1U, caster.MovementRevision);
        Assert.True(Vector2.Distance(caster.Position, guard.Position) >= state.Rules.MinimumSeparation);
        Assert.True(Vector2.Distance(caster.Position, travelling.Position) >= state.Rules.MinimumSeparation);
        Assert.NotEqual(slots[0], caster.Position);
        Assert.NotEqual(slots[1], caster.Position);
    }

    [Fact]
    public void EarlierRecallUpdatesSpatialOccupancyBeforeAnotherPlayerCompletes()
    {
        var rules = Rules();
        var setup = new WorldSetup();
        foreach (int owner in new[] { 1, 2 })
        {
            setup.Players.Add(new PlayerSpawn(owner, "water", new Vector2(100), new Vector2(owner * 200), 4));
            for (int i = 0; i < rules.MaxPopulation; i++)
                setup.Units.Add(new UnitSpawn(owner, "water", new Vector2(owner * 200 + i % 10 * 2, owner * 200 + i / 10 * 2)));
        }
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(world.GetView(1).RecallArrivalSlots, world.GetView(2).RecallArrivalSlots);
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.TryEnqueue(2, SimCommand.Recall(1, 1)); world.Step();
        var secondPositions = world.GetView(2).Units.Where(x => x.OwnerId == 2).Select(x => x.Position).ToArray();
        for (int i = 0; i < 60; i++) world.Step();
        Assert.True(world.GetView(1).RecallReadyTick > world.GetView(1).Tick);
        Assert.Equal(0, world.GetView(2).RecallReadyTick);
        Assert.Equal(secondPositions, world.GetView(2).Units.Where(x => x.OwnerId == 2).Select(x => x.Position));
        var failure = Assert.Single(world.GetView(2).Events, x => x.Kind == SimEventKind.CommandRejected);
        Assert.Equal((int)CommandFailure.InvalidPlacement, failure.Detail);
        Assert.Equal(0, failure.SourceId); Assert.Equal(0, failure.TargetId);
    }

    [Fact]
    public void ProtectedOrNonDamagingHitDoesNotInterruptAndPassiveShieldAuraContinues()
    {
        var rules = Rules();
        var world = World(rules);
        var state = State(world);
        var caster = state.Units.Single();
        caster.Element = rules.ElementIndex("magnetism");
        state.ProtectionEnabled = true;
        state.Players[0].ProtectionEndTick = 100;
        world.TryEnqueue(1, SimCommand.Recall(1, 1)); world.Step();
        Assert.True(caster.Shield > 0);
        float hp = caster.Health, shield = caster.Shield;
        Assert.Equal(0, CombatSystem.Damage(state, caster, 10, 2, 0, caster.Element));
        Assert.Equal(hp, caster.Health); Assert.Equal(shield, caster.Shield);
        VisibilitySystem.Refresh(state);
        Assert.Equal((byte)1, world.GetView(1).RecallSquad);
    }
}
