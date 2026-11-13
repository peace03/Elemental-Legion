using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>시드로 정한 입장 위치와 중립 자리의 수명, 자리별 맵 핵을 관리한다.</summary>
    internal sealed class WorldEcology
    {
        private const int PlacementAttempts = 64;
        private static readonly (int Column, int Row)[] LatticeNeighbors =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1), (-1, 1), (1, -1)
        };
        private readonly WorldState world;
        private readonly Vector2[] sites;
        private readonly NeutralGroup[] groups;
        private readonly CoreSchedule[] coreSchedules;
        private readonly Vector2 legionOffset;
        private readonly float minBaseDistance;
        private readonly float neutralBuildClearanceSquared;
        private readonly float starterClearance;
        private readonly long respawnTicks;
        private readonly long coreThreshold;

        internal WorldEcology(WorldState world, float minBaseDistance, WorldCheckpoint.EcologyData saved = null, bool legacyPlacement = false)
        {
            this.world = world;
            GameRules rules = world.Rules;
            int siteCount = rules.RoomPlayers == 0 ? rules.MaxPlayers : rules.RoomPlayers;
            int columns = (int)Math.Ceiling(Math.Sqrt(siteCount));
            int rows = (siteCount + columns - 1) / columns;
            float spacing = rules.MapSize / (columns + 1);
            if (float.IsNaN(minBaseDistance) || float.IsInfinity(minBaseDistance) || minBaseDistance <= 0 || spacing < minBaseDistance)
                throw new ArgumentException("현재 맵의 입장 위치에서 최소 본진 간격을 유지할 수 없습니다.", nameof(minBaseDistance));
            this.minBaseDistance = minBaseDistance;
            float neutralBuildClearance = Math.Max(rules.BuildRadius, rules.BaseRadius + rules.MinimumSeparation);
            neutralBuildClearanceSquared = neutralBuildClearance * neutralBuildClearance;
            legionOffset = new Vector2(0, rules.BaseRadius + rules.SlotSpacing * (rules.StartFieldUnitCount + 1));
            if (spacing < legionOffset.Y)
                throw new ArgumentException("입장 군단을 놓을 맵 여유가 없습니다.");
            sites = new Vector2[siteCount];
            for (int index = 0; index < sites.Length; index++)
            {
                int row = index / columns;
                int rowCount = Math.Min(columns, siteCount - row * columns);
                sites[index] = rules.RoomPlayers == 0
                    ? new Vector2((index % columns + 1) * spacing, (row + 1) * spacing)
                    : new Vector2((index % columns + 1) * rules.MapSize / (rowCount + 1), (row + 1) * rules.MapSize / (rows + 1));
            }

            var nature = new int[rules.Elements.Count];
            var energy = new int[rules.Elements.Count];
            int natureCount = 0, energyCount = 0;
            float natureRange = 0;
            for (int index = 0; index < rules.Elements.Count; index++)
            {
                if (rules.Elements[index].Group == ElementGroup.Nature)
                {
                    nature[natureCount++] = index;
                    natureRange = Math.Max(natureRange, rules.Elements[index].Range);
                }
                else energy[energyCount++] = index;
            }
            starterClearance = Math.Max(rules.NeutralAggroRadius, natureRange + rules.LeashDistance) +
                rules.SlotSpacing * Math.Max(1, (rules.StartFieldUnitCount - 1) * 0.5f);
            respawnTicks = checked((long)Math.Ceiling(rules.NeutralRespawnSeconds * rules.TickRate));
            coreThreshold = 60L * rules.TickRate * rules.OpeningCoreBundleSize;
            coreSchedules = new CoreSchedule[sites.Length];

            long neutralBudget = Net.Protocol.MaxUnits - (long)sites.Length * rules.MaxPopulation;
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId < 0 && !unit.EcologyNeutral) neutralBudget--;
            if (saved != null)
            {
                WorldCheckpoint.Check(saved.Groups != null && saved.Groups.Length > 0 && saved.Groups.Length <= neutralBudget, "중립 무리 용량");
                groups = new NeutralGroup[saved.Groups.Length];
                Restore(saved, neutralBudget);
                return;
            }
            if (neutralBudget < 2L * rules.NeutralMaxGroupSize)
                throw new ArgumentException("플레이어 인구를 예약한 뒤 두 중립 무리를 배치할 공간이 없습니다.");
            for (int index = sites.Length - 1; index > 0; index--)
            {
                int other = world.Random.Next(index + 1);
                (sites[index], sites[other]) = (sites[other], sites[index]);
            }
            foreach (PlayerState player in world.Players)
                if (!ContainsSite(player.BasePosition)) throw new ArgumentException("기존 본진이 생태 입장 자리에 없습니다.");
            PrepareSupplyRoutes();

            groups = legacyPlacement
                ? PlaceLegacyGroups(neutralBudget, nature, natureCount, energy, energyCount)
                : PlaceInitialGroups(neutralBudget, nature, natureCount, energy, energyCount);
            // 기하 탐색이 모두 성공한 뒤에만 기존 월드에 중립을 추가한다.
            foreach (NeutralGroup group in groups)
                for (int slot = 0; slot < group.Slots.Length; slot++)
                    group.Slots[slot].Unit = SpawnNeutral(group.Element, group.Anchor + group.Slots[slot].Offset);
        }

        internal WorldCheckpoint.EcologyData Capture()
        {
            var snapshot = new WorldCheckpoint.EcologyData
            {
                MinBaseDistance = minBaseDistance, Sites = (Vector2[])sites.Clone(),
                Groups = new WorldCheckpoint.GroupData[groups.Length],
                Schedules = new WorldCheckpoint.ScheduleData[coreSchedules.Length]
            };
            for (int index = 0; index < groups.Length; index++)
            {
                NeutralGroup group = groups[index];
                var slots = new WorldCheckpoint.SlotData[group.Slots.Length];
                for (int slot = 0; slot < slots.Length; slot++)
                    slots[slot] = new WorldCheckpoint.SlotData { Unit = group.Slots[slot].Unit?.Id ?? 0,
                        ReadyTick = group.Slots[slot].ReadyTick, Reserved = group.Slots[slot].Reserved };
                snapshot.Groups[index] = new WorldCheckpoint.GroupData
                {
                    Element = group.Element, StarterSite = group.StarterSite, Energy = group.Energy, Anchor = group.Anchor, Slots = slots
                };
            }
            for (int index = 0; index < coreSchedules.Length; index++)
                snapshot.Schedules[index] = new WorldCheckpoint.ScheduleData
                {
                    Site = index, NearPosition = coreSchedules[index].NearPosition, SharedPosition = coreSchedules[index].SharedPosition,
                    Credit = coreSchedules[index].Credit, Phase = coreSchedules[index].Phase, FirstGranted = coreSchedules[index].FirstGranted,
                    LastTick = coreSchedules[index].LastTick, NextTick = coreSchedules[index].NextTick
                };
            return snapshot;
        }

        private void Restore(WorldCheckpoint.EcologyData saved, long neutralBudget)
        {
            GameRules rules = world.Rules;
            WorldCheckpoint.Check(saved.Sites != null && saved.Sites.Length == sites.Length &&
                saved.Groups != null && saved.Groups.Length == groups.Length &&
                saved.Schedules != null && saved.Schedules.Length == coreSchedules.Length, "생태 배열 크기");
            var remainingSites = new HashSet<Vector2>();
            foreach (Vector2 site in saved.Sites)
                WorldCheckpoint.Check(WorldCheckpoint.Finite(site.X) && WorldCheckpoint.Finite(site.Y) &&
                    InsideWorld(site) && InsideWorld(site + legionOffset) && remainingSites.Add(site), "입장 위치");
            saved.Sites.CopyTo(sites, 0);
            foreach (PlayerState player in world.Players)
                WorldCheckpoint.Check(ContainsSite(player.BasePosition), "저장된 본진의 생태 자리 누락");

            var liveNeutrals = new HashSet<int>();
            var starterSites = new HashSet<int>();
            for (int index = 0; index < groups.Length; index++)
            {
                WorldCheckpoint.GroupData item = saved.Groups[index];
                bool starter = item.StarterSite >= 0;
                int count = item.Slots?.Length ?? 0;
                neutralBudget -= count;
                // 이전 월드의 시작 자리와 무리 순서는 저장된 값을 사용한다.
                WorldCheckpoint.Check(item.StarterSite >= -1 && item.StarterSite < sites.Length &&
                    (!starter || !item.Energy && starterSites.Add(item.StarterSite)) &&
                    item.Element >= 0 && item.Element < rules.Elements.Count &&
                    rules.Elements[item.Element].Group == (item.Energy ? ElementGroup.Energy : ElementGroup.Nature) &&
                    WorldCheckpoint.Finite(item.Anchor.X) && WorldCheckpoint.Finite(item.Anchor.Y) &&
                    count > 0 && neutralBudget >= 0 &&
                    (count >= rules.NeutralMinGroupSize && count <= rules.NeutralMaxGroupSize || !starter && count == rules.NeutralSoloSize),
                    "중립 무리");
                var group = new NeutralGroup(item.Element, item.StarterSite, item.Energy, count, rules.SlotSpacing) { Anchor = item.Anchor };
                groups[index] = group;
                for (int slot = 0; slot < count; slot++)
                {
                    WorldCheckpoint.SlotData data = item.Slots[slot];
                    WorldCheckpoint.Check(data.Unit >= 0 && data.ReadyTick >= 0 &&
                        data.ReadyTick - world.Tick <= Math.Max(respawnTicks, rules.TickRate), "중립 재생성 시각");
                    if (data.Unit != 0)
                    {
                        WorldCheckpoint.Check(data.Reserved != true, "생존 중립의 중복 예약");
                        WorldCheckpoint.Check(world.UnitById.TryGetValue(data.Unit, out UnitState unit) && unit.OwnerId == -1 &&
                            unit.EcologyNeutral && unit.Element == item.Element && liveNeutrals.Add(data.Unit), "중립 자리 참조");
                        group.Slots[slot].Unit = unit;
                    }
                    else if (data.Reserved != false)
                    {
                        WorldCheckpoint.Check(world.TryReserveUnitSlots(1), "중립 재생성 예약 용량");
                        group.Slots[slot].Reserved = true;
                    }
                    group.Slots[slot].ReadyTick = data.ReadyTick;
                }
            }
            foreach (UnitState unit in world.Units)
                WorldCheckpoint.Check(!unit.EcologyNeutral || unit.OwnerId >= 0 || liveNeutrals.Contains(unit.Id), "중립 자리 누락");
            for (int index = 0; index < coreSchedules.Length; index++)
            {
                WorldCheckpoint.ScheduleData item = saved.Schedules[index];
                WorldCheckpoint.Check(item.Site == index && item.Credit >= -coreThreshold && item.Credit <= coreThreshold &&
                    item.Phase >= 0 && item.Phase < Math.Max(1, rules.OpeningNearbyBundlesPerMinute + rules.OpeningSharedBundlesPerMinute) &&
                    item.LastTick == world.Tick && item.NextTick == NextSupplyTick(item.Credit, item.FirstGranted, item.LastTick) &&
                    (item.FirstGranted || item.Credit == 0 && item.Phase == 0) &&
                    ValidPosition(item.NearPosition) && ValidPosition(item.SharedPosition), "자리별 맵 핵 생성 일정");
                coreSchedules[index] = new CoreSchedule { NearPosition = item.NearPosition, SharedPosition = item.SharedPosition,
                    Credit = item.Credit, Phase = item.Phase, FirstGranted = item.FirstGranted,
                    LastTick = item.LastTick, NextTick = item.NextTick };
            }
        }

        internal bool ContainsSite(Vector2 basePosition) => Array.IndexOf(sites, basePosition) >= 0;

        internal bool TryChooseSpawn(out Vector2 basePosition, out Vector2 legionPosition)
        {
            float safeDistanceSquared = starterClearance * starterClearance;
            foreach (Vector2 site in sites)
            {
                Vector2 legion = site + legionOffset;
                if (!IsSafePlayerSpawn(world, site, legion, minBaseDistance)) continue;
                if (!RecallSystem.TryCreateLayout(world, site, out _)) continue;
                bool blocked = false;
                foreach (UnitState unit in world.Units)
                    if (unit.OwnerId < 0 && unit.Health > 0 && Vector2.DistanceSquared(unit.Position, legion) < safeDistanceSquared)
                    {
                        blocked = true;
                        break;
                    }
                if (blocked) continue;
                basePosition = site;
                legionPosition = legion;
                return true;
            }
            basePosition = legionPosition = default;
            return false;
        }

        internal static bool IsSafePlayerSpawn(WorldState world, Vector2 basePosition, Vector2 legionPosition, float minBaseDistance)
        {
            if (RecallSystem.Reserved(world, basePosition, world.Rules.BaseRadius)) return false;
            float baseDistanceSquared = minBaseDistance * minBaseDistance;
            foreach (PlayerState player in world.Players)
                if (Vector2.DistanceSquared(basePosition, player.BasePosition) < baseDistanceSquared) return false;

            GameRules rules = world.Rules;
            float enemyDistance = rules.SpawnMinEnemyUnitDistance;
            float formationDistance = enemyDistance + rules.SlotSpacing * (rules.StartFieldUnitCount - 1) * 0.5f;
            float enemyDistanceSquared = enemyDistance * enemyDistance;
            float formationDistanceSquared = formationDistance * formationDistance;
            Vector2[] guardSlots = null;
            if (rules.StartGarrisonUnitCount > 0 && !RecallSystem.TryCreateLayout(world, basePosition, out guardSlots)) return false;
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId >= 0 && unit.Health > 0 &&
                    (Vector2.DistanceSquared(unit.Position, basePosition) < enemyDistanceSquared ||
                    Vector2.DistanceSquared(unit.Position, legionPosition) < formationDistanceSquared)) return false;
            if (guardSlots != null)
                foreach (UnitState unit in world.Units)
                {
                    if (unit.OwnerId < 0 || unit.Health <= 0) continue;
                    for (int index = 0; index < rules.StartGarrisonUnitCount; index++)
                        if (Vector2.DistanceSquared(unit.Position, guardSlots[index]) < enemyDistanceSquared) return false;
                }
            return true;
        }

        internal void Tick(WorldState world)
        {
            foreach (NeutralGroup group in groups) RespawnGroup(group);
            GameRules rules = world.Rules;
            int explorationRate = rules.ExplorationBundlesPerMinute;
            long ticksPerMinute = 60L * rules.TickRate;
            long minuteTick = world.Tick % ticksPerMinute;
            if (minuteTick == 0) minuteTick = ticksPerMinute;
            // 월드 틱만으로 출현 시점을 구해 저장·승계 뒤에도 주기를 이어 간다.
            bool explorationDue = explorationRate > 0 &&
                minuteTick * explorationRate / ticksPerMinute > (minuteTick - 1) * explorationRate / ticksPerMinute;
            for (int site = 0; site < sites.Length; site++)
            {
                ref CoreSchedule schedule = ref coreSchedules[site];
                bool active = false;
                foreach (PlayerState player in world.Players)
                    if (player.GhostEndTick == 0 && player.BasePosition == sites[site]) { active = true; break; }
                if (active && world.Rules.MapCoreSpawnPerPlayerPerMinute > 0)
                {
                    bool opening = !schedule.FirstGranted;
                    if (schedule.FirstGranted)
                        schedule.Credit = Math.Min(coreThreshold, schedule.Credit + world.Rules.MapCoreSpawnPerPlayerPerMinute);
                    if (!schedule.FirstGranted || schedule.Credit >= coreThreshold)
                    {
                        bool near = IsNearbyPhase(schedule.Phase);
                        Vector2 position = near ? schedule.NearPosition : schedule.SharedPosition;
                        if (world.Cores.Count < Net.GameplayCodec.MaxObjects && InsideWorld(position) && !InsideBuilding(position))
                        {
                            world.Cores.Add(new CoreState { Id = world.NextId++, Position = position,
                                Amount = world.Rules.OpeningCoreBundleSize, Expires = world.Time + world.Rules.CoreLifetime });
                            world.Metrics.EcologyCoreSpawns++;
                            // 첫 묶음도 같은 예산에서 선지급하여 재입장으로 공급량이 늘지 않게 한다.
                            schedule.Credit -= coreThreshold;
                            schedule.FirstGranted = true;
                            schedule.Phase = (schedule.Phase + 1) %
                                (world.Rules.OpeningNearbyBundlesPerMinute + world.Rules.OpeningSharedBundlesPerMinute);
                        }
                    }
                    // 처음 입장한 틱도 동일한 활성 노출 시간으로 센다.
                    if (opening && schedule.FirstGranted)
                        schedule.Credit += world.Rules.MapCoreSpawnPerPlayerPerMinute;
                }
                schedule.LastTick = world.Tick;
                schedule.NextTick = NextSupplyTick(schedule.Credit, schedule.FirstGranted, schedule.LastTick);
                if (active && explorationDue && world.Cores.Count < Net.GameplayCodec.MaxObjects)
                    SpawnExplorationCore(site);
            }
        }

        private void SpawnExplorationCore(int site)
        {
            GameRules rules = world.Rules;
            Vector2 start = sites[site] + legionOffset;
            float reach = (float)Math.Min(rules.MapSize, rules.LegionSpeed * rules.ExplorationCoreLifetime);
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                Vector2 point = start + RandomDisc(reach);
                if (!InsideWorld(point) || InsideBuilding(point)) continue;
                bool nearSite = false;
                foreach (Vector2 home in sites)
                    if (Vector2.DistanceSquared(home, point) <= neutralBuildClearanceSquared)
                    {
                        nearSite = true;
                        break;
                    }
                if (nearSite || !ClearPath(start, point)) continue;

                world.Cores.Add(new CoreState { Id = world.NextId++, Position = point,
                    Amount = rules.OpeningCoreBundleSize, Expires = world.Time + rules.ExplorationCoreLifetime });
                world.Metrics.EcologyCoreSpawns++;
                return;
            }
        }

        private long NextSupplyTick(long credit, bool firstGranted, long tick) => !firstGranted || world.Rules.MapCoreSpawnPerPlayerPerMinute == 0 ? tick :
            tick + (coreThreshold - credit + world.Rules.MapCoreSpawnPerPlayerPerMinute - 1) / world.Rules.MapCoreSpawnPerPlayerPerMinute;

        private bool IsNearbyPhase(int phase)
        {
            int shared = world.Rules.OpeningSharedBundlesPerMinute;
            int total = world.Rules.OpeningNearbyBundlesPerMinute + shared;
            return (long)phase * shared / total == (long)(phase + 1) * shared / total;
        }

        private NeutralGroup[] PlaceInitialGroups(long neutralBudget, int[] nature, int natureCount, int[] energy, int energyCount)
        {
            GameRules rules = world.Rules;
            double spacing = rules.NeutralWalkSpacingSeconds * rules.LegionSpeed;
            float radius = GroupRadius(rules);
            double rowSpacing = spacing * Math.Sqrt(3) / 2;
            // 한 후보에서 훑는 격자 크기를 제한하고, 실제 배치의 유닛 예산은 따로 확인한다.
            if (spacing < 2 * radius + rules.MinimumSeparation || spacing > rules.MapSize * Math.Sqrt(2) ||
                (rules.MapSize / spacing + 1) * (rules.MapSize / rowSpacing + 1) > Net.Protocol.MaxUnits)
                throw new ArgumentException("중립 도보 간격으로 월드 안에 안전하게 격자를 배치할 수 없습니다.");

            float center = rules.MapSize * 0.5f;
            float energyHalfWidth = center * rules.NeutralEnergyZoneFraction - radius;
            bool spawnEnergy = rules.NeutralEnergyGroupRatio > 0;
            if (spawnEnergy && energyHalfWidth <= 0) throw new ArgumentException("중앙 에너지 구역에 중립 무리가 들어갈 공간이 없습니다.");
            float originHalfWidth = spawnEnergy ? energyHalfWidth : center;

            List<Vector2> best = null;
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                var origin = attempt == 0 ? new Vector2(center) :
                    new Vector2(RandomRange(center - originHalfWidth, center + originHalfWidth),
                        RandomRange(center - originHalfWidth, center + originHalfWidth));
                var cells = new List<(int Column, int Row)>();
                var anchors = new Dictionary<(int Column, int Row), Vector2>();
                int firstRow = (int)Math.Ceiling((radius - origin.Y) / rowSpacing);
                int lastRow = (int)Math.Floor((rules.MapSize - radius - origin.Y) / rowSpacing);
                for (int row = firstRow; row <= lastRow; row++)
                {
                    double rowX = origin.X + row * spacing * 0.5;
                    int firstColumn = (int)Math.Ceiling((radius - rowX) / spacing);
                    int lastColumn = (int)Math.Floor((rules.MapSize - radius - rowX) / spacing);
                    for (int column = firstColumn; column <= lastColumn; column++)
                    {
                        var anchor = new Vector2((float)(rowX + column * spacing), (float)(origin.Y + row * rowSpacing));
                        if (!CanPlaceAnchor(anchor, radius)) continue;
                        cells.Add((column, row));
                        anchors.Add((column, row), anchor);
                    }
                }

                foreach (var cell in cells)
                {
                    if (!anchors.TryGetValue(cell, out Vector2 anchor)) continue;
                    var component = new List<Vector2> { anchor };
                    var pending = new Queue<((int Column, int Row) Cell, Vector2 Anchor)>();
                    pending.Enqueue((cell, anchor));
                    anchors.Remove(cell);
                    while (pending.Count > 0)
                    {
                        var current = pending.Dequeue();
                        foreach (var offset in LatticeNeighbors)
                        {
                            var neighbor = (current.Cell.Column + offset.Column, current.Cell.Row + offset.Row);
                            if (!anchors.TryGetValue(neighbor, out Vector2 next) || !ClearPath(current.Anchor, next)) continue;
                            anchors.Remove(neighbor);
                            component.Add(next);
                            pending.Enqueue((neighbor, next));
                        }
                    }
                    if (component.Count < 2 || best != null && component.Count <= best.Count ||
                        spawnEnergy && !component.Exists(point => InsideEnergyZone(point, radius)) ||
                        !Array.TrueForAll(sites, site => component.Exists(point => ClearPath(site + legionOffset, point)))) continue;
                    best = component;
                }
            }
            if (best == null) throw new ArgumentException("입장 지점에서 접근할 수 있는 연결된 중립 배치를 찾지 못했습니다.");
            if ((long)best.Count * rules.NeutralMaxGroupSize > neutralBudget)
                throw new ArgumentException("중립 배치와 플레이어 최대 인구가 월드 유닛 상한을 넘습니다.");
            int energyGroups = spawnEnergy ? Math.Max(1, (int)Math.Floor(best.Count * rules.NeutralEnergyGroupRatio)) : 0;
            var result = new NeutralGroup[best.Count];
            for (int index = 0; index < result.Length; index++)
            {
                bool isEnergy = energyGroups > 0 && InsideEnergyZone(best[index], radius);
                if (isEnergy) energyGroups--;
                result[index] = CreateGroup(-1, isEnergy, isEnergy ? energy : nature, isEnergy ? energyCount : natureCount);
                result[index].Anchor = best[index];
            }
            return result;
        }

        private NeutralGroup CreateGroup(int site, bool energy, int[] elements, int elementCount)
        {
            GameRules rules = world.Rules;
            int element = elements[world.Random.Next(elementCount)];
            int count = site < 0 && world.Random.NextDouble() < rules.NeutralSoloChance
                ? rules.NeutralSoloSize
                : world.Random.Next(rules.NeutralMinGroupSize, rules.NeutralMaxGroupSize + 1);
            return new NeutralGroup(element, site, energy, count, rules.SlotSpacing);
        }

        // 데모의 시작 교전을 재현하는 데 쓰는 기존 밀도 배치다.
        private NeutralGroup[] PlaceLegacyGroups(long neutralBudget, int[] nature, int natureCount, int[] energy, int energyCount)
        {
            GameRules rules = world.Rules;
            double total = Math.Ceiling((double)rules.MapSize * rules.MapSize * rules.NeutralWorldGroupsPerSquareKm / 1_000_000);
            if (starterClearance >= rules.OpeningFirstNatureMaxDistance || total < sites.Length * 2 || total * rules.NeutralMaxGroupSize > neutralBudget)
                throw new ArgumentException("데모의 시작 중립과 전체 무리 수를 월드 안에 배치할 수 없습니다.");
            var result = new NeutralGroup[(int)total];
            int energyStart = result.Length - (int)Math.Floor((result.Length - sites.Length) * rules.NeutralEnergyGroupRatio);
            for (int index = 0; index < result.Length; index++)
            {
                int site = index < sites.Length ? index : -1;
                bool isEnergy = index >= energyStart;
                NeutralGroup group = CreateGroup(site, isEnergy, isEnergy ? energy : nature, isEnergy ? energyCount : natureCount);
                int paired = index >= sites.Length && index < sites.Length * 2 ? index - sites.Length : -1;
                int first = isEnergy ? energyStart : sites.Length * 2;
                int regions = (isEnergy ? result.Length : energyStart) - first;
                PlaceLegacyGroup(result, index, group, paired, index - first, regions);
                result[index] = group;
            }
            return result;
        }

        private void PlaceLegacyGroup(NeutralGroup[] previous, int index, NeutralGroup group, int paired, int region, int regions)
        {
            GameRules rules = world.Rules;
            float separation = rules.MinimumSeparation * rules.MinimumSeparation;
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                Vector2 anchor = paired < 0 ? SampleLegacyAnchor(group, region, attempt < PlacementAttempts / 2 ? regions : 0) :
                    previous[paired].Anchor + RandomDirection() * RandomRange(rules.OpeningNextGroupMinDistance, rules.OpeningNextGroupMaxDistance);
                if (paired >= 0 && !ClearPath(previous[paired].Anchor, anchor)) continue;
                bool fits = true;
                foreach (NeutralSlot slot in group.Slots)
                {
                    Vector2 position = anchor + slot.Offset;
                    if (!CanPlaceNeutral(group, position)) { fits = false; break; }
                    for (int other = 0; other < index && fits; other++)
                        foreach (NeutralSlot previousSlot in previous[other].Slots)
                            if (Vector2.DistanceSquared(position, previous[other].Anchor + previousSlot.Offset) < separation)
                            { fits = false; break; }
                    if (!fits) break;
                }
                if (!fits) continue;
                group.Anchor = anchor;
                return;
            }
            throw new ArgumentException("데모 중립 무리의 유효한 초기 위치를 찾지 못했습니다.");
        }

        private Vector2 SampleLegacyAnchor(NeutralGroup group, int region, int regions)
        {
            if (group.StarterSite >= 0) return SampleLegacyStarter(group.StarterSite);
            GameRules rules = world.Rules;
            float width = group.Energy ? rules.MapSize * rules.NeutralEnergyZoneFraction : rules.MapSize;
            float inset = (rules.MapSize - width) * 0.5f;
            if (regions == 0) return new Vector2(inset + width * (float)world.Random.NextDouble(), inset + width * (float)world.Random.NextDouble());
            int columns = (int)Math.Ceiling(Math.Sqrt(regions));
            int rows = (regions + columns - 1) / columns;
            int row = region / columns;
            int rowCount = Math.Min(columns, regions - row * columns);
            var jitter = new Vector2((float)world.Random.NextDouble(), (float)world.Random.NextDouble());
            return new Vector2(inset + (region % columns + jitter.X) * width / rowCount,
                inset + (row + jitter.Y) * width / rows);
        }

        private static float GroupRadius(GameRules rules)
        {
            int columns = (int)Math.Ceiling(Math.Sqrt(rules.NeutralMaxGroupSize));
            return (columns - 1) * rules.SlotSpacing * (float)Math.Sqrt(0.5);
        }

        private bool CanPlaceAnchor(Vector2 anchor, float radius)
        {
            GameRules rules = world.Rules;
            float buildDistance = Math.Max(rules.VisionRadius, (float)Math.Sqrt(neutralBuildClearanceSquared)) + radius;
            float spawnDistance = Math.Max(starterClearance, rules.VisionRadius) + radius;
            foreach (Vector2 site in sites)
                if (Vector2.DistanceSquared(site, anchor) <= buildDistance * buildDistance ||
                    Vector2.DistanceSquared(site + legionOffset, anchor) <= spawnDistance * spawnDistance) return false;
            foreach (BuildingState building in world.Buildings)
            {
                float distance = (building.Kind == BuildingKind.Base ? buildDistance - radius : building.Radius) + radius;
                if (building.BlocksMovement && Vector2.DistanceSquared(building.Position, anchor) <= distance * distance) return false;
            }
            float separation = rules.MinimumSeparation + radius;
            foreach (UnitState unit in world.Units)
                if (unit.Health > 0 && Vector2.DistanceSquared(unit.Position, anchor) < separation * separation) return false;
            return true;
        }

        private bool InsideEnergyZone(Vector2 position, float radius)
        {
            GameRules rules = world.Rules;
            float inset = (rules.MapSize - rules.MapSize * rules.NeutralEnergyZoneFraction) * 0.5f + radius;
            return position.X >= inset && position.Y >= inset && position.X <= rules.MapSize - inset && position.Y <= rules.MapSize - inset;
        }

        private void RespawnGroup(NeutralGroup group)
        {
            bool hasSurvivor = false;
            for (int index = 0; index < group.Slots.Length; index++)
            {
                ref NeutralSlot slot = ref group.Slots[index];
                if (slot.Unit == null) continue;
                if (slot.Unit.Health > 0 && slot.Unit.OwnerId < 0)
                    hasSurvivor = true;
            }
            for (int index = 0; index < group.Slots.Length; index++)
            {
                ref NeutralSlot slot = ref group.Slots[index];
                if (slot.Unit != null || !slot.Reserved || world.Tick < slot.ReadyTick) continue;
                Vector2 anchor = group.Anchor;
                if (!hasSurvivor && group.StarterSite >= 0)
                    anchor = SampleLegacyStarter(group.StarterSite);
                Vector2 position = anchor + slot.Offset;
                if (!CanPlaceNeutral(group, position))
                {
                    position += RandomDisc(world.Rules.SlotSpacing * group.Columns);
                    if (!CanPlaceNeutral(group, position))
                    {
                        // 막힌 자리는 다음 초에 다시 시도해 한 틱에 탐색이 몰리는 것을 막는다.
                        slot.ReadyTick = world.Tick + world.Rules.TickRate;
                        continue;
                    }
                }
                slot.Unit = SpawnNeutral(group.Element, position, consumeReservation: true);
                slot.Reserved = false;
                group.Anchor = anchor;
                hasSurvivor = true;
            }
        }

        /// <summary>전향된 원소는 생존 슬롯을 유지하므로 빈자리의 예약은 별도로 확보한다.</summary>
        internal void ReserveVacancies()
        {
            foreach (NeutralGroup group in groups)
                for (int index = 0; index < group.Slots.Length; index++)
                {
                    ref NeutralSlot slot = ref group.Slots[index];
                    if (slot.Unit != null && (slot.Unit.Health <= 0 || slot.Unit.OwnerId >= 0))
                    {
                        slot.Unit = null;
                        slot.ReadyTick = world.Tick + respawnTicks;
                    }
                    if (slot.Unit == null && !slot.Reserved) slot.Reserved = world.TryReserveUnitSlots(1);
                }
        }

        private UnitState SpawnNeutral(int element, Vector2 position, bool consumeReservation = false)
        {
            UnitState unit = world.AddUnit(-1, element, position, consumeReservation: consumeReservation);
            unit.Home = position;
            unit.EcologyNeutral = true;
            if (consumeReservation) world.Metrics.EcologyRespawns++;
            return unit;
        }

        private void PrepareSupplyRoutes()
        {
            GameRules rules = world.Rules;
            for (int index = 0; index < sites.Length; index++)
            {
                ref CoreSchedule schedule = ref coreSchedules[index];
                bool found = false;
                for (int attempt = 0; attempt < PlacementAttempts; attempt++)
                {
                    Vector2 near = sites[index] + legionOffset + RandomDirection() *
                        RandomRange(rules.OpeningFirstCoreMinDistance, Math.Max(rules.OpeningFirstCoreMinDistance, Math.Min(rules.OpeningFirstCoreMaxDistance, rules.VisionRadius)));
                    if (!ValidPosition(near) || InsideBuilding(near) || !ClearPath(sites[index] + legionOffset, near)) continue;
                    schedule.NearPosition = near;
                    found = true;
                    break;
                }
                if (!found) throw new ArgumentException("시작 핵의 유효한 경로를 찾지 못했습니다.");
                float nearest = float.MaxValue;
                schedule.SharedPosition = schedule.NearPosition;
                for (int other = 0; other < sites.Length; other++)
                {
                    if (other == index) continue;
                    Vector2 shared = (sites[index] + sites[other]) * 0.5f;
                    float distance = Vector2.DistanceSquared(sites[index], sites[other]);
                    if (distance >= nearest || InsideBuilding(shared) ||
                        !ClearPath(sites[index] + legionOffset, shared) || !ClearPath(sites[other] + legionOffset, shared)) continue;
                    bool blocked = false;
                    foreach (Vector2 site in sites)
                        if (Vector2.DistanceSquared(site, shared) <= neutralBuildClearanceSquared) { blocked = true; break; }
                    if (blocked) continue;
                    nearest = distance;
                    schedule.SharedPosition = shared;
                }
                if (sites.Length > 1 && nearest == float.MaxValue)
                    throw new ArgumentException("양쪽에서 접근할 수 있는 공용 핵 경로를 찾지 못했습니다.");
                schedule.LastTick = world.Tick;
                schedule.NextTick = world.Tick;
            }
        }

        private bool ClearPath(Vector2 from, Vector2 to)
        {
            foreach (BuildingState building in world.Buildings)
                if (building.BlocksMovement && CircleCollision.Limit(from, to - from, building.Position, building.Radius) < 1) return false;
            // 입장 전에도 양쪽 본진의 실제 충돌 반경을 피한다.
            foreach (Vector2 site in sites)
                if (CircleCollision.Limit(from, to - from, site, world.Rules.BaseRadius) < 1) return false;
            return true;
        }

        private bool ValidPosition(Vector2 position) => WorldCheckpoint.Finite(position.X) && WorldCheckpoint.Finite(position.Y) && InsideWorld(position);
        private float RandomRange(float minimum, float maximum) => minimum + (maximum - minimum) * (float)world.Random.NextDouble();

        private Vector2 SampleLegacyStarter(int site)
        {
            float minimum = Math.Max(starterClearance, world.Rules.OpeningFirstNatureMinDistance);
            float distance = RandomRange(minimum, world.Rules.OpeningFirstNatureMaxDistance);
            return sites[site] + legionOffset + RandomDirection() * distance;
        }

        private bool CanPlaceNeutral(NeutralGroup group, Vector2 position)
        {
            GameRules rules = world.Rules;
            if (!InsideWorld(position) || InsideBuilding(position)) return false;
            if (group.Energy && !InsideEnergyZone(position, 0)) return false;
            if (group.StarterSite >= 0)
            {
                if (!ClearPath(sites[group.StarterSite] + legionOffset, position)) return false;
                float distance = Vector2.DistanceSquared(position, sites[group.StarterSite] + legionOffset);
                if (distance < starterClearance * starterClearance || distance > rules.OpeningFirstNatureMaxDistance * rules.OpeningFirstNatureMaxDistance ||
                    distance < rules.OpeningFirstNatureMinDistance * rules.OpeningFirstNatureMinDistance)
                    return false;
            }
            foreach (PlayerState player in world.Players)
                if (Vector2.DistanceSquared(player.BasePosition, position) <= neutralBuildClearanceSquared) return false;
            foreach (Vector2 site in sites)
            {
                // 아직 비어 있는 입장 위치에도 건설 공간을 남겨 둔다.
                if (Vector2.DistanceSquared(site, position) <= neutralBuildClearanceSquared ||
                    Vector2.DistanceSquared(site + legionOffset, position) <= starterClearance * starterClearance) return false;
                if (group.StarterSite < 0)
                {
                    // 일반 무리가 시작 무리와 별개로 새 군단을 즉시 공격하지 않도록 입장 시야를 비워 둔다.
                    if (Vector2.DistanceSquared(site + legionOffset, position) <= rules.VisionRadius * rules.VisionRadius ||
                        Vector2.DistanceSquared(site, position) <= rules.VisionRadius * rules.VisionRadius) return false;
                }
            }
            float separationSquared = rules.MinimumSeparation * rules.MinimumSeparation;
            foreach (UnitState unit in world.Units)
                if (unit.Health > 0 && Vector2.DistanceSquared(position, unit.Position) < separationSquared) return false;
            return true;
        }

        private bool InsideWorld(Vector2 position) =>
            position.X >= 0 && position.Y >= 0 && position.X <= world.Rules.MapSize && position.Y <= world.Rules.MapSize;

        private bool InsideBuilding(Vector2 position)
        {
            foreach (BuildingState building in world.Buildings)
                if (building.BlocksMovement &&
                    Vector2.DistanceSquared(position, building.Position) < building.Radius * building.Radius)
                    return true;
            return false;
        }

        private Vector2 RandomDirection()
        {
            double angle = world.Random.NextDouble() * (2 * Math.PI);
            return new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        }

        private Vector2 RandomDisc(float radius) => RandomDirection() * (radius * (float)Math.Sqrt(world.Random.NextDouble()));

        private sealed class NeutralGroup
        {
            internal readonly int Element, StarterSite, Columns;
            internal readonly bool Energy;
            internal readonly NeutralSlot[] Slots;
            internal Vector2 Anchor;

            internal NeutralGroup(int element, int starterSite, bool energy, int count, float spacing)
            {
                Element = element;
                StarterSite = starterSite;
                Energy = energy;
                Columns = (int)Math.Ceiling(Math.Sqrt(count));
                int rows = (count + Columns - 1) / Columns;
                Slots = new NeutralSlot[count];
                for (int index = 0; index < count; index++)
                    Slots[index].Offset = new Vector2(index % Columns - (Columns - 1) * 0.5f, index / Columns - (rows - 1) * 0.5f) * spacing;
            }
        }

        private struct NeutralSlot
        {
            internal Vector2 Offset;
            internal UnitState Unit;
            internal long ReadyTick;
            internal bool Reserved;
        }

        private struct CoreSchedule
        {
            internal Vector2 NearPosition, SharedPosition;
            internal long Credit, LastTick, NextTick;
            internal int Phase;
            internal bool FirstGranted;
        }
    }
}
