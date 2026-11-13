using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>신규 경로와 자리의 공급 예산이 재입장·복원으로 늘어나지 않는지 확인한다.</summary>
public sealed class OpeningRouteTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(41)]
    [InlineData(917)]
    public void OpeningRoutesRespectDistancesAndBudget(int seed)
    {
        var rules = GameRulesTests.Rules();
        var world = Fresh(rules, seed);
        JObject snapshot = Snapshot(world);
        var ecology = snapshot["Ecology"]!;
        var sites = ecology["Sites"]!.Select(Position).ToArray();
        Assert.Equal(15, sites.Length);
        Assert.All(ecology["Groups"]!, group => Assert.Equal(-1, (int)group["StarterSite"]!));
        Vector2[] anchors = ecology["Groups"]!.Select(group => Position(group["Anchor"]!)).ToArray();
        Assert.True(anchors.Length >= 2);
        float expectedSpacing = (float)(rules.NeutralWalkSpacingSeconds * rules.LegionSpeed);
        foreach (Vector2 anchor in anchors)
            Assert.InRange(anchors.Where(other => other != anchor).Min(other => Vector2.Distance(anchor, other)),
                expectedSpacing - 0.001f, expectedSpacing + 0.001f);
        Assert.Equal(336f, sites.Skip(1).Min(site => Vector2.Distance(sites[0], site)));
        Assert.Equal(sites.Length, ecology["Schedules"]!.Count());
        var offset = new Vector2(0, rules.BaseRadius + rules.SlotSpacing * (rules.StartFieldUnitCount + 1));
        for (int index = 0; index < sites.Length; index++)
        {
            var schedule = ecology["Schedules"]![index]!;
            Assert.Equal(index, (int)schedule["Site"]!);
            Vector2 start = sites[index] + offset;
            Vector2 near = Position(schedule["NearPosition"]!);
            Vector2 shared = Position(schedule["SharedPosition"]!);
            Assert.InRange(Vector2.Distance(start, near), rules.OpeningFirstCoreMinDistance - 0.001f, rules.OpeningFirstCoreMaxDistance + 0.001f);
            Assert.Contains(anchors, anchor => sites.All(site => CircleCollision.Limit(start, anchor - start, site, rules.BaseRadius) == 1));
            Assert.Contains(sites.Where((_, other) => other != index), other => Vector2.Distance((sites[index] + other) / 2, shared) < 0.01f);
            Assert.All(sites, site => Assert.True(Vector2.Distance(site, shared) > rules.BuildRadius));
            Assert.True((bool)schedule["FirstGranted"]! == false);
        }
        Assert.Contains(ecology["Groups"]!, group => (bool)group["Energy"]!);
        foreach (var group in ecology["Groups"]!.Where(group => (bool)group["Energy"]!))
        {
            float inset = rules.MapSize * (1 - rules.NeutralEnergyZoneFraction) / 2;
            foreach (var slot in group["Slots"]!)
            {
                Vector2 position = Position(snapshot["Units"]!.Single(unit => (int)unit["Id"]! == (int)slot["Unit"]!)["Position"]!);
                Assert.InRange(position.X, inset, rules.MapSize - inset);
                Assert.InRange(position.Y, inset, rules.MapSize - inset);
            }
        }
        Assert.True(JToken.DeepEquals(ecology, Snapshot(Fresh(rules, seed))["Ecology"]));
        Assert.True(world.TryChooseSpawn(out var home, out var legion));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", home, legion)));
        world.Step();
        Assert.Equal(4, TotalCores(world));
        for (int tick = 1; tick < 60 * rules.TickRate; tick++) world.Step();
        var cores = Snapshot(world)["Cores"]!;
        double baselineExpiry = world.Metrics.Tick * rules.DeltaTime + rules.CoreLifetime;
        var baseline = cores.Where(core => (double)core["Expires"]! <= baselineExpiry).ToArray();
        Assert.Equal(20, baseline.Sum(core => (int)core["Amount"]!));
        Assert.Equal(12, baseline.Where(core => Vector2.Distance(Position(core["Position"]!), legion) <= rules.OpeningFirstCoreMaxDistance + 0.01f).Sum(core => (int)core["Amount"]!));
        Assert.Equal(8, baseline.Where(core => Vector2.Distance(Position(core["Position"]!), legion) > rules.OpeningFirstCoreMaxDistance + 0.01f).Sum(core => (int)core["Amount"]!));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    public void AdjacentNeutralAnchorsMatchTheConfiguredWalkingTime(int players)
    {
        GameRules rules = GameRulesTests.Rules().ForRoom(players);
        var world = Fresh(rules, 77);
        var ecology = Snapshot(world)["Ecology"]!;
        Vector2[] sites = ecology["Sites"]!.Select(Position).ToArray();
        Vector2[] anchors = ecology["Groups"]!.Select(group => Position(group["Anchor"]!)).ToArray();
        float expectedDistance = (float)(rules.LegionSpeed * rules.NeutralWalkSpacingSeconds);
        Assert.True(anchors.Length >= 2);
        Assert.All(anchors, anchor => Assert.InRange(anchors.Where(other => other != anchor)
            .Min(other => Vector2.Distance(anchor, other)), expectedDistance - 0.001f, expectedDistance + 0.001f));
        var edge = (from start in anchors
                    from destination in anchors
                    where Math.Abs(Vector2.Distance(start, destination) - expectedDistance) < 0.001f &&
                        sites.All(site => CircleCollision.Limit(start, destination - start, site, rules.BaseRadius) == 1)
                    select (start, destination)).First();
        // 같은 서버 이동을 사용하고, 거리 측정에는 중립 교전 시간을 포함하지 않는다.
        var route = new WorldSetup();
        route.Players.Add(new PlayerSpawn(1, "water", sites[0], edge.start));
        route.Units.Add(new UnitSpawn(1, "water", edge.start));
        var traveler = new SimulationWorld(rules, route, 77);
        Assert.Equal(CommandAdmission.Accepted, traveler.TryEnqueue(1, SimCommand.MoveTo(1, 1, edge.destination)));
        WorldView view = traveler.GetView(1);
        while (traveler.Metrics.Tick < (rules.NeutralWalkSpacingSeconds + 10) * rules.TickRate &&
            Vector2.DistanceSquared(view.LegionPosition, edge.destination) > 0.01f)
            traveler.Step();
        Assert.InRange(Vector2.DistanceSquared(view.LegionPosition, edge.destination), 0, 0.01f);
        Assert.InRange((double)traveler.Metrics.Tick / rules.TickRate,
            rules.NeutralWalkSpacingSeconds - 1, rules.NeutralWalkSpacingSeconds + 10);
    }

    [Fact]
    public void ExplorationCoresAppearAtSeparateSitesAndResumeFromTheSameTick()
    {
        var json = GameRulesTests.Data().Root;
        var opening = json["economy"]!["opening"]!;
        opening["explorationBundlesPerMinute"] = 5;
        opening["explorationCoreLifetimeSeconds"] = 120;
        var rules = new GameRules(new GameDataDocument(json.ToString())).ForRoom(2);
        var world = Fresh(rules, 77);
        var sites = Snapshot(world)["Ecology"]!["Sites"]!.Select(Position).ToArray();
        Assert.True(world.TryChooseSpawn(out var home, out var start));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", home, start)));
        for (int tick = 0; tick < 24 * rules.TickRate; tick++) world.Step();

        JObject before = Snapshot(world);
        var explored = before["Cores"]!.Where(core => (double)core["Expires"]! > 120).ToArray();
        Assert.Equal(2, explored.Length);
        Assert.Equal(2, explored.Select(core => Position(core["Position"]!)).Distinct().Count());
        foreach (var core in explored)
        {
            Vector2 position = Position(core["Position"]!);
            Assert.All(sites, site => Assert.True(Vector2.Distance(position, site) > rules.BuildRadius));
            Assert.Equal(world.GetView(1).CanSeePoint(position), world.GetView(1).Cores.Any(visible => visible.Id == (int)core["Id"]!));
        }
        var resumed = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        for (int tick = 0; tick < 12 * rules.TickRate; tick++)
        {
            world.Step();
            resumed.Step();
        }
        Assert.Equal(3, Snapshot(world)["Cores"]!.Count(core => (double)core["Expires"]! > 120));
        Assert.True(JToken.DeepEquals(Snapshot(world)["Cores"], Snapshot(resumed)["Cores"]));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    public void ANewRoomSharedBundleCanBeReachedBeforeItExpires(int players)
    {
        GameRules rules = GameRulesTests.Rules().ForRoom(players);
        var world = Fresh(rules, 77);
        Assert.True(world.TryChooseSpawn(out var home, out var start));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", home, start)));
        var ecology = Snapshot(world)["Ecology"]!;
        var schedule = ecology["Schedules"]![0]!;
        Vector2 shared = Position(schedule["SharedPosition"]!);
        long spawnedAt = 0;
        int bundleId = 0;
        while (world.Metrics.Tick < 60 * rules.TickRate && spawnedAt == 0)
        {
            long before = world.Metrics.EcologyCoreSpawns;
            world.Step();
            if (world.Metrics.EcologyCoreSpawns == before) continue;
            JToken? bundle = Snapshot(world)["Cores"]!.FirstOrDefault(core => Position(core["Position"]!) == shared);
            if (bundle != null)
            {
                spawnedAt = world.Metrics.Tick;
                bundleId = (int)bundle["Id"]!;
            }
        }
        Assert.True(spawnedAt > 0);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.MoveTo(1, 1, shared)));
        long expiresAt = spawnedAt + (long)Math.Ceiling(rules.CoreLifetime * rules.TickRate);
        while (world.Metrics.Tick < expiresAt && !world.GetView(1).Events.Any(e => e.Kind == SimEventKind.CorePickedUp && e.EntityId == bundleId))
            world.Step();
        Assert.True(world.Metrics.Tick < expiresAt);
        Assert.DoesNotContain(Snapshot(world)["Cores"]!, core => (int)core["Id"]! == bundleId);
    }

    [Fact]
    public void ReenteringSlotDoesNotRefillOpeningResources()
    {
        var json = GameRulesTests.Data().Root;
        json["server"]!["maxPlayers"] = 1;
        json["economy"]!["opening"]!["explorationBundlesPerMinute"] = 0;
        json["economy"]!["droppedCoreLifetimeSeconds"] = 3600;
        json["ghost"]!["decaySeconds"] = json["ghost"]!["reconnectWindowSeconds"] = 0.15;
        var rules = new GameRules(new GameDataDocument(json.ToString()));
        var continuous = Fresh(rules, 11);
        var reused = Fresh(rules, 11);
        Assert.True(continuous.TryChooseSpawn(out var home, out var start));
        Assert.True(continuous.TryAddPlayer(new PlayerSpawn(1, "water", home, start)));
        Assert.True(reused.TryAddPlayer(new PlayerSpawn(1, "water", home, start)));
        for (int tick = 0; tick < 100; tick++) { continuous.Step(); reused.Step(); }
        var before = Snapshot(reused)["Ecology"]!["Schedules"]![0]!.DeepClone();
        Assert.True(reused.BeginGhost(1));
        for (int tick = 0; tick < 301; tick++) reused.Step();
        Assert.Equal(before["Credit"], Snapshot(reused)["Ecology"]!["Schedules"]![0]!["Credit"]);
        Assert.True(reused.TryChooseSpawn(out var sameHome, out var sameStart));
        Assert.Equal(home, sameHome);
        Assert.True(reused.TryAddPlayer(new PlayerSpawn(99, "earth", sameHome, sameStart)));
        for (int tick = 0; tick < 1100; tick++) { continuous.Step(); reused.Step(); }
        var expected = Snapshot(continuous)["Ecology"]!["Schedules"]![0]!;
        var actual = Snapshot(reused)["Ecology"]!["Schedules"]![0]!;
        Assert.Equal(expected["Credit"], actual["Credit"]);
        Assert.Equal(expected["Phase"], actual["Phase"]);
        Assert.Equal((long)expected["NextTick"]! + 301, (long)actual["NextTick"]!);
        Assert.Equal(20, TotalCores(continuous));
        Assert.Equal(TotalCores(continuous), TotalCores(reused));
    }

    [Fact]
    public void DiskAndMigrationPreserveSiteBudgetAndActualCoordinates()
    {
        var rules = GameRulesTests.Rules();
        var world = Fresh(rules, 41);
        Assert.True(world.TryChooseSpawn(out var home, out var start));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", home, start)));
        for (int tick = 0; tick < 101; tick++) world.Step();
        var before = Snapshot(world);
        var disk = SimulationWorld.RestoreCheckpoint(rules, before.ToObject<WorldCheckpoint>()!);
        var migrated = SimulationWorld.RestoreMigrationState(rules, JObject.FromObject(world.CaptureMigrationState()).ToObject<MigrationWorldState>()!);
        Assert.True(JToken.DeepEquals(before["Ecology"], Snapshot(disk)["Ecology"]));
        foreach (string field in new[] { "Units", "Buildings", "Cores" })
            Assert.True(JToken.DeepEquals(before[field], Snapshot(disk)[field]));
        Assert.True(disk.TryResumeGhost(1));
        disk.Step();
        Assert.Equal(4, TotalCores(disk));
        Assert.Equal(before["Ecology"]!["Schedules"]![0]!["Phase"], Snapshot(disk)["Ecology"]!["Schedules"]![0]!["Phase"]);
        for (int tick = 0; tick < 400; tick++) { world.Step(); migrated.Step(); }
        Assert.True(JToken.DeepEquals(Snapshot(world), Snapshot(migrated)));
        foreach (string field in new[] { "Site", "NearPosition", "SharedPosition", "Credit", "Phase", "FirstGranted", "LastTick", "NextTick" })
        {
            var invalid = before.DeepClone();
            ((JObject)invalid["Ecology"]!["Schedules"]![0]!).Remove(field);
            Assert.ThrowsAny<Exception>(() => SimulationWorld.RestoreCheckpoint(rules, invalid.ToObject<WorldCheckpoint>()!));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(15)]
    public void RoomRoutesKeepBoundsAndClearBothApproaches(int players)
    {
        var rules = GameRulesTests.Rules().ForRoom(players);
        var world = Fresh(rules, 77);
        var json = Snapshot(world);
        var ecology = json["Ecology"]!;
        var sites = ecology["Sites"]!.Select(Position).ToArray();
        var offset = new Vector2(0, rules.BaseRadius + rules.SlotSpacing * (rules.StartFieldUnitCount + 1));
        foreach (var unit in json["Units"]!)
        {
            Vector2 position = Position(unit["Position"]!);
            Assert.InRange(position.X, 0, rules.MapSize);
            Assert.InRange(position.Y, 0, rules.MapSize);
            foreach (var site in sites)
            {
                Assert.True(Vector2.Distance(site, position) > rules.BuildRadius);
                Assert.True(Vector2.Distance(site + offset, position) > rules.NeutralAggroRadius + rules.SlotSpacing * (rules.StartFieldUnitCount - 1) / 2);
            }
        }
        foreach (var schedule in ecology["Schedules"]!)
        {
            int site = (int)schedule["Site"]!;
            var shared = Position(schedule["SharedPosition"]!);
            int other = Array.FindIndex(sites, value => Vector2.Distance((value + sites[site]) / 2, shared) < 0.001f);
            Assert.True(other >= 0 && other != site);
            foreach (var obstacle in sites)
            {
                Assert.Equal(1f, CircleCollision.Limit(sites[site] + offset, shared - sites[site] - offset, obstacle, rules.BaseRadius));
                Assert.Equal(1f, CircleCollision.Limit(sites[other] + offset, shared - sites[other] - offset, obstacle, rules.BaseRadius));
            }
        }
        for (int index = 0; index < players; index++)
        {
            Assert.True(world.TryChooseSpawn(out var home, out var start));
            Assert.True(world.TryAddPlayer(new PlayerSpawn(index + 1, "water", home, start)));
        }
        world.Step();
        Assert.Equal(0, world.Metrics.Attacks);
        Assert.Equal(players * 4, TotalCores(world));
    }

    [Fact]
    public void ImpossibleOpeningGeometryIsAtomicAndDoesNotAwardCompensation()
    {
        var data = GameRulesTests.Data().Root;
        data["construction"]!["buildRadius"] = 1000;
        data["economy"]!["opening"]!["explorationBundlesPerMinute"] = 0;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var setup = new WorldSetup();
        setup.Players.Add(new PlayerSpawn(1, "water", new Vector2(336), new Vector2(336, 345), cores: 73, natureForge: true));
        setup.Units.Add(new UnitSpawn(1, "water", new Vector2(336, 345)));
        setup.Cores.Add(new CoreSpawn(new Vector2(315, 345), 7));
        var world = new SimulationWorld(rules, setup, 1);
        Assert.Equal(CommandAdmission.Accepted, world.TryEnqueue(1, SimCommand.Train(1, "water")));
        world.Step();
        var before = JObject.FromObject(world.CaptureMigrationState());
        Assert.Throws<ArgumentException>(() => world.EnableEcology(rules.RoomMinBaseDistance));
        Assert.True(JToken.DeepEquals(before, JObject.FromObject(world.CaptureMigrationState())));
        Assert.False(world.TryChooseSpawn(out _, out _));
        Assert.Equal(7, TotalCores(world));
        Assert.Equal(1, world.GetView(1).ReservedPopulation);
    }

    [Fact]
    public void DisabledSupplyNeverGrantsAnOpeningBundle()
    {
        var data = GameRulesTests.Data().Root;
        data["economy"]!["mapCoreSpawnPerPlayerPerMinute"] = 0;
        data["economy"]!["opening"]!["nearbyBundlesPerMinute"] = 0;
        data["economy"]!["opening"]!["sharedBundlesPerMinute"] = 0;
        data["economy"]!["opening"]!["explorationBundlesPerMinute"] = 0;
        var rules = new GameRules(new GameDataDocument(data.ToString()));
        var world = Fresh(rules, 11);
        Assert.True(world.TryChooseSpawn(out var home, out var start));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(1, "water", home, start)));
        for (int tick = 0; tick < rules.TickRate * 60; tick++) world.Step();
        Assert.Equal(0, TotalCores(world));
        var restored = SimulationWorld.RestoreMigrationState(rules, world.CaptureMigrationState());
        restored.Step();
        Assert.Equal(0, TotalCores(restored));
    }

    [Fact]
    public void NonSiteNewPlayersAreRejectedBeforeMutationAndSavedSitesRemainAuthoritative()
    {
        var rules = GameRulesTests.Rules();
        var world = Fresh(rules, 1);
        var before = JObject.FromObject(world.CaptureMigrationState());
        Assert.False(world.TryAddPlayer(new PlayerSpawn(1, "water", new Vector2(200), new Vector2(220))));
        Assert.True(JToken.DeepEquals(before, JObject.FromObject(world.CaptureMigrationState())));
        var saved = Snapshot(world);
        Vector2 actual = Position(saved["Ecology"]!["Sites"]![0]!) + new Vector2(5, 5);
        saved["Ecology"]!["Sites"]![0] = JToken.FromObject(actual);
        var restored = SimulationWorld.RestoreCheckpoint(rules, saved.ToObject<WorldCheckpoint>()!);
        Assert.True(restored.TryChooseSpawn(out var home, out var start));
        Assert.Equal(actual, home);
        Assert.True(restored.TryAddPlayer(new PlayerSpawn(1, "water", home, start)));
        restored.Step();
        Assert.Equal(4, TotalCores(restored));
    }

    [Fact]
    public void SupplyTracksActiveParticipantsWithoutBankingEmptySites()
    {
        var data = GameRulesTests.Data().Root;
        data["economy"]!["droppedCoreLifetimeSeconds"] = 3600;
        data["economy"]!["opening"]!["explorationBundlesPerMinute"] = 0;
        var rules = new GameRules(new GameDataDocument(data.ToString())).ForRoom(4);
        var world = Fresh(rules, 41);
        for (int owner = 1; owner <= 2; owner++)
        {
            Assert.True(world.TryChooseSpawn(out var home, out var start));
            Assert.True(world.TryAddPlayer(new PlayerSpawn(owner, "water", home, start)));
        }
        for (int tick = 0; tick < 30 * rules.TickRate; tick++) world.Step();
        Assert.True(world.TryChooseSpawn(out var laterHome, out var laterStart));
        Assert.True(world.TryAddPlayer(new PlayerSpawn(3, "water", laterHome, laterStart)));
        for (int tick = 0; tick < 30 * rules.TickRate; tick++) world.Step();
        Assert.Equal(20 + 20 + 8, TotalCores(world));
        var schedules = Snapshot(world)["Ecology"]!["Schedules"]!;
        var empty = Assert.Single(schedules, item => !(bool)item["FirstGranted"]!);
        Assert.Equal(0, (long)empty["Credit"]!);
        Assert.Equal(0, (int)empty["Phase"]!);
    }

    internal static SimulationWorld Fresh(GameRules rules, int seed)
    {
        var world = new SimulationWorld(rules, new WorldSetup(), seed);
        world.EnableEcology(rules.RoomMinBaseDistance);
        return world;
    }

    internal static JObject Snapshot(SimulationWorld world) => JObject.FromObject(world.CaptureCheckpoint());
    internal static Vector2 Position(JToken item) => item.ToObject<Vector2>();
    private static int TotalCores(SimulationWorld world) => Snapshot(world)["Cores"]!.Sum(core => (int)core["Amount"]!);
}
