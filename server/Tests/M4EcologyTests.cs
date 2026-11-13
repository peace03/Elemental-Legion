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
            Assert.True(scaled.MapSize >= previousSize);
            Assert.Equal(Math.Min(rules.MapSize, (float)Math.Round(720 * Math.Sqrt(players / 2.0))), scaled.MapSize);
            Assert.Equal(rules.DataHash, scaled.DataHash);
            var room = new NetworkWorld(scaled, settings);
            int groups = OpeningRouteTests.Snapshot(room.World)["Ecology"]!["Groups"]!.Count();
            Assert.True(groups >= 2);
            Assert.InRange(room.World.Metrics.NeutralUnits, groups, groups * 4);
            for (int index = 0; index < players; index++)
                Assert.Equal(JoinFailure.None, room.TryJoin(rules.StartingElements[0], out _));
            Assert.Equal(JoinFailure.WorldFull, room.TryJoin(rules.StartingElements[0], out _));
            previousSize = scaled.MapSize;
        }
    }

    [Fact]
    public void NormalWorldStartsWithSeededNeutralGroupsOutsideAttackRange()
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
        var neutral = NearestNeutrals(first.World, view.LegionPosition);
        Assert.Equal(view.BasePosition, sameSeed.BasePosition);
        Assert.Equal(neutral, NearestNeutrals(second.World, sameSeed.LegionPosition));
        Assert.InRange(neutral.Length, 1, 4);
        Assert.All(neutral, unit =>
        {
            Assert.True(Vector2.Distance(unit.Position, view.BasePosition) > rules.BuildRadius);
            Assert.True(Vector2.Distance(unit.Position, view.LegionPosition) > rules.VisionRadius);
        });
        foreach (var unit in OpeningRouteTests.Snapshot(first.World)["Units"]!)
            if ((int)unit["Owner"]! < 0)
                Assert.Equal(view.CanSeePoint(OpeningRouteTests.Position(unit["Position"]!)), view.Units.Any(visible => visible.Id == (int)unit["Id"]!));
        Assert.Equal(rules.StartUnitCount, view.Population);
        Assert.Equal(0, view.CoreBalance);
        Assert.False(view.NatureForge);

        first.Step();
        Assert.Equal(0, first.World.Metrics.Attacks);
        Assert.Equal(neutral, NearestNeutrals(first.World, view.LegionPosition));
    }

    [Fact]
    public void NetworkWorldUsesWalkingSpacingAndConfiguredGroupSize()
    {
        var json = GameRulesTests.Data().Root;
        json["neutral"]!["soloChance"] = 0;
        json["neutral"]!["groupSizeMin"] = 3;
        json["neutral"]!["groupSizeMax"] = 3;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));

        int groups = OpeningRouteTests.Snapshot(network.World)["Ecology"]!["Groups"]!.Count();
        Assert.Equal(groups * 3, network.World.Metrics.NeutralUnits);
        json["neutral"]!["walkSpacingSeconds"] = 90;
        var sparseData = new GameDataDocument(json.ToString());
        var sparseWorld = new NetworkWorld(new GameRules(sparseData), new NetworkSettings(sparseData));
        Assert.True(sparseWorld.World.Metrics.NeutralUnits < network.World.Metrics.NeutralUnits);
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
        json["economy"]!["opening"]!["explorationBundlesPerMinute"] = 0;
        json["neutral"]!["walkSpacingSeconds"] = 42000;
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
            ? firstLegion + Vector2.UnitX * (rules.SpawnMinEnemyUnitDistance + rules.SlotSpacing * ((rules.StartFieldUnitCount - 1) * 0.5f + 0.1f))
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
        json["start"]!["garrisonUnitCount"] = 0;
        foreach (var element in json["elements"]!) element["hp"] = 1000;
        var data = new GameDataDocument(json.ToString());
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        Assert.Equal(JoinFailure.None, network.TryJoin(rules.ElementIndex("earth"), out int owner));
        var view = network.World.GetView(owner);
        var original = NearestNeutrals(network.World, view.LegionPosition)
            .MinBy(unit => Vector2.DistanceSquared(unit.Position, view.LegionPosition));
        Vector2 home = original.Position;
        Vector2 approach = Vector2.Normalize(home - view.LegionPosition);
        Vector2 Neutral() => OpeningRouteTests.Position(OpeningRouteTests.Snapshot(network.World)["Units"]!.Single(unit => (int)unit["Id"]! == original.Id)["Position"]!);
        UnitView Bait() => Assert.Single(view.Units, unit => unit.OwnerId == owner);

        Assert.True(Vector2.Distance(home, Bait().Position) > rules.NeutralAggroRadius);
        Assert.Equal(CommandAdmission.Accepted, network.Command(owner, SimCommand.MoveTo(1, 1, home)));
        for (int tick = 0; tick < rules.TickRate * 90 && Neutral() == home; tick++) network.Step();
        Assert.NotEqual(home, Neutral());
        Assert.InRange(Vector2.Distance(home, Neutral()), 0.001f, rules.LegionSpeed * rules.NeutralMoveSpeedMultiplier * (float)rules.DeltaTime + 0.001f);

        Vector2 retreat = RetreatAroundBase(view, rules, -approach);
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 2, retreat));
        for (int tick = 0; tick < rules.TickRate * 8 && Vector2.Distance(home, Bait().Position) <= rules.NeutralLeashRadius; tick++)
        {
            network.Step();
            Assert.InRange(Vector2.Distance(home, Neutral()), 0, rules.NeutralLeashRadius + 0.001f);
        }
        Assert.True(Vector2.Distance(home, Bait().Position) > rules.NeutralLeashRadius);
        Assert.Equal(CommandAdmission.Accepted, network.Move(owner, 3, Vector2.Zero));
        for (int tick = 0; tick < rules.TickRate * 5; tick++) network.Step();
        Assert.Equal(home, Neutral());
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
    public void NormalWorldSpawnsTheInitialBundleAndChargesItToTheSiteBudget()
    {
        var data = GameRulesTests.Data();
        var rules = new GameRules(data);
        var network = new NetworkWorld(rules, new NetworkSettings(data));
        Assert.Equal(JoinFailure.None, network.TryJoin(rules.StartingElements[0], out int owner));
        var view = network.World.GetView(owner);
        Assert.Empty(view.Cores);
        Assert.Equal(0, view.CoreBalance);
        network.Step();
        Assert.Equal(4, view.Cores.Sum(core => core.Amount));
        Assert.All(view.Cores, core => Assert.InRange(Vector2.Distance(core.Position, view.LegionPosition),
            rules.OpeningFirstCoreMinDistance, rules.OpeningFirstCoreMaxDistance));
        for (int tick = 1; tick < 12 * rules.TickRate; tick++) network.Step();
        Assert.Equal(4, view.CoreBalance + view.Cores.Sum(core => core.Amount));
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

        float distance = rules.BuildRadius - rules.BuildingRadius(BuildingKind.NatureForge) - rules.BuildGridSize;
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
        Assert.Equal(rules.StartGarrisonUnitCount + 1, view.GarrisonCount);
        Assert.Equal(CommandAdmission.Accepted, network.Command(owner, SimCommand.Rally(++sequence)));
        for (int tick = 0; tick < rules.TickRate * 20 && view.GarrisonCount != 0; tick++) network.Step();
        Assert.Equal((long)rules.StartGarrisonUnitCount + 1, view.GarrisonArrivals);
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
    public void GameRulesRejectATowerCapacityBeyondTheBuildingFrameLimit()
    {
        var json = GameRulesTests.Data().Root;
        json["baseLevels"]![1]!["towerSlots"] = GameplayCodec.MaxBuildings;
        var data = new GameDataDocument(json.ToString());
        var error = Assert.Throws<ArgumentException>(() => new GameRules(data));
        Assert.Contains("towerSlots", error.Message);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(1, 8)]
    public void ADefeatedNeutralSlotIsReplenishedAtTheConfiguredTick(double conversionChance, double respawnSeconds)
    {
        var json = GameRulesTests.Data().Root;
        json["start"]!["unitCount"] = 1;
        json["start"]!["fieldUnitCount"] = 1;
        json["start"]!["garrisonUnitCount"] = 0;
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
        Vector2 target = NearestNeutrals(network.World, view.LegionPosition)
            .MinBy(unit => Vector2.DistanceSquared(unit.Position, view.LegionPosition)).Position;
        Vector2 approach = Vector2.Normalize(target - view.LegionPosition);
        Assert.Equal(CommandAdmission.Accepted, network.Command(owner, SimCommand.MoveTo(1, 1, target)));
        for (int tick = 0; tick < 90 * rules.TickRate && network.World.Metrics.NeutralUnits == initial; tick++) network.Step();
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

    private static (int Id, int Element, Vector2 Position)[] NearestNeutrals(SimulationWorld world, Vector2 from)
    {
        var snapshot = OpeningRouteTests.Snapshot(world);
        var ecology = snapshot["Ecology"]!;
        var nearest = ecology["Groups"]!.MinBy(group => Vector2.DistanceSquared(from, OpeningRouteTests.Position(group["Anchor"]!)))!;
        var ids = nearest["Slots"]!.Select(slot => (int)slot["Unit"]!).ToHashSet();
        return snapshot["Units"]!.Where(unit => ids.Contains((int)unit["Id"]!))
            .Select(unit => ((int)unit["Id"]!, (int)unit["Element"]!, OpeningRouteTests.Position(unit["Position"]!))).ToArray();
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
    public void NeutralRespawnKeepsGhostAndVacantSiteBuildAreasClear()
    {
        var json = GameRulesTests.Data().Root;
        json["start"]!["unitCount"] = 1;
        json["start"]!["fieldUnitCount"] = 1;
        json["start"]!["garrisonUnitCount"] = 0;
        json["neutral"]!["respawnSeconds"] = 3;
        json["neutral"]!["groupSizeMin"] = json["neutral"]!["groupSizeMax"] = 2;
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
        Vector2 target = NearestNeutrals(network.World, view.LegionPosition)
            .MinBy(unit => Vector2.DistanceSquared(unit.Position, view.LegionPosition)).Position;
        Vector2 approach = Vector2.Normalize(target - view.LegionPosition);
        Assert.Equal(CommandAdmission.Accepted, network.Command(owner, SimCommand.MoveTo(1, 1, target)));
        for (int tick = 0; tick < 90 * rules.TickRate && network.World.Metrics.NeutralUnits == initial; tick++) network.Step();
        int remaining = network.World.Metrics.NeutralUnits;
        Assert.True(remaining < initial);

        // 생성 후보 주변만 새 본진의 건설 구역에 포함시켜 건물 자체의 충돌 검사와 구분한다.
        Vector2 outward = Vector2.Normalize(target - view.BasePosition);
        Vector2 ghostBase = target + outward * ((rules.BuildRadius + rules.BaseRadius) * 0.5f);
        Vector2 ghostLegion = ghostBase + outward * rules.VisionRadius * 2;
        var saved = OpeningRouteTests.Snapshot(network.World);
        var sites = saved["Ecology"]!["Sites"]!;
        int vacant = Enumerable.Range(0, sites.Count()).First(index => OpeningRouteTests.Position(sites[index]!) != view.BasePosition);
        sites[vacant] = Newtonsoft.Json.Linq.JToken.FromObject(ghostBase);
        network = new NetworkWorld(rules, new NetworkSettings(data), saved.ToObject<WorldCheckpoint>()!, 2);
        Assert.True(network.TryResume(owner));
        view = network.World.GetView(owner);
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
        Assert.Equal(remaining, network.World.Metrics.NeutralUnits);
        // 비어 있는 입장 자리도 본진 건설 구역을 예약하므로 기존 앵커 안에서는 계속 기다린다.
        Assert.Contains(OpeningRouteTests.Snapshot(network.World)["Ecology"]!["Groups"]!
            .SelectMany(group => group["Slots"]!), slot => (int)slot["Unit"]! == 0 && (bool)slot["Reserved"]!);
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
