using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>시드로 정한 입장 위치와 중립 자리의 수명, 접속자별 맵 핵을 관리한다.</summary>
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
            legionOffset = new Vector2(0, rules.BaseRadius + rules.SlotSpacing * (rules.StartUnitCount + 1));
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
                rules.SlotSpacing * Math.Max(1, (rules.StartUnitCount - 1) * 0.5f);
            if (starterClearance >= rules.VisionRadius)
                throw new ArgumentException("시작 중립을 시야 안의 안전한 위치에 배치할 수 없습니다.");
            respawnTicks = checked((long)Math.Ceiling(rules.NeutralRespawnSeconds * rules.TickRate));
            coreThreshold = 60L * rules.TickRate;
            coreSchedules = new CoreSchedule[rules.MaxPlayers];

            double total = Math.Ceiling((double)rules.MapSize * rules.MapSize * rules.NeutralWorldGroupsPerSquareKm / 1_000_000);
            int initialUnits = saved == null ? world.Units.Count : 0;
            if (total < sites.Length || total * rules.NeutralMaxGroupSize > Net.Protocol.MaxUnits - initialUnits - (long)rules.MaxPlayers * rules.MaxPopulation)
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
                int first = isEnergy ? groups.Length - energyGroups : sites.Length;
                PlaceInitialGroup(group, index - first, (isEnergy ? groups.Length : groups.Length - energyGroups) - first);
            }
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
                    slots[slot] = new WorldCheckpoint.SlotData { Unit = group.Slots[slot].Unit?.Id ?? 0, ReadyTick = group.Slots[slot].ReadyTick };
                snapshot.Groups[index] = new WorldCheckpoint.GroupData
                {
                    Element = group.Element, StarterSite = group.StarterSite, Energy = group.Energy, Anchor = group.Anchor, Slots = slots
                };
            }
            for (int index = 0; index < coreSchedules.Length; index++)
                snapshot.Schedules[index] = new WorldCheckpoint.ScheduleData
                {
                    Owner = coreSchedules[index].Owner, Credit = coreSchedules[index].Credit, GlobalNext = coreSchedules[index].GlobalNext
                };
            return snapshot;
        }

        private void Restore(WorldCheckpoint.EcologyData saved, int energyGroups)
        {
            GameRules rules = world.Rules;
            WorldCheckpoint.Check(saved.Sites != null && saved.Sites.Length == sites.Length &&
                saved.Groups != null && saved.Groups.Length == groups.Length &&
                saved.Schedules != null && saved.Schedules.Length == coreSchedules.Length, "생태 배열 크기");
            var remainingSites = new HashSet<Vector2>(sites);
            foreach (Vector2 site in saved.Sites)
                WorldCheckpoint.Check(remainingSites.Remove(site), "입장 위치");
            saved.Sites.CopyTo(sites, 0);

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
                        WorldCheckpoint.Check(world.UnitById.TryGetValue(data.Unit, out UnitState unit) && unit.OwnerId == -1 &&
                            unit.EcologyNeutral && unit.Element == item.Element && liveNeutrals.Add(data.Unit), "중립 자리 참조");
                        group.Slots[slot].Unit = unit;
                    }
                    group.Slots[slot].ReadyTick = data.ReadyTick;
                }
            }
            foreach (UnitState unit in world.Units)
                WorldCheckpoint.Check(!unit.EcologyNeutral || unit.OwnerId >= 0 || liveNeutrals.Contains(unit.Id), "중립 자리 누락");
            for (int index = 0; index < coreSchedules.Length; index++)
            {
                WorldCheckpoint.ScheduleData item = saved.Schedules[index];
                WorldCheckpoint.Check(item.Owner >= 0 && item.Credit >= 0 && item.Credit < coreThreshold, "맵 핵 생성 크레딧");
                coreSchedules[index] = new CoreSchedule { Owner = item.Owner, Credit = item.Credit, GlobalNext = item.GlobalNext };
            }
        }

        internal bool TryChooseSpawn(out Vector2 basePosition, out Vector2 legionPosition)
        {
            float safeDistanceSquared = starterClearance * starterClearance;
            foreach (Vector2 site in sites)
            {
                Vector2 legion = site + legionOffset;
                if (!IsSafePlayerSpawn(world, site, legion, minBaseDistance)) continue;
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
            float baseDistanceSquared = minBaseDistance * minBaseDistance;
            foreach (PlayerState player in world.Players)
                if (Vector2.DistanceSquared(basePosition, player.BasePosition) < baseDistanceSquared) return false;

            GameRules rules = world.Rules;
            float enemyDistance = rules.SpawnMinEnemyUnitDistance;
            float formationDistance = enemyDistance + rules.SlotSpacing * (rules.StartUnitCount - 1) * 0.5f;
            float enemyDistanceSquared = enemyDistance * enemyDistance;
            float formationDistanceSquared = formationDistance * formationDistance;
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId >= 0 && unit.Health > 0 &&
                    (Vector2.DistanceSquared(unit.Position, basePosition) < enemyDistanceSquared ||
                    Vector2.DistanceSquared(unit.Position, legionPosition) < formationDistanceSquared)) return false;
            return true;
        }

        internal void Tick(WorldState world)
        {
            foreach (NeutralGroup group in groups) RespawnGroup(group);
            foreach (PlayerState player in world.Players)
            {
                if (player.GhostEndTick != 0) continue;
                ref CoreSchedule schedule = ref coreSchedules[player.Slot];
                if (schedule.Owner != player.Id) schedule = new CoreSchedule { Owner = player.Id };
                schedule.Credit += world.Rules.MapCoreSpawnPerPlayerPerMinute;
                if (schedule.Credit < coreThreshold) continue;
                schedule.Credit -= coreThreshold;
                bool local = !player.NatureForge || !schedule.GlobalNext;
                if (TrySpawnCore(player.BasePosition, local)) schedule.GlobalNext = player.NatureForge && local;
            }
        }

        private void PlaceInitialGroup(NeutralGroup group, int region, int regions)
        {
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                Vector2 anchor = SampleAnchor(group, region, attempt < PlacementAttempts / 2 ? regions : 0);
                bool fits = true;
                foreach (NeutralSlot slot in group.Slots)
                    if (!CanPlaceNeutral(group, anchor + slot.Offset))
                    {
                        fits = false;
                        break;
                    }
                if (!fits) continue;
                group.Anchor = anchor;
                for (int index = 0; index < group.Slots.Length; index++)
                    group.Slots[index].Unit = SpawnNeutral(group.Element, anchor + group.Slots[index].Offset);
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
                else
                {
                    slot.Unit = null;
                    slot.ReadyTick = world.Tick + respawnTicks;
                }
            }
            for (int index = 0; index < group.Slots.Length; index++)
            {
                ref NeutralSlot slot = ref group.Slots[index];
                if (slot.Unit != null || world.Tick < slot.ReadyTick) continue;
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
                slot.Unit = SpawnNeutral(group.Element, position);
                group.Anchor = anchor;
                hasSurvivor = true;
            }
        }

        private UnitState SpawnNeutral(int element, Vector2 position)
        {
            UnitState unit = world.AddUnit(-1, element, position);
            unit.Home = position;
            unit.EcologyNeutral = true;
            return unit;
        }

        private bool TrySpawnCore(Vector2 basePosition, bool local)
        {
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                Vector2 position = local
                    ? basePosition + RandomDisc(world.Rules.VisionRadius)
                    : RandomSquare(0, world.Rules.MapSize);
                if (!InsideWorld(position) || InsideBuilding(position)) continue;
                world.Cores.Add(new CoreState
                {
                    Id = world.NextId++,
                    Position = position,
                    Amount = 1,
                    Expires = world.Time + world.Rules.CoreLifetime
                });
                return true;
            }
            return false;
        }

        private Vector2 SampleAnchor(NeutralGroup group, int region = 0, int regions = 0)
        {
            if (group.StarterSite >= 0)
            {
                float minimum = Math.Max(starterClearance, world.Rules.BuildRadius - legionOffset.Y);
                float distance = (minimum + world.Rules.VisionRadius) * 0.5f;
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
                float distance = Vector2.DistanceSquared(position, sites[group.StarterSite] + legionOffset);
                if (distance < starterClearance * starterClearance || distance > rules.VisionRadius * rules.VisionRadius)
                    return false;
            }
            foreach (PlayerState player in world.Players)
                if (Vector2.DistanceSquared(player.BasePosition, position) <= neutralBuildClearanceSquared) return false;
            foreach (Vector2 site in sites)
            {
                // 아직 비어 있는 입장 위치에도 건설 공간을 남겨 둔다.
                if (Vector2.DistanceSquared(site, position) <= neutralBuildClearanceSquared) return false;
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
        }

        private struct CoreSchedule
        {
            internal int Owner;
            internal long Credit;
            internal bool GlobalNext;
        }
    }
}
