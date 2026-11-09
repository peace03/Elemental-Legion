using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>일반 서버의 중립·자원 공급과 재사용 가능한 입장 위치를 검사한다.</summary>
public sealed class M4EcologyTests
{
    [Fact]
    public void RoomAreaGrowsWithStartingRosterAndEverySiteRemainsPlayable()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        float previousSize = 0;
        for (int players = 2; players <= rules.MaxPlayers; players++)
        {
            GameRules scaled = rules.ForRoom(players);
            Assert.True(scaled.MapSize > previousSize);
            Assert.InRange(scaled.MapSize * scaled.MapSize / players, 28_650, 28_950);
            Assert.Equal(rules.DataHash, scaled.DataHash);
            var room = new NetworkWorld(scaled, settings);
            int groups = (int)Math.Ceiling((double)scaled.MapSize * scaled.MapSize * scaled.NeutralWorldGroupsPerSquareKm / 1_000_000);
            Assert.InRange(room.World.Metrics.NeutralUnits, groups, groups * 4);
            for (int index = 0; index < players; index++)
                Assert.Equal(JoinFailure.None, room.TryJoin(rules.StartingElements[0], out _));
            Assert.Equal(JoinFailure.WorldFull, room.TryJoin(rules.StartingElements[0], out _));
            previousSize = scaled.MapSize;
        }
    }

    [Fact]
    public void NormalWorldStartsWithAVisibleSeededNeutralGroupOutsideAttackRange()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        var first = new NetworkWorld(rules, settings);
        var second = new NetworkWorld(rules, settings);
        int element = rules.ElementIndex("lightning");

        Assert.Equal(JoinFailure.None, first.TryJoin(element, out int owner));
        Assert.Equal(JoinFailure.None, second.TryJoin(element, out int other));
        var view = first.World.GetView(owner);
        var sameSeed = second.World.GetView(other);
        bool IsStartingNeutral(UnitView unit) => unit.OwnerId < 0 &&
            Vector2.DistanceSquared(unit.Position, view.LegionPosition) <= rules.VisionRadius * rules.VisionRadius;
        var neutral = view.Units.Where(IsStartingNeutral).ToArray();
        Assert.Equal(view.BasePosition, sameSeed.BasePosition);
        Assert.Equal(neutral, sameSeed.Units.Where(IsStartingNeutral).ToArray());
        Assert.InRange(neutral.Length, 1, 4);
        Assert.All(neutral, unit =>
        {
            Assert.Equal(ElementGroup.Nature, rules.Elements[unit.Element].Group);
            Assert.True(Vector2.Distance(unit.Position, view.BasePosition) > rules.BuildRadius);
        });
        Assert.Equal(3, view.Population);
        Assert.Equal(0, view.CoreBalance);
        Assert.False(view.NatureForge);

        first.Step();
        Assert.Equal(0, first.World.Metrics.Attacks);
        Assert.Equal(neutral, view.Units.Where(IsStartingNeutral).ToArray());
    }

    [Fact]
    public void NetworkWorldUsesItsOwnNeutralDensity()
    {
        var json = GameRulesTests.Data().Root;
        json["neutral"]!["soloChance"] = 0;
        json["neutral"]!["groupSizeMin"] = 3;
        json["neutral"]!["groupSizeMax"] = 3;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));

        Assert.Equal(12f, rules.NeutralGroupsPerSquareKm);
        Assert.Equal(180f, rules.NeutralWorldGroupsPerSquareKm);
        Assert.Equal(509 * 3, network.World.Metrics.NeutralUnits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentryWaitsForAnOccupiedSpawnToClear(bool byFormation)
    {
        var json = GameRulesTests.Data().Root;
        json["server"]!["maxPlayers"] = 2;
        json["ghost"]!["decaySeconds"] = 0.15;
        json["ghost"]!["reconnectWindowSeconds"] = 0.15;
        json["world"]!["legionSpeed"] = 0.01;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var settings = new NetworkSettings(data);
        var world = new SimulationWorld(rules, new WorldSetup(), 1);
        world.EnableEcology(settings.MinBaseDistance);
        Assert.Equal(25f, rules.SpawnMinEnemyUnitDistance);
        Assert.True(world.TryChooseSpawn(out Vector2 firstBase, out Vector2 firstLegion));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "earth", firstBase, firstLegion)));
        Assert.True(world.TryChooseSpawn(out Vector2 secondBase, out _));
        Assert.InRange(Vector2.Distance(firstBase, secondBase), settings.MinBaseDistance, rules.MapSize);

        Vector2 enemyPosition = byFormation
            ? firstLegion + Vector2.UnitX * (rules.SpawnMinEnemyUnitDistance + rules.SlotSpacing * 1.1f)
            : firstBase - Vector2.UnitY * (rules.SpawnMinEnemyUnitDistance - 1);
        Assert.True(world.TryAddPlayer(new PlayerSpawn(2, "earth", secondBase, enemyPosition)));
        Assert.True(world.BeginGhost(1));
        for (long tick = 0; tick < rules.GhostDecayTicks; tick++) world.Step();
        Assert.False(world.TryGetView(1, out _));
        var enemies = world.GetView(2).Units.Where(unit => unit.OwnerId == 2).ToArray();
        Assert.NotEmpty(enemies);
        if (byFormation)
        {
            Assert.All(enemies, unit =>
            {
                Assert.True(Vector2.Distance(unit.Position, firstBase) > rules.SpawnMinEnemyUnitDistance);
                Assert.True(Vector2.Distance(unit.Position, firstLegion) > rules.SpawnMinEnemyUnitDistance);
            });
            Vector2 formationEdge = firstLegion + Vector2.UnitX * rules.SlotSpacing;
            Assert.Contains(enemies, unit => Vector2.Distance(unit.Position, formationEdge) < rules.SpawnMinEnemyUnitDistance);
        }
        else
        {
            Assert.All(enemies, unit => Assert.True(Vector2.Distance(unit.Position, firstLegion) > rules.SpawnMinEnemyUnitDistance + rules.SlotSpacing));
            Assert.Contains(enemies, unit => Vector2.Distance(unit.Position, firstBase) < rules.SpawnMinEnemyUnitDistance);
        }

        Assert.False(world.TryChooseSpawn(out _, out _));
        Assert.True(world.BeginGhost(2));
        Assert.False(world.TryChooseSpawn(out _, out _));
        for (long tick = 0; tick < rules.GhostDecayTicks; tick++) world.Step();
        Assert.True(world.TryChooseSpawn(out Vector2 nextBase, out _));
        Assert.Equal(firstBase, nextBase);
    }

    [Fact]
    public void NeutralChasesAnApproachingUnitAndReturnsAfterItLeavesTheLeash()
    {
        var json = GameRulesTests.Data().Root;
        json["start"]!["unitCount"] = 1;
        json["start"]!["fieldUnitCount"] = 1;
        foreach (var element in json["elements"]!) element["hp"] = 1000;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        Assert.Equal(JoinFailure.None, network.TryJoin(rules.ElementIndex("earth"), out int owner));
        var view = network.World.GetView(owner);
        UnitView original = view.Units.Where(unit => unit.OwnerId < 0)
            .MinBy(unit => Vector2.DistanceSquared(unit.Position, view.LegionPosition));
        Vector2 home = original.Position;
        Vector2 approach = Vector2.Normalize(home - view.LegionPosition);
        UnitView Neutral() => Assert.Single(view.Units, unit => unit.Id == original.Id);
        UnitView Bait() => Assert.Single(view.Units, unit => unit.OwnerId == owner);

        Assert.True(Vector2.Distance(home, Bait().Position) > rules.NeutralAggroRadius);
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 1, approach));
        for (int tick = 0; tick < rules.TickRate * 3 && Neutral().Position == home; tick++) network.Step();
        Assert.NotEqual(home, Neutral().Position);
        Assert.InRange(Vector2.Distance(home, Neutral().Position), 0.001f, rules.LegionSpeed * rules.NeutralMoveSpeedMultiplier * (float)rules.DeltaTime + 0.001f);

        Vector2 retreat = RetreatAroundBase(view, rules, -approach);
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 2, retreat));
        for (int tick = 0; tick < rules.TickRate * 8 && Vector2.Distance(home, Bait().Position) <= rules.NeutralLeashRadius; tick++)
        {
            network.Step();
            Assert.InRange(Vector2.Distance(home, Neutral().Position), 0, rules.NeutralLeashRadius + 0.001f);
        }
        Assert.True(Vector2.Distance(home, Bait().Position) > rules.NeutralLeashRadius);
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 3, Vector2.Zero));
        for (int tick = 0; tick < rules.TickRate * 5; tick++) network.Step();
        Assert.Equal(home, Neutral().Position);
        Assert.Equal(1, view.Population);
    }

    [Fact]
    public void ScriptedNeutralDoesNotPursuePlayersWithinWorldAggroRadius()
    {
        var rules = new GameRules(GameRulesTests.Data());
        var setup = new WorldSetup();
        var home = new Vector2(50, 50);
        var bait = new Vector2(50, 50 + rules.NeutralAggroRadius - 1);
        setup.Players.Add(new PlayerSpawn(1, "earth", new Vector2(50, 75), bait));
        setup.Units.Add(new UnitSpawn(1, "earth", bait, garrison: true));
        setup.Units.Add(new UnitSpawn(-1, "earth", home));
        var world = new SimulationWorld(rules, setup, 1);

        for (int tick = 0; tick < rules.TickRate; tick++) world.Step();
        Assert.Equal(home, Assert.Single(world.GetView(1).Units, unit => unit.OwnerId < 0).Position);
        Assert.Equal(0, world.Metrics.Attacks);
    }

    [Fact]
    public void NormalWorldSpawnsTheFirstCoreWithinStartingBaseVisionAtThreeSeconds()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        Assert.Equal(JoinFailure.None, network.TryJoin(rules.StartingElements[0], out int owner));
        var view = network.World.GetView(owner);

        for (int tick = 1; tick < rules.TickRate * 3; tick++) network.Step();
        Assert.Empty(view.Cores);
        Assert.Equal(0, view.CoreBalance);

        network.Step();
        Assert.Equal(1L, view.CoreBalance + view.Cores.Sum(core => core.Amount));
        Assert.All(view.Cores, core => Assert.InRange(Vector2.Distance(core.Position, view.BasePosition), 0, rules.VisionRadius));
    }

    [Fact]
    public void NormalWorldMapCoresFundAForgeAndItsFirstReserveCanRally()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        int element = rules.ElementIndex("water");
        Assert.Equal(JoinFailure.None, network.TryJoin(element, out int owner));
        var view = network.World.GetView(owner);
        int required = rules.BuildingCost(BuildingKind.NatureForge) + rules.TrainingCost(element, element);
        ulong sequence = 0;

        for (int tick = 0; tick < (required + 30) * rules.TickRate * 6 && view.CoreBalance < required; tick++)
        {
            Vector2 direction = Vector2.Zero;
            if (view.Cores.Count > 0)
            {
                Vector2 target = view.Cores.MinBy(core => Vector2.DistanceSquared(core.Position, view.LegionPosition)).Position;
                direction = target - view.LegionPosition;
                if (CircleCollision.Limit(view.LegionPosition, direction, view.BasePosition, rules.BaseRadius) < 1)
                {
                    Vector2 outward = view.LegionPosition - view.BasePosition;
                    var tangent = new Vector2(-outward.Y, outward.X);
                    direction = Vector2.Dot(direction, tangent) >= 0 ? tangent : -tangent;
                }
            }
            Assert.Equal(CommandAdmission.Accepted, network.Move(owner, ++sequence, direction));
            network.Step();
        }
        Assert.True(view.CoreBalance >= required, $"Core balance {view.CoreBalance}, expected at least {required}.");
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, ++sequence, Vector2.Zero));
        network.Step();

        float distance = rules.BaseRadius + rules.BuildingRadius(BuildingKind.NatureForge) + rules.SlotSpacing;
        BuildingView forge = default;
        foreach (Vector2 side in new[] { -Vector2.UnitY, Vector2.UnitX, -Vector2.UnitX })
        {
            Vector2 cell = (view.BasePosition + side * distance) / rules.BuildGridSize;
            var command = SimCommand.Build(++sequence, BuildingKind.NatureForge, (int)MathF.Round(cell.X), (int)MathF.Round(cell.Y));
            Assert.Equal(CommandAdmission.Accepted, network.Command(owner, command));
            network.Step();
            forge = view.Buildings.SingleOrDefault(building => building.Kind == BuildingKind.NatureForge);
            if (forge.Id != 0) break;
        }
        Assert.True(forge.IsUnderConstruction);
        while (network.World.Metrics.Tick < forge.ConstructionEndTick) network.Step();
        Assert.True(view.NatureForge);
        Assert.Equal(CommandAdmission.Accepted, network.Command(owner, SimCommand.Train(++sequence, "water")));
        for (int tick = 0; tick <= (int)Math.Ceiling(rules.NatureTrainSeconds * rules.TickRate); tick++) network.Step();
        Assert.Equal(1, view.GarrisonCount);
        Assert.Equal(CommandAdmission.Accepted, network.Command(owner, SimCommand.Rally(++sequence)));
        for (int tick = 0; tick < rules.TickRate * 20 && view.GarrisonCount != 0; tick++) network.Step();
        Assert.Equal(1L, view.GarrisonArrivals);
    }

    [Fact]
    public void M4DemoFundsConstructionWithoutPrebuildingFacilities()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var options = ServerOptions.Parse(["--listen", "--scenario", "m4-demo"], rules.MaxPlayers);
        var network = new NetworkWorld(rules, new NetworkSettings(data), options.Scenario);

        Assert.Equal(JoinFailure.None, network.TryJoin(rules.StartingElements[0], out int owner));
        var view = network.World.GetView(owner);
        Assert.Equal(1, view.BaseLevel);
        Assert.Equal(rules.StartUnitCount, view.Population);
        Assert.Equal(BuildingKind.Base, Assert.Single(view.Buildings).Kind);
        Assert.True(view.CoreBalance >= 700);
        Assert.Contains(view.Units, unit => unit.OwnerId < 0);
    }

    [Fact]
    public void NetworkWorldRejectsATowerCapacityBeyondTheBuildingFrameLimit()
    {
        var json = GameRulesTests.Data().Root;
        json["baseLevels"]![1]!["towerSlots"] = GameplayCodec.MaxBuildings;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        Assert.Throws<ArgumentException>(() => new NetworkWorld(rules, new NetworkSettings(data)));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(1, 8)]
    public void ADefeatedNeutralSlotIsReplenishedAtTheConfiguredTick(double conversionChance, double respawnSeconds)
    {
        var json = GameRulesTests.Data().Root;
        json["start"]!["unitCount"] = 1;
        json["start"]!["fieldUnitCount"] = 1;
        json["baseLevels"]![0]!["populationCap"] = 2;
        json["baseLevels"]![0]!["garrisonCap"] = 2;
        json["neutral"]!["respawnSeconds"] = respawnSeconds;
        json["neutral"]!["conversionChance"] = conversionChance;
        json["neutral"]!["attackMultiplier"] = 0.0001;
        foreach (var element in json["elements"]!)
        {
            element["hp"] = 1;
            element["attacksPerSecond"] = 0.1;
        }
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        Assert.Equal(JoinFailure.None, network.TryJoin(rules.ElementIndex("water"), out int owner));
        var view = network.World.GetView(owner);
        int initial = network.World.Metrics.NeutralUnits;
        Vector2 target = view.Units.Where(unit => unit.OwnerId < 0)
            .MinBy(unit => Vector2.DistanceSquared(unit.Position, view.LegionPosition)).Position;
        Vector2 approach = Vector2.Normalize(target - view.LegionPosition);
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 1, approach));
        for (int tick = 0; tick < 4 * rules.TickRate && network.World.Metrics.NeutralUnits == initial; tick++) network.Step();
        int remaining = network.World.Metrics.NeutralUnits;
        Assert.True(remaining < initial);
        Assert.Equal((long)conversionChance, network.World.Metrics.Conversions);
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 2, RetreatAroundBase(view, rules, -approach)));
        int wait = (int)Math.Ceiling(rules.NeutralRespawnSeconds * rules.TickRate);
        for (int tick = 1; tick < wait; tick++)
        {
            if (tick == rules.TickRate * 5)
                Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 3, Vector2.Zero));
            network.Step();
            Assert.True(network.World.Metrics.NeutralUnits <= remaining);
        }
        int beforeRespawn = network.World.Metrics.NeutralUnits;
        network.Step();
        Assert.True(network.World.Metrics.NeutralUnits > beforeRespawn);
    }

    private static Vector2 RetreatAroundBase(WorldView view, GameRules rules, Vector2 retreat)
    {
        Vector2 path = retreat * (rules.NeutralLeashRadius + rules.NeutralAggroRadius);
        if (CircleCollision.Limit(view.LegionPosition, path, view.BasePosition, rules.BaseRadius) >= 1) return retreat;
        Vector2 tangent = new(-retreat.Y, retreat.X);
        float side = Vector2.Dot(view.LegionPosition - view.BasePosition, tangent) < 0 ? -1 : 1;
        return Vector2.Normalize(retreat + tangent * side);
    }

    [Fact]
    public void NeutralRespawnWaitsUntilTheGhostBaseBuildAreaClears()
    {
        var json = GameRulesTests.Data().Root;
        json["start"]!["unitCount"] = 1;
        json["start"]!["fieldUnitCount"] = 1;
        json["neutral"]!["respawnSeconds"] = 3;
        json["neutral"]!["conversionChance"] = 0;
        json["neutral"]!["attackMultiplier"] = 0.0001;
        json["ghost"]!["decaySeconds"] = 5;
        json["ghost"]!["reconnectWindowSeconds"] = 5;
        foreach (var element in json["elements"]!)
        {
            element["hp"] = 1;
            element["attacksPerSecond"] = 0.1;
        }
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        Assert.Equal(JoinFailure.None, network.TryJoin(rules.ElementIndex("water"), out int owner));
        var view = network.World.GetView(owner);
        int initial = network.World.Metrics.NeutralUnits;
        Vector2 target = view.Units.Where(unit => unit.OwnerId < 0)
            .MinBy(unit => Vector2.DistanceSquared(unit.Position, view.LegionPosition)).Position;
        Vector2 approach = Vector2.Normalize(target - view.LegionPosition);
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 1, approach));
        for (int tick = 0; tick < 4 * rules.TickRate && network.World.Metrics.NeutralUnits == initial; tick++) network.Step();
        int remaining = network.World.Metrics.NeutralUnits;
        Assert.True(remaining < initial);

        // 생성 후보 주변만 새 본진의 건설 구역에 포함시켜 건물 자체의 충돌 검사와 구분한다.
        Vector2 outward = Vector2.Normalize(target - view.BasePosition);
        Vector2 ghostBase = target + outward * ((rules.BuildRadius + rules.BaseRadius) * 0.5f);
        Vector2 ghostLegion = ghostBase + outward * rules.VisionRadius * 2;
        Assert.True(network.World.TryAddPlayer(new PlayerSpawn(2, "earth", ghostBase, ghostLegion)));
        Assert.True(network.World.BeginGhost(2));
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 2, RetreatAroundBase(view, rules, -approach)));
        for (long tick = 1; tick < rules.GhostDecayTicks; tick++)
        {
            network.Step();
            Assert.True(network.World.Metrics.NeutralUnits <= remaining);
        }
        Assert.True(network.World.TryGetView(2, out _));
        for (int tick = 0; tick <= 2 * rules.TickRate && network.World.Metrics.NeutralUnits <= remaining; tick++) network.Step();
        Assert.False(network.World.TryGetView(2, out _));
        Assert.True(network.World.Metrics.NeutralUnits > remaining);
    }

    [Fact]
    public void LastDepartureTicksUntilGhostExpiresAndTheSiteCanBeReused()
    {
        var json = GameRulesTests.Data().Root;
        json["server"]!["maxPlayers"] = 1;
        json["ghost"]!["decaySeconds"] = 0.15;
        json["ghost"]!["reconnectWindowSeconds"] = 0.15;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        int element = rules.StartingElements[0];
        Assert.Equal(JoinFailure.None, network.TryJoin(element, out int first));
        Vector2 position = network.World.GetView(first).BasePosition;
        network.Leave(first);

        Assert.Equal(JoinFailure.WorldFull, network.TryJoin(element, out _));
        for (long tick = 0; tick < rules.GhostDecayTicks; tick++) network.Step();
        Assert.Equal(rules.GhostDecayTicks, network.World.Metrics.Tick);
        Assert.False(network.World.TryGetView(first, out _));
        network.Step();
        Assert.Equal(rules.GhostDecayTicks, network.World.Metrics.Tick);
        Assert.Equal(JoinFailure.None, network.TryJoin(element, out int second));
        Assert.True(second > first);
        Assert.Equal(position, network.World.GetView(second).BasePosition);
    }
}
