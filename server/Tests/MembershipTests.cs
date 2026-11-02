using System.Numerics;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>동적 접속에서 기존 상태·시야 슬롯과 입력 확인 번호를 보존하는지 검사한다.</summary>
public sealed class MembershipTests
{
    [Fact]
    public void NewPlayerKeepsExistingWorldAndInitialBalance()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60), cores: rules.StartingCores)));
        Assert.Equal(rules.StartUnitCount, world.GetView(1).Population);
        Assert.Equal(rules.StartingCores, world.GetView(1).CoreBalance);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX)));
        world.Step();
        var position = world.GetView(1).LegionPosition;
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(500), new Vector2(510))));
        Assert.Equal(position, world.GetView(1).LegionPosition);
        Assert.Equal(1UL, world.GetView(1).AppliedSequence);
        Assert.DoesNotContain(world.GetView(1).Units, unit => unit.OwnerId == 2);
        Assert.False(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60))));
    }

    [Fact]
    public void DisconnectStopsQueuedAndHeldInputWithoutDeletingTheLegion()
    {
        var rules = GameRulesTests.Rules();
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        world.TryEnqueue(1, SimCommand.Move(1, Vector2.UnitX)); world.Step();
        var position = world.GetView(1).LegionPosition;
        world.TryEnqueue(1, SimCommand.Move(2, Vector2.UnitY));
        world.StopPlayer(1);
        world.Step();
        Assert.Equal(position, world.GetView(1).LegionPosition);
        Assert.Equal(rules.StartUnitCount, world.GetView(1).Population);
        Assert.Equal(1UL, world.GetView(1).AppliedSequence);
    }

    [Fact]
    public void JoiningWithMoreVisibleDropsThanUnitsPreservesTheExistingWorld()
    {
        var rules = GameRulesTests.Rules(); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(50), new Vector2(60)));
        for (int index = 0; index < 30; index++) setup.Cores.Add(new CoreSpawn(new Vector2(50), 1));
        var world = new SimulationWorld(rules, setup, 1);
        int[] cores = world.GetView(1).Cores.Select(core => core.Id).ToArray();
        Assert.Equal(30, cores.Length);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "fire", new Vector2(500), new Vector2(510))));
        Assert.Equal(cores, world.GetView(1).Cores.Select(core => core.Id));
        Assert.Equal(rules.StartUnitCount, world.GetView(2).Population);
        Assert.Equal(0, world.Metrics.Tick);
    }

    [Fact]
    public void JoiningAfterCombatPreservesSurvivorsDropsAndTheWorldTick()
    {
        var rules = GameRulesTests.Rules(); var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "fire", new Vector2(50), new Vector2(60)));
        setup.Units.Add(new UnitSpawn(1, "fire", new Vector2(60)));
        for (int index = 0; index < 30; index++) setup.Units.Add(new UnitSpawn(-1, "water", new Vector2(61 + index * 0.01f, 60), health: 1));
        var world = new SimulationWorld(rules, setup, 1);
        for (int index = 0; index < 20; index++) world.Step();
        Assert.True(world.Metrics.Deaths > 0);
        long tick = world.Metrics.Tick;
        int[] units = world.GetView(1).Units.Select(unit => unit.Id).ToArray();
        int[] cores = world.GetView(1).Cores.Select(core => core.Id).ToArray();
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "water", new Vector2(500), new Vector2(510))));
        Assert.Equal(tick, world.Metrics.Tick);
        Assert.Equal(units, world.GetView(1).Units.Select(unit => unit.Id));
        Assert.Equal(cores, world.GetView(1).Cores.Select(core => core.Id));
    }
}
