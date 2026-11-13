using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class FacilityProgressionTests
{
    [Fact]
    public void BaseLevelsUnlockOneRankAtATimeAndStopAtPopulation300()
    {
        var rules = GameRulesTests.Rules();
        var world = Create(rules);
        var view = world.GetView(1);
        BuildingState forge = State(world).Buildings.Single(building => building.Kind == BuildingKind.NatureForge);
        ulong sequence = 0;
        for (byte rank = 1; rank <= 4; rank++)
        {
            long balance = view.CoreBalance;
            Apply(world, SimCommand.UpgradeFacility(++sequence, forge.Id, FacilityUpgradeBranch.A, rank), CommandFailure.NotUnlocked);
            Assert.Equal(balance, view.CoreBalance);
            Apply(world, SimCommand.UpgradeBase(++sequence));
            Assert.Equal(rank + 1, view.BaseLevel);
            Assert.True(rules.TryGetFacilityUpgrade(forge.Kind, FacilityUpgradeBranch.A, rank, out FacilityUpgrade expected));
            balance = view.CoreBalance;
            Apply(world, SimCommand.UpgradeFacility(++sequence, forge.Id, FacilityUpgradeBranch.A, rank));
            Assert.Equal(rank, forge.UpgradeRank);
            Assert.Equal(balance - expected.Cost, view.CoreBalance);
        }

        while (view.BaseLevel < 9) Apply(world, SimCommand.UpgradeBase(++sequence));
        Assert.Equal(300, view.PopulationCap);
        Assert.Equal(20, rules.GarrisonCap(view.BaseLevel));
        Assert.Equal(6, rules.TowerSlots(view.BaseLevel));
        Apply(world, SimCommand.UpgradeBase(++sequence), CommandFailure.MaxLevel);
    }

    [Fact]
    public void InvalidOwnershipAndObsoletePurchaseDoNotSpendCores()
    {
        var rules = GameRulesTests.Rules();
        var world = Create(rules, level: 5, secondPlayer: true);
        WorldState state = State(world);
        BuildingState forge = state.Buildings.Single(building => building.Kind == BuildingKind.NatureForge);
        var view = world.GetView(1);
        Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.UpgradeFacility(1, 0, FacilityUpgradeBranch.A, 1)));
        Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.UpgradeFacility(1, forge.Id, FacilityUpgradeBranch.None, 1)));
        Assert.Equal(CommandAdmission.InvalidInput, world.TryEnqueue(1, SimCommand.UpgradeFacility(1, forge.Id, FacilityUpgradeBranch.A, 5)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.UpgradeFacility(1, forge.Id, FacilityUpgradeBranch.A, 1)));
        world.Step();
        Assert.Contains(world.GetView(2).Events, item => item.Sequence == 1 && item.Detail == (int)CommandFailure.InvalidPlacement);
        Assert.Equal(2000, world.GetView(2).CoreBalance);

        long balance = view.CoreBalance;
        Apply(world, SimCommand.UpgradeFacility(1, forge.Id, FacilityUpgradeBranch.A, 2), CommandFailure.Busy);
        Assert.Equal(balance, view.CoreBalance);
        Apply(world, SimCommand.UpgradeFacility(2, forge.Id, FacilityUpgradeBranch.A, 1));
        balance = view.CoreBalance;
        Apply(world, SimCommand.UpgradeFacility(3, forge.Id, FacilityUpgradeBranch.A, 1), CommandFailure.Busy);
        Apply(world, SimCommand.UpgradeFacility(4, forge.Id, FacilityUpgradeBranch.B, 2), CommandFailure.Busy);
        Assert.Equal(balance, view.CoreBalance);

        var position = new Vector2(230, 200);
        state.AddBuilding(state.PlayerById[1], BuildingKind.Collector, position, constructionEndTick: state.Tick + 10);
        BuildingState collector = state.Buildings.Single(building => building.Kind == BuildingKind.Collector);
        Apply(world, SimCommand.UpgradeFacility(5, collector.Id, FacilityUpgradeBranch.B, 1), CommandFailure.Busy);
        collector.Health = 0;
        Apply(world, SimCommand.UpgradeFacility(6, collector.Id, FacilityUpgradeBranch.B, 1), CommandFailure.InvalidPlacement);
        Assert.Equal(balance, view.CoreBalance);
        Assert.Equal(1, forge.UpgradeRank);
        Assert.Equal(FacilityUpgradeBranch.A, forge.UpgradeBranch);
    }

    [Theory]
    [InlineData(FacilityUpgradeBranch.A)]
    [InlineData(FacilityUpgradeBranch.B)]
    public void CollectorUsesPurchasedScheduleAtEveryRankAndStopsWhenDestroyed(FacilityUpgradeBranch branch)
    {
        var rules = GameRulesTests.Rules();
        var world = Create(rules, level: 5);
        WorldState state = State(world);
        state.AddBuilding(state.PlayerById[1], BuildingKind.Collector, new Vector2(230, 200));
        BuildingState collector = state.Buildings.Single(building => building.Kind == BuildingKind.Collector);
        Assert.Equal(rules.CollectorIntervalTicks, collector.NextProductionTick);

        for (byte rank = 1; rank <= 4; rank++)
        {
            long before = collector.NextProductionTick;
            Apply(world, SimCommand.UpgradeFacility(rank, collector.Id, branch, rank));
            long interval = rules.CollectorProductionTicks(branch, rank);
            int amount = rules.CollectorProductionAmount(branch, rank);
            Assert.Equal(branch == FacilityUpgradeBranch.A ? Math.Min(before, state.Tick + interval) : before, collector.NextProductionTick);
            StepUntil(world, collector.NextProductionTick - 1);
            long balance = world.GetView(1).CoreBalance;
            world.Step();
            Assert.Equal(balance + amount, world.GetView(1).CoreBalance);
            Assert.Equal(state.Tick + interval, collector.NextProductionTick);
            Assert.Contains(state.Events, e => e.Kind == SimEventKind.CoresProduced && e.EntityId == collector.Id && e.Amount == amount);
        }

        StepUntil(world, collector.NextProductionTick - 1);
        long lastBalance = world.GetView(1).CoreBalance;
        collector.Health = 0;
        world.Step();
        Assert.Equal(lastBalance, world.GetView(1).CoreBalance);
        Assert.DoesNotContain(state.Events, item => item.Kind == SimEventKind.CoresProduced);
    }

    [Fact]
    public void CollectorKeepsRemainingTicksDuringGhostAndResume()
    {
        var rules = GameRulesTests.Rules();
        var world = Create(rules);
        WorldState state = State(world);
        state.AddBuilding(state.PlayerById[1], BuildingKind.Collector, new Vector2(230, 200));
        BuildingState collector = state.Buildings.Single(building => building.Kind == BuildingKind.Collector);
        StepUntil(world, collector.NextProductionTick - 3);
        Assert.True(world.BeginGhost(1));
        Assert.Equal(state.Tick, collector.ProductionPausedAtTick);
        StepUntil(world, collector.NextProductionTick + 10);
        Assert.Equal(2000, world.GetView(1).CoreBalance);
        Assert.True(world.TryResumeGhost(1));
        Assert.Equal(state.Tick + 3, collector.NextProductionTick);
        StepUntil(world, collector.NextProductionTick - 1);
        Assert.Equal(2000, world.GetView(1).CoreBalance);
        world.Step();
        Assert.Equal(2001, world.GetView(1).CoreBalance);
    }

    [Theory]
    [InlineData(BuildingKind.NatureForge, "water", FacilityUpgradeBranch.A, 8, 7, 0f)]
    [InlineData(BuildingKind.NatureForge, "water", FacilityUpgradeBranch.B, 8, 8, 0.15f)]
    [InlineData(BuildingKind.EnergyForge, "light", FacilityUpgradeBranch.A, 17, 14, 0f)]
    [InlineData(BuildingKind.EnergyForge, "light", FacilityUpgradeBranch.B, 17, 17, 0.15f)]
    public void ForgeLocksDiscountAndDamageAtOrderAcceptance(BuildingKind kind, string element, FacilityUpgradeBranch branch,
        int oldPrice, int newPrice, float bonus)
    {
        var rules = GameRulesTests.Rules();
        var world = Create(rules, level: 5);
        WorldState state = State(world);
        PlayerState owner = state.PlayerById[1];
        BuildingState forge = state.Buildings.Single(building => building.Kind == kind);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(1, element)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.UpgradeFacility(2, forge.Id, branch, 1)));
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(3, element, 2, 4)));
        world.Step();
        Assert.Equal(new[] { oldPrice, newPrice, newPrice }, owner.TrainingQueue.Select(order => order.Cost));
        Assert.Equal(new[] { 0, bonus, bonus }, owner.TrainingQueue.Select(order => order.AttackBonus));
        ulong orderId = owner.TrainingQueue[^1].Id;
        long balance = owner.Cores;
        Apply(world, SimCommand.CancelTraining(4, orderId));
        Assert.Equal(balance + newPrice, owner.Cores);
        while (owner.TrainingQueue.Count > 0) StepUntil(world, owner.TrainingEndTick);
        Assert.Equal(new[] { 0, bonus }, state.Units.Select(unit => unit.TrainingAttackBonus));
        forge.Health = 0;
        world.Step();
        Assert.Equal(new[] { 0, bonus }, state.Units.Select(unit => unit.TrainingAttackBonus));
    }

    [Theory]
    [InlineData(BuildingKind.NatureTower, "water")]
    [InlineData(BuildingKind.EnergyTower, "light")]
    public void TowersApplyPurchasedRangeOrDamageAtEveryRank(BuildingKind kind, string element)
    {
        var rules = GameRulesTests.Rules();
        var world = Create(rules, level: 5);
        WorldState state = State(world);
        int elementIndex = rules.ElementIndex(element);
        var position = new Vector2(230, 200);
        var radiusPosition = new Vector2(230, 216);
        state.AddBuilding(state.PlayerById[1], kind, position, elementIndex);
        state.AddBuilding(state.PlayerById[1], kind, radiusPosition, elementIndex);
        BuildingState damageTower = state.Buildings.Single(building => building.Kind == kind && building.Position == position);
        BuildingState rangeTower = state.Buildings.Single(building => building.Kind == kind && building.Position == radiusPosition);
        float hp = rules.Elements[elementIndex].Hp;
        UnitState target = state.AddUnit(-1, elementIndex, position + Vector2.UnitX * 4);
        ulong sequence = 0;
        for (byte rank = 1; rank <= 4; rank++)
        {
            target.Position = new Vector2(450);
            Apply(world, SimCommand.UpgradeFacility(++sequence, damageTower.Id, FacilityUpgradeBranch.B, rank));
            Apply(world, SimCommand.UpgradeFacility(++sequence, rangeTower.Id, FacilityUpgradeBranch.A, rank));
            Assert.True(rules.TryGetFacilityUpgrade(kind, FacilityUpgradeBranch.A, rank, out FacilityUpgrade range));
            Assert.True(rules.TryGetFacilityUpgrade(kind, FacilityUpgradeBranch.B, rank, out FacilityUpgrade damage));
            rangeTower.AttackRemaining = damageTower.AttackRemaining = 0;
            target.Health = hp;
            target.Position = position + Vector2.UnitX * 4;
            state.Grid.Rebuild(state.Units);
            SiegeSystem.RunTowers(state);
            Assert.Equal(hp - rules.Elements[elementIndex].Attack * (float)damage.Effect, target.Health, 4);

            target.Health = hp;
            target.Position = radiusPosition + Vector2.UnitX * ((float)range.Effect + 0.01f);
            state.Grid.Rebuild(state.Units);
            SiegeSystem.RunTowers(state);
            Assert.Equal(hp, target.Health);
            target.Position = radiusPosition + Vector2.UnitX * (float)range.Effect;
            state.Grid.Rebuild(state.Units);
            SiegeSystem.RunTowers(state);
            Assert.Equal(hp - rules.Elements[elementIndex].Attack * rules.TowerDamageMultiplier, target.Health, 4);
        }
    }

    [Fact]
    public void TrainingDamageBonusScalesTheStrikeButNotTheBurn()
    {
        var rules = GameRulesTests.Rules();
        var world = Create(rules, level: 5);
        WorldState state = State(world);
        int fire = rules.ElementIndex("fire");
        var position = new Vector2(350);
        UnitState source = state.AddUnit(1, fire, position);
        UnitState target = state.AddUnit(-1, fire, position + Vector2.UnitX);
        source.Target = target;
        source.TrainingAttackBonus = 0.25f;
        var combat = new CombatSystem();
        combat.Run(state);
        Assert.Equal(rules.Elements[fire].Hp - rules.Elements[fire].Attack * 1.25f, target.Health, 4);
        Assert.Equal(rules.Elements[fire].DamagePerSecond, target.Statuses[(int)StatusKind.Burn].Dps);
    }

    private static SimulationWorld Create(GameRules rules, int level = 1, bool secondPlayer = false)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(200, 230), level, 2000,
            natureForge: true, energyForge: level >= 3));
        if (secondPlayer) setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(700), new Vector2(700, 730), level, 2000));
        return new SimulationWorld(rules, setup, 1);
    }

    private static void StepUntil(SimulationWorld world, long tick)
    {
        while (world.GetView(1).Tick < tick) world.Step();
    }

    private static void Apply(SimulationWorld world, SimCommand command, CommandFailure expected = CommandFailure.None)
    {
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, command));
        world.Step();
        SimEvent result = Assert.Single(world.GetView(1).Events, e => e.Sequence == command.Sequence);
        Assert.Equal(expected == CommandFailure.None ? SimEventKind.CommandApplied : SimEventKind.CommandRejected, result.Kind);
        if (expected != CommandFailure.None) Assert.Equal((int)expected, result.Detail);
    }

    private static WorldState State(SimulationWorld world) => (WorldState)typeof(SimulationWorld)
        .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
}
