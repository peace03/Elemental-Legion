using System.Numerics;
using System.Reflection;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>본진 체력의 틱별 합산 피해와 자기권·패배·복원 경계를 검사한다.</summary>
public sealed class BaseDefenseBudgetTests
{
    [Theory]
    [InlineData("earth", 1)]
    [InlineData("earth", 20)]
    [InlineData("earth,water,fire,light", 4)]
    [InlineData("light,fire,water,earth", 4)]
    public void AllAttackersShareBaseHullBudget(string elements, int count)
    {
        var rules = Rules();
        var world = AttackingWorld(rules, elements.Split(','), count);
        float before = Base(world).Health;

        world.Step();

        float budget = Budget(rules);
        Assert.Equal(12.5f, budget);
        Assert.Equal(budget, before - Base(world).Health);
        var attacks = world.GetView(1).Events.Where(e => e.Kind == SimEventKind.Attack &&
            e.TargetKind == ObservedEntityKind.Building).ToArray();
        Assert.Equal(count, attacks.Length);
        Assert.Equal(budget, attacks.Sum(e => e.Amount));
        Assert.All(attacks, e => Assert.InRange(e.Amount, 0, budget));
        Assert.Equal(budget, Assert.Single(world.GetView(2).Events, e => e.Kind == SimEventKind.BaseUnderAttack).Amount);
    }

    [Fact]
    public void SmallHitsShareTheRemainingBudgetInAttackOrder()
    {
        var rules = Rules(data =>
        {
            foreach (JObject element in data["elements"]!)
                element["attack"] = (string)element["id"]! == "earth" ? 3 : 4;
        });
        foreach (string[] order in new[] { new[] { "earth", "water", "fire", "ice" }, new[] { "ice", "fire", "water", "earth" } })
        {
            var world = AttackingWorld(rules, order, order.Length);
            world.Step();
            var attacks = world.GetView(1).Events.Where(e => e.Kind == SimEventKind.Attack).ToArray();
            Assert.Equal(Budget(rules), attacks.Sum(e => e.Amount));
            Assert.Equal(2000 - Budget(rules), Base(world).Health);
            Assert.Equal(Budget(rules) - attacks.Take(3).Sum(e => e.Amount), attacks[3].Amount);
        }
    }

    [Fact]
    public void DifferentOwnersShareTheSameBaseBudget()
    {
        var rules = Rules();
        var setup = Setup(new Vector2(207, 200));
        setup.Players.Add(new PlayerSpawn(3, "fire", new Vector2(600), new Vector2(193, 200)));
        setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(207, 200)));
        setup.Units.Add(new UnitSpawn(3, "fire", new Vector2(193, 200)));
        var world = new SimulationWorld(rules, setup, 1);
        world.Step();

        Assert.Equal(2000 - Budget(rules), Base(world).Health);
        var attacks = world.GetView(2).Events.Where(e => e.Kind == SimEventKind.Attack).ToArray();
        Assert.Equal(new[] { 1, 3 }, attacks.Select(e => e.OwnerId));
        Assert.Equal(Budget(rules), attacks.Sum(e => e.Amount));
    }

    [Theory]
    [InlineData(10, 90f)]
    [InlineData(40, 300f)]
    public void HullBudgetUsesConfiguredDamageRateAndTickDuration(int tickRate, float damageRate)
    {
        var rules = Rules(data =>
        {
            data["world"]!["tickRate"] = tickRate;
            data["buildings"]!["base"]!["incomingDamagePerSecond"] = damageRate;
        });
        var world = AttackingWorld(rules, new[] { "earth" }, 3);
        world.Step();
        Assert.Equal(2000 - damageRate / tickRate, Base(world).Health);
    }

    [Fact]
    public void DomeOverflowUsesHullBudgetAndReportsOnlyAppliedDamage()
    {
        var rules = Rules(data => data["buildings"]!["dome"]!["hp"] = 20);
        var world = AttackingWorld(rules, new[] { "earth" }, 3, dome: true);
        world.Step();

        Assert.DoesNotContain(world.GetView(2).Buildings, b => b.Kind == BuildingKind.Dome);
        Assert.Equal(2000 - Budget(rules), Base(world).Health);
        var attacks = world.GetView(1).Events.Where(e => e.Kind == SimEventKind.Attack).ToArray();
        Assert.Equal(20 + Budget(rules), attacks.Sum(e => e.Amount));
        Assert.Equal(2, attacks.Count(e => e.Amount == 0));
        Assert.Equal(20 + Budget(rules), Assert.Single(world.GetView(2).Events,
            e => e.Kind == SimEventKind.BaseUnderAttack).Amount);
    }

    [Fact]
    public void DomeAndOuterBuildingsKeepNormalDamage()
    {
        var rules = Rules();
        var shielded = AttackingWorld(rules, new[] { "earth" }, 3, dome: true);
        shielded.Step();
        Assert.Equal(2000, Base(shielded).Health);
        Assert.Equal(rules.BuildingHealth(BuildingKind.Dome) - 150,
            Assert.Single(shielded.GetView(2).Buildings, b => b.Kind == BuildingKind.Dome).Health);
        Assert.Equal(150, shielded.GetView(1).Events.Where(e => e.Kind == SimEventKind.Attack).Sum(e => e.Amount));

        var setup = Setup(new Vector2(219, 200));
        for (int i = 0; i < 3; i++) setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(219, 200)));
        var outer = new SimulationWorld(rules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, outer.TryEnqueue(2, SimCommand.Build(1, BuildingKind.Collector, 215, 200)));
        outer.Step();
        Assert.Equal(rules.BuildingHealth(BuildingKind.Collector) - 150,
            Assert.Single(outer.GetView(2).Buildings, b => b.Kind == BuildingKind.Collector).Health);
        Assert.Equal(2000, Base(outer).Health);
    }

    [Fact]
    public void DirectDomeTargetDoesNotConsumeHullBudget()
    {
        var rules = Rules();
        var world = AttackingWorld(rules, new[] { "earth" }, 1, dome: true);
        world.Step();
        // 일반 표적 탐색은 자기권을 제외하므로 내부 공격의 직접 지정 경로를 검사한다.
        WorldState state = State(world);
        SiegeSystem.Attack(state, state.Units[0], state.Players[1].Dome, 50);
        Assert.Equal(rules.BuildingHealth(BuildingKind.Dome) - 100,
            state.Players[1].Dome.Health);
        Assert.Equal(2000, state.Buildings.Single(b => b.OwnerId == 2 && b.Kind == BuildingKind.Base).Health);
    }

    [Fact]
    public void DifferentBasesHaveIndependentBudgets()
    {
        var rules = Rules();
        var setup = Setup(new Vector2(207, 200));
        setup.Players.Add(new PlayerSpawn(3, "earth", new Vector2(800, 600), new Vector2(607, 600)));
        setup.Players.Add(new PlayerSpawn(4, "water", new Vector2(600), new Vector2(600, 630)));
        setup.Units.Add(new UnitSpawn(1, "earth", new Vector2(207, 200)));
        setup.Units.Add(new UnitSpawn(3, "earth", new Vector2(607, 600)));
        var world = new SimulationWorld(rules, setup, 1);

        world.Step();

        Assert.Equal(2000 - Budget(rules), Base(world, 2).Health);
        Assert.Equal(2000 - Budget(rules), Base(world, 4).Health);
    }

    [Fact]
    public void BudgetResetsNextTickAndNeverAccruesUnusedOrExcessDamage()
    {
        var rules = Rules(data =>
        {
            foreach (JObject element in data["elements"]!) element["attacksPerSecond"] = 1;
        });
        var world = AttackingWorld(rules, new[] { "earth" }, 3);
        world.Step();
        Assert.Equal(2000 - Budget(rules), Base(world).Health);
        for (int tick = 1; tick < rules.TickRate - 1; tick++) world.Step();
        Assert.Equal(2000 - Budget(rules), Base(world).Health);
        Assert.DoesNotContain(world.GetView(1).Events, e => e.Kind == SimEventKind.Attack);
        world.Step();
        Assert.Equal(2000 - 2 * Budget(rules), Base(world).Health);
        world.Step();
        Assert.Equal(2000 - 2 * Budget(rules), Base(world).Health);
    }

    [Fact]
    public void FullBaseNeeds160DamageTicks()
    {
        var rules = Rules();
        var world = AttackingWorld(rules, new[] { "earth" }, 3);
        Assert.Equal(2000, Base(world).Health);
        for (int tick = 1; tick < 160; tick++)
        {
            world.Step();
            Assert.True(world.TryGetView(2, out _));
            Assert.Equal(2000 - tick * Budget(rules), Base(world).Health);
        }
        world.Step();
        Assert.False(world.TryGetView(2, out _));
        Assert.Equal(1, world.GetView(1).BattleWins);
        Assert.Equal(Budget(rules), world.GetView(1).Events.Where(e => e.Kind == SimEventKind.Attack).Sum(e => e.Amount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedBaseDefeatsOnlyOnceAfterBudgetExhaustion(bool ghost)
    {
        var rules = Rules();
        var world = AttackingWorld(rules, new[] { "earth" }, 3);
        JObject saved = JObject.FromObject(world.CaptureMigrationState());
        saved["World"]!["Buildings"]!.Single(b => (int)b["Owner"]! == 2)["Health"] = 25.5f;
        world = SimulationWorld.RestoreMigrationState(rules, saved.ToObject<MigrationWorldState>()!);
        if (ghost) Assert.True(world.BeginGhost(2));
        world.Step();
        Assert.Equal(13, Base(world).Health);
        world.Step();
        Assert.Equal(0.5f, Base(world).Health);
        Assert.Equal(0, world.GetView(1).BattleWins);
        world.Step();
        Assert.False(world.TryGetView(2, out _));
        Assert.Equal(0.5f, world.GetView(1).Events.Where(e => e.Kind == SimEventKind.Attack).Sum(e => e.Amount));
        Assert.Equal(ghost ? 0 : 1, world.GetView(1).BattleWins);
        long drops = world.Metrics.Drops;
        world.Step();
        Assert.Equal(ghost ? 0 : 1, world.GetView(1).BattleWins);
        Assert.Equal(drops, world.Metrics.Drops);
    }

    [Fact]
    public void MigrationAfterSpentBudgetRestartsAtTheNextTickAndStaysEquivalent()
    {
        var rules = Rules();
        var original = AttackingWorld(rules, new[] { "earth", "water", "fire" }, 3);
        original.Step();
        Assert.Equal(2000 - Budget(rules), Base(original).Health);
        string captured = JsonConvert.SerializeObject(original.CaptureMigrationState());
        var restored = SimulationWorld.RestoreMigrationState(rules, JsonConvert.DeserializeObject<MigrationWorldState>(captured)!);
        Assert.Empty(restored.GetView(2).Events);
        for (int tick = 2; tick <= 160; tick++)
        {
            original.Step();
            restored.Step();
            Assert.Equal(tick, restored.GetView(1).Tick);
            Assert.Equal(JsonConvert.SerializeObject(original.CaptureMigrationState()), JsonConvert.SerializeObject(restored.CaptureMigrationState()));
            Assert.Equal(JsonConvert.SerializeObject(original.GetView(1).Events), JsonConvert.SerializeObject(restored.GetView(1).Events));
            if (tick < 160) Assert.Equal(2000 - tick * Budget(rules), Base(restored).Health);
        }
        Assert.Equal(captured, JsonConvert.SerializeObject(JsonConvert.DeserializeObject<MigrationWorldState>(captured)));
        Assert.False(restored.TryGetView(2, out _));
    }

    [Fact]
    public void DiskCheckpointKeepsDamageAndUsesFreshBudgetAfterGhostRestore()
    {
        var rules = Rules();
        var world = AttackingWorld(rules, new[] { "earth" }, 3);
        world.Step();
        var saved = JsonConvert.DeserializeObject<WorldCheckpoint>(JsonConvert.SerializeObject(world.CaptureCheckpoint()))!;
        Assert.Equal(WorldCheckpoint.CurrentSchema, saved.SchemaVersion);
        var restored = SimulationWorld.RestoreCheckpoint(rules, saved);
        Assert.Equal(2000 - Budget(rules), Base(restored).Health);
        Assert.True(restored.TryResumeGhost(1));
        restored.Step();
        Assert.Equal(2000 - 2 * Budget(rules), Base(restored).Health);
    }

    private static float Budget(GameRules rules) => (float)(rules.BaseIncomingDamagePerSecond * rules.DeltaTime);

    private static WorldState State(SimulationWorld world) => (WorldState)typeof(SimulationWorld)
        .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;

    private static BuildingView Base(SimulationWorld world, int owner = 2) =>
        Assert.Single(world.GetView(owner).Buildings, b => b.OwnerId == owner && b.Kind == BuildingKind.Base);

    private static GameRules Rules(Action<JObject>? configure = null)
    {
        var data = GameRulesTests.Data().Root;
        foreach (JObject element in data["elements"]!)
        {
            element["attack"] = 50;
            element["attacksPerSecond"] = (int)data["world"]!["tickRate"]!;
            element["range"] = 6;
        }
        data["buildings"]!["dome"]!["buildSeconds"] = 0;
        data["buildings"]!["collector"]!["buildSeconds"] = 0;
        configure?.Invoke(data);
        return new GameRules(new GameDataDocument(data.ToString()));
    }

    private static WorldSetup Setup(Vector2 attacker)
    {
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(400), attacker));
        setup.Players.Add(new PlayerSpawn(2, "water", new Vector2(200), new Vector2(200, 230), 4, 100));
        return setup;
    }

    private static SimulationWorld AttackingWorld(GameRules rules, string[] elements, int count, bool dome = false)
    {
        var edge = new Vector2(207, 200);
        var setup = Setup(edge);
        for (int i = 0; i < count; i++) setup.Units.Add(new UnitSpawn(1, elements[i % elements.Length], edge));
        var world = new SimulationWorld(rules, setup, 1);
        if (dome) Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(2, SimCommand.Build(1, BuildingKind.Dome, 0, 0)));
        return world;
    }
}
