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
        private bool preparing;

        internal WorldEcology(WorldState world, float minBaseDistance, WorldCheckpoint.EcologyData saved = null)
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
            if (starterClearance >= rules.OpeningFirstNatureMaxDistance)
                throw new ArgumentException("시작 중립을 초반 경로의 안전한 위치에 배치할 수 없습니다.");
            respawnTicks = checked((long)Math.Ceiling(rules.NeutralRespawnSeconds * rules.TickRate));
            coreThreshold = 60L * rules.TickRate * rules.OpeningCoreBundleSize;
            coreSchedules = new CoreSchedule[sites.Length];

            double total = Math.Ceiling((double)rules.MapSize * rules.MapSize * rules.NeutralWorldGroupsPerSquareKm / 1_000_000);
            int initialUnits = saved == null ? world.Units.Count + world.ReservedUnitSlots : 0;
            if (total < sites.Length * 2 || total * rules.NeutralMaxGroupSize > Net.Protocol.MaxUnits - initialUnits - (long)rules.MaxPlayers * rules.MaxPopulation)
                throw new ArgumentException("중립 무리 수가 입장 위치 또는 월드 유닛 상한에 맞지 않습니다.");
            groups = new NeutralGroup[(int)total];
            int energyGroups = (int)Math.Floor((groups.Length - sites.Length) * rules.NeutralEnergyGroupRatio);
            if (saved != null)
            {
                Restore(saved, energyGroups);
                return;
            }
            for (int index = sites.Length - 1; index > 0; index--)
            {
                int other = world.Random.Next(index + 1);
                (sites[index], sites[other]) = (sites[other], sites[index]);
            }
            foreach (PlayerState player in world.Players)
                if (!ContainsSite(player.BasePosition)) throw new ArgumentException("기존 본진이 생태 입장 자리에 없습니다.");
            PrepareSupplyRoutes();
            preparing = true;
            for (int index = 0; index < groups.Length; index++)
            {
                int site = index < sites.Length ? index : -1;
                bool isEnergy = index >= groups.Length - energyGroups;
                int element = isEnergy ? energy[world.Random.Next(energyCount)] : nature[world.Random.Next(natureCount)];
                int count = site < 0 && world.Random.NextDouble() < rules.NeutralSoloChance
                    ? rules.NeutralSoloSize
                    : world.Random.Next(rules.NeutralMinGroupSize, rules.NeutralMaxGroupSize + 1);
                var group = new NeutralGroup(element, site, isEnergy, count, rules.SlotSpacing);
                groups[index] = group;
                int first = isEnergy ? groups.Length - energyGroups : sites.Length * 2;
                PlaceInitialGroup(group, index - first, (isEnergy ? groups.Length : groups.Length - energyGroups) - first,
                    index >= sites.Length && index < sites.Length * 2 ? index - sites.Length : -1);
            }
            preparing = false;
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

        private void Restore(WorldCheckpoint.EcologyData saved, int energyGroups)
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
            for (int index = 0; index < groups.Length; index++)
            {
                WorldCheckpoint.GroupData item = saved.Groups[index];
                bool starter = index < sites.Length;
                int count = item.Slots?.Length ?? 0;
                WorldCheckpoint.Check(item.StarterSite == (starter ? index : -1) &&
                    item.Energy == (index >= groups.Length - energyGroups) &&
                    item.Element >= 0 && item.Element < rules.Elements.Count &&
                    rules.Elements[item.Element].Group == (item.Energy ? ElementGroup.Energy : ElementGroup.Nature) &&
                    WorldCheckpoint.Finite(item.Anchor.X) && WorldCheckpoint.Finite(item.Anchor.Y) && count > 0 &&
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
                        if (InsideWorld(position) && !InsideBuilding(position))
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

        private void PlaceInitialGroup(NeutralGroup group, int region, int regions, int nextSite)
        {
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                Vector2 anchor = nextSite < 0 ? SampleAnchor(group, region, attempt < PlacementAttempts / 2 ? regions : 0) :
                    groups[nextSite].Anchor + RandomDirection() * RandomRange(world.Rules.OpeningNextGroupMinDistance, world.Rules.OpeningNextGroupMaxDistance);
                if (nextSite >= 0 && !ClearPath(groups[nextSite].Anchor, anchor)) continue;
                bool fits = true;
                foreach (NeutralSlot slot in group.Slots)
                    if (!CanPlaceNeutral(group, anchor + slot.Offset))
                    {
                        fits = false;
                        break;
                    }
                if (!fits) continue;
                group.Anchor = anchor;
                return;
            }
            throw new ArgumentException("중립 무리의 유효한 초기 위치를 찾지 못했습니다.");
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
                if (!hasSurvivor)
                    anchor = group.StarterSite >= 0 ? SampleAnchor(group) : anchor + RandomDisc(world.Rules.NeutralAggroRadius);
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

        private Vector2 SampleAnchor(NeutralGroup group, int region = 0, int regions = 0)
        {
            if (group.StarterSite >= 0)
            {
                float minimum = Math.Max(starterClearance, world.Rules.OpeningFirstNatureMinDistance);
                float distance = RandomRange(minimum, world.Rules.OpeningFirstNatureMaxDistance);
                return sites[group.StarterSite] + legionOffset + RandomDirection() * distance;
            }
            float width = group.Energy ? world.Rules.MapSize * world.Rules.NeutralEnergyZoneFraction : world.Rules.MapSize;
            float inset = (world.Rules.MapSize - width) * 0.5f;
            if (regions == 0) return RandomSquare(inset, width);
            int columns = (int)Math.Ceiling(Math.Sqrt(regions));
            int rows = (regions + columns - 1) / columns;
            int row = region / columns;
            int rowCount = Math.Min(columns, regions - row * columns);
            Vector2 jitter = RandomSquare(0, 1);
            return new Vector2(inset + (region % columns + jitter.X) * width / rowCount,
                inset + (row + jitter.Y) * width / rows);
        }

        private bool CanPlaceNeutral(NeutralGroup group, Vector2 position)
        {
            GameRules rules = world.Rules;
            if (!InsideWorld(position) || InsideBuilding(position)) return false;
            if (group.Energy)
            {
                float inset = (rules.MapSize - rules.MapSize * rules.NeutralEnergyZoneFraction) * 0.5f;
                if (position.X < inset || position.Y < inset || position.X > rules.MapSize - inset || position.Y > rules.MapSize - inset)
                    return false;
            }
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
            // 초기 배치를 준비하는 동안에는 아직 월드에 추가하지 않은 무리도 검사한다.
            if (preparing) foreach (NeutralGroup other in groups)
            {
                if (other == null || other == group) break;
                foreach (NeutralSlot slot in other.Slots)
                    if (Vector2.DistanceSquared(position, other.Anchor + slot.Offset) < separationSquared) return false;
            }
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

        private Vector2 RandomSquare(float inset, float width) =>
            new Vector2(inset + width * (float)world.Random.NextDouble(), inset + width * (float)world.Random.NextDouble());

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
