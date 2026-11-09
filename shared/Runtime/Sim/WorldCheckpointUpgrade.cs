using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>명시적으로 지원하는 v1 저장만 원본 규칙으로 검증한 뒤 현재 체크포인트로 변환한다.</summary>
    public static class WorldCheckpointUpgrade
    {
        public const string SupportedSourceHash = "46aadae359aed09a8022a3249cd90d2af29d7fa89ff2a65877c171e8a909be65";
        public const int MaxJsonBytes = 32 * 1024 * 1024;

        /// <summary>신규 필수 필드가 없는 v1 규칙을 일반 GameRules와 분리해 읽는다.</summary>
        public sealed class LegacyRules
        {
            internal readonly JObject Root;
            public string DataHash { get; }
            public int MaxPlayers => (int)Root.SelectToken("server.maxPlayers");
            internal int TickRate => (int)Root.SelectToken("world.tickRate");
            internal JArray Elements => (JArray)Root["elements"];
            internal float MapSize(int roomPlayers) => roomPlayers == 0 ? (float)Root.SelectToken("world.mapSize") :
                Math.Min((float)Root.SelectToken("world.mapSize"), (float)Math.Round((float)Root.SelectToken("world.roomMapSizeAtTwoPlayers") * Math.Sqrt(roomPlayers / 2.0)));
            internal int PopulationCap(int level) => (int)Root["baseLevels"][level - 1]["populationCap"];
            internal int TrainingCost(int element, int main) => (int)((int)Root.SelectToken(IsNature(element) ? "economy.natureUnitCost" : "economy.energyUnitCost") *
                (IsNature(element) && element == main ? 1 - (double)Root.SelectToken("economy.mainElementDiscount") : 1));
            internal bool IsNature(int element) => (string)Elements[element]["group"] == "nature";
            internal bool Element(int index) => index >= 0 && index < Elements.Count;
            internal double Number(string path) => (double)Root.SelectToken(path);

            public LegacyRules(string sourceJson)
            {
                ParseJson(sourceJson);
                var document = new GameDataDocument(sourceJson);
                Check(document.Sha256 == SupportedSourceHash, "지원하지 않는 원본 게임 데이터 해시");
                DataHash = document.Sha256;
                Root = document.Root;
            }
        }

        /// <summary>입력 크기와 중복 키를 확인한다. 일반 저장 로더의 호환 범위를 확장하지 않는다.</summary>
        public static JObject ParseJson(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) == 0 || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
                throw new ArgumentException("이관 JSON 크기가 잘못됐습니다.");
            using (var input = new StringReader(json))
            using (var reader = new JsonTextReader(input) { MaxDepth = 24, DateParseHandling = DateParseHandling.None })
            {
                var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonReaderException("JSON 뒤에 추가 데이터가 있습니다.");
                foreach (JObject item in result.DescendantsAndSelf().OfType<JObject>())
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JProperty property in item.Properties())
                        if (!names.Add(property.Name)) throw new JsonReaderException("대소문자만 다른 중복 필드입니다.");
                }
                return result;
            }
        }

        private static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error, TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore, DateParseHandling = DateParseHandling.None,
            Culture = CultureInfo.InvariantCulture
        });

        /// <summary>추가 차감 없이 첫 유료 주문을 전체 시간으로 시작한다. 원본 객체는 수정하지 않는다.</summary>
        public static WorldCheckpoint Upgrade(string sourceJson, LegacyRules sourceRules, GameRules targetRules)
        {
            if (sourceRules == null || targetRules == null) throw new ArgumentNullException();
            JObject json = ParseJson(sourceJson);
            ValidateVectors(json);
            LegacyCheckpoint source = json.ToObject<LegacyCheckpoint>(Serializer());
            ValidateSource(source, sourceRules);
            ValidateFieldNames(json, JObject.FromObject(source, Serializer()));
            GameRules target = targetRules.ForRoom(source.RoomPlayers);
            Check(sourceRules.Elements.Count == target.Elements.Count, "대상 원소 수");
            for (int i = 0; i < target.Elements.Count; i++)
                Check((string)sourceRules.Elements[i]["id"] == target.Elements[i].Id, "원소 인덱스 변경");
            var reactions = (JArray)sourceRules.Root["reactions"];
            Check(reactions.Count == target.Reactions.Count, "반응 수 변경");
            for (int i = 0; i < reactions.Count; i++) Check((string)reactions[i]["id"] == target.Reactions[i].Id, "반응 인덱스 변경");

            json["SchemaVersion"] = 3; json["DataHash"] = target.DataHash; json["Candidates"] = new JArray();
            var geometry = new WorldState(target, source.Seed, false, source.Tick);
            foreach (BuildingData building in source.Buildings)
                geometry.Buildings.Add(new BuildingState(building.Id, building.Owner, building.Kind, building.Position, target, building.TowerElement)
                { ConstructionEndTick = building.ConstructionEndTick });
            foreach (JObject player in json["Players"])
            {
                int main = (int)player["MainElement"];
                Vector2 origin = player["BasePosition"].ToObject<Vector2>();
                Check(RecallSystem.TryCreateLayout(geometry, origin, out Vector2[] slots), "기존 시설 사이의 귀환 공간 부족");
                player["RecallArrivalSlots"] = JArray.FromObject(slots);
                player["RecallSquad"] = 0; player["RecallEndTick"] = 0; player["RecallReadyTick"] = 0; player["RecallSequence"] = 0UL;
                player["FocusAnchor"] = 1; player["NextDefenseAlarmTick"] = 0;
                // 모르는 과거 거래는 추정하지 않고 옛 누적을 새 확인 이후 결과로 다시 표시하지 않는다.
                var journey = new JObject { ["Converted"] = (long)player["Converted"], ["Rescued"] = 0L, ["Lost"] = 0L,
                    ["CoresIn"] = 0L, ["CoresSpent"] = 0L, ["BuildingsDestroyed"] = 0L, ["SiegeWins"] = (long)player["BattleWins"] };
                player["JourneyTotal"] = journey;
                player["JourneyAcknowledged"] = journey.DeepClone();
                var squads = new JArray();
                for (int i = 1; i <= 4; i++)
                {
                    JToken position = i == 4 ? JObject.FromObject(slots[0]) : player[i == 1 ? "LegionPosition" : "BasePosition"];
                    squads.Add(new JObject { ["Id"] = i, ["Position"] = position.DeepClone(), ["Destination"] = position.DeepClone(),
                        ["Direction"] = JObject.FromObject(Vector2.Zero), ["Heading"] = i == 1 ? player["Heading"].DeepClone() : JObject.FromObject(Vector2.UnitY),
                        ["Order"] = 0, ["AppliedSequence"] = 0UL, ["ElementMask"] = 0UL, ["MovementRevision"] = 0,
                        ["FormationDirty"] = true, ["FollowFormation"] = false });
                }
                player["Squads"] = squads;
                var training = (JArray)player["Training"];
                int owner = (int)player["Id"];
                int freeGuard = Math.Max(0, target.GarrisonCap((int)player["BaseLevel"]) - source.Units.Count(unit => unit.Owner == owner && unit.Garrison));
                foreach (JObject order in training) order["TargetSquad"] = freeGuard-- > 0 ? 4 : 1;
                ulong last = training.Count == 0 ? 0 : (ulong)training[training.Count - 1]["Id"];
                Check(last < ulong.MaxValue, "양성 주문 번호 소진");
                player["NextTrainingOrderId"] = last + 1;
                player["TrainingStartTick"] = training.Count == 0 ? 0 : source.Tick;
                player["TrainingEndTick"] = training.Count == 0 ? 0 : checked(source.Tick + (long)Math.Ceiling(
                    target.TrainingSeconds((int)training[0]["Element"], main) * target.TickRate));
                player["TrainingPausedAtTick"] = training.Count > 0 && (long)player["GhostEndTick"] != 0 ? source.Tick : -1;
            }
            var keptGuards = new Dictionary<int, int>();
            foreach (JObject unit in json["Units"])
            {
                int owner = (int)unit["Owner"];
                bool guard = (bool)unit["Garrison"], overflow = false;
                if (guard)
                {
                    keptGuards.TryGetValue(owner, out int count);
                    int level = source.Players.First(player => player.Id == owner).BaseLevel;
                    overflow = count >= target.GarrisonCap(level);
                    keptGuards[owner] = count + 1;
                }
                unit["FreeOrigin"] = false; unit["RescuedOnce"] = false;
                unit["SquadId"] = owner < 0 || overflow ? 0 : guard ? 4 : 1;
                unit["Garrison"] = guard && !overflow; unit["DestinationSquadId"] = overflow ? 1 : 0;
                unit["IsReinforcing"] = overflow; unit["MovementRevision"] = 0;
                unit["KillerSquad"] = (int)unit["KillerOwner"] < 0 ? 0 : (bool)unit["KillerTower"] ? 4 : 1;
                unit["LastHitSquads"] = new JArray(((JArray)unit["LastHitOwners"]).Select(value => (int)value < 0 ? 0 : 1));
                foreach (JObject status in unit["Statuses"])
                    status["SourceSquad"] = (int)status["LegionOwner"] >= 0 ? 1 : 0;
            }
            foreach (JObject building in json["Buildings"])
            { building["FreeOrigin"] = false; building["KillerSquad"] = (int)building["KillerOwner"] < 0 ? 0 : 1; }
            foreach (JObject area in json["Areas"]) area["SourceSquad"] = 1;
            if (source.Ecology != null) MapEcology(json, source, sourceRules, target, geometry);
            WorldCheckpoint result = json.ToObject<WorldCheckpoint>(Serializer());
            SimulationWorld.RestoreCheckpoint(target, result);
            return result;
        }

        private static void ValidateVectors(JObject json)
        {
            foreach (JProperty property in json.Descendants().OfType<JProperty>())
            {
                if (property.Name == "Sites" && property.Value is JArray sites)
                { foreach (JToken site in sites) RequireVector(site); }
                else if (property.Name == "BasePosition" || property.Name == "LegionPosition" || property.Name == "Heading" ||
                    property.Name == "Position" || property.Name == "Home" || property.Name == "Anchor") RequireVector(property.Value);
            }
        }

        private static void ValidateFieldNames(JToken input, JToken canonical)
        {
            // Json.NET의 대소문자 별칭은 거절하고, 검증한 원본 수치 토큰은 그대로 매핑한다.
            if (input is JObject source && canonical is JObject expected)
                foreach (JProperty property in source.Properties())
                {
                    JProperty field = expected.Property(property.Name, StringComparison.Ordinal);
                    if (field == null) throw new JsonSerializationException("원본 필드 이름의 대소문자가 v1 스키마와 다릅니다.");
                    ValidateFieldNames(property.Value, field.Value);
                }
            else if (input is JArray values && canonical is JArray fields)
                for (int i = 0; i < values.Count; i++) ValidateFieldNames(values[i], fields[i]);
        }

        private static void RequireVector(JToken token)
        {
            if (!(token is JObject point) || point.Count != 2 ||
                !(point["X"]?.Type == JTokenType.Integer || point["X"]?.Type == JTokenType.Float) ||
                !(point["Y"]?.Type == JTokenType.Integer || point["Y"]?.Type == JTokenType.Float))
                throw new JsonSerializationException("원본 좌표에는 X/Y 수치가 모두 필요합니다.");
        }

        private static void ValidateSource(LegacyCheckpoint source, LegacyRules rules)
        {
            Check(source != null && source.DataHash == rules.DataHash, "원본 게임 데이터 해시");
            Check(source.Tick >= 0 && source.Tick < long.MaxValue / 2 && source.NextId > 0 && source.NextBuildingId > 0 && source.NextAreaId > 0 &&
                (source.RoomPlayers == 0 || source.RoomPlayers >= 2 && source.RoomPlayers <= rules.MaxPlayers), "원본 틱 또는 다음 번호");
            Check(source.Players != null && source.Players.Length <= rules.MaxPlayers && source.Units != null && source.Units.Length <= Protocol.MaxUnits &&
                source.Buildings != null && source.Buildings.Length <= GameplayCodec.MaxBuildings && source.Cores != null && source.Cores.Length <= GameplayCodec.MaxObjects &&
                source.Areas != null && source.Areas.Length <= GameplayCodec.MaxObjects, "원본 개체 수");
            float map = rules.MapSize(source.RoomPlayers);
            double time = (double)source.Tick / rules.TickRate;
            int previous = -1, usedColors = 0;
            var players = new Dictionary<int, PlayerData>();
            var slots = new HashSet<int>();
            foreach (PlayerData player in source.Players)
            {
                Check(player != null && player.Id > previous && player.Slot >= 0 && player.Slot < rules.MaxPlayers && slots.Add(player.Slot) &&
                    PlayerNickname.IsValid(player.Nickname) && rules.Element(player.MainElement) &&
                    ((JArray)rules.Root.SelectToken("start.allowedElements")).Any(value => (string)value == (string)rules.Elements[player.MainElement]["id"]) &&
                    player.BaseLevel >= 1 && player.BaseLevel <= ((JArray)rules.Root["baseLevels"]).Count &&
                    Inside(map, player.BasePosition) && Inside(map, player.LegionPosition) && Finite(player.Heading.X) && Finite(player.Heading.Y) && player.Heading.LengthSquared() <= 1.001f &&
                    player.Cores >= 0 && player.BattleWins >= 0 && player.Trained >= 0 && player.Converted >= 0 && player.GarrisonArrivals >= 0 &&
                    (player.GhostEndTick == 0 || player.GhostEndTick >= source.Tick && (player.GhostEndTick - source.Tick <= Math.Ceiling(rules.Number("ghost.decaySeconds") * rules.TickRate) || source.Tick == 0 && player.GhostEndTick == 1)), "원본 플레이어");
                Check(player.Trained <= long.MaxValue - player.Converted, "원본 획득 합계");
                if (player.ColorId.HasValue)
                { Check(PlayerPalette.IsValid(player.ColorId.Value) && (usedColors & (1 << player.ColorId.Value)) == 0, "원본 소유 색"); usedColors |= 1 << player.ColorId.Value; }
                var vision = new VisionGrid(map, (float)rules.Number("world.visionCellSize"));
                Check(vision.TryRestoreExplored(player.Explored), "원본 탐색 기록");
                Check(player.Training != null && player.Training.Length <= rules.PopulationCap(player.BaseLevel) &&
                    (player.GhostEndTick == 0 || player.Training.Length == 0), "원본 양성 대기열");
                ulong order = 0;
                foreach (TrainingData item in player.Training)
                { Check(item.Id > order && rules.Element(item.Element) && item.Cost == rules.TrainingCost(item.Element, player.MainElement), "원본 유료 양성 주문"); order = item.Id; }
                players.Add(player.Id, player); previous = player.Id;
            }
            var bases = new HashSet<int>(); previous = 0;
            foreach (BuildingData building in source.Buildings)
            {
                Check(building.Id > previous && building.Id < source.NextBuildingId && building.Kind >= BuildingKind.Base && building.Kind <= BuildingKind.Dome &&
                    players.ContainsKey(building.Owner) && Inside(map, building.Position) && rules.Element(building.KillerElement) &&
                    building.KillerOwner >= -1 && building.KillerSource >= 0 && building.KillerSource < source.NextId && Finite(building.AttackRemaining), "원본 건물");
                string key = BuildingKey(building.Kind);
                string path = key == "towers" ? key : "buildings." + key;
                Check(Finite(building.Health) && building.Health > 0 && building.Health <= rules.Number(path + ".hp") &&
                    (building.Kind == BuildingKind.NatureTower ? rules.Element(building.TowerElement) && rules.IsNature(building.TowerElement) :
                    building.Kind == BuildingKind.EnergyTower ? rules.Element(building.TowerElement) && !rules.IsNature(building.TowerElement) : building.TowerElement == -1), "원본 건물 종류/체력");
                Check(building.ConstructionEndTick == 0 || building.Kind != BuildingKind.Base && building.ConstructionEndTick > source.Tick &&
                    building.ConstructionEndTick - source.Tick <= Math.Ceiling(rules.Number(path + ".buildSeconds") * rules.TickRate) && players[building.Owner].GhostEndTick == 0, "원본 건설 시각");
                if (building.Kind == BuildingKind.Base) Check(building.ConstructionEndTick == 0 && building.Position == players[building.Owner].BasePosition && bases.Add(building.Owner), "원본 본진");
                previous = building.Id;
            }
            Check(bases.Count == players.Count, "원본 본진 수");
            var units = new Dictionary<int, UnitData>(); previous = 0;
            foreach (UnitData unit in source.Units)
            {
                Check(unit != null && unit.Id > previous && unit.Id < source.NextId && rules.Element(unit.Element) &&
                    (unit.Owner == -1 && !unit.Garrison || players.ContainsKey(unit.Owner)) && Inside(map, unit.Position) && Finite(unit.Home.X) && Finite(unit.Home.Y) &&
                    Finite(unit.Health) && unit.Health > 0 && unit.Health <= (float)rules.Elements[unit.Element]["hp"] &&
                    Finite(unit.Shield) && unit.Shield >= 0 && Finite(unit.RangeMultiplier) && unit.RangeMultiplier >= 0 && Finite(unit.SpeedMultiplier) && unit.SpeedMultiplier >= 0 &&
                    Finite(unit.Haste) && unit.Haste >= 0 && Debuff(unit.SmokeDebuff) && Debuff(unit.SteamDebuff) && Debuff(unit.SandDebuff) &&
                    Finite(unit.AttackRemaining) && Finite(unit.ShieldRemaining) && unit.Target >= 0 && unit.Target < source.NextId &&
                    unit.BuildingTarget >= 0 && unit.BuildingTarget < source.NextBuildingId && unit.Detour >= 0 && unit.Detour < source.NextBuildingId &&
                    unit.DetourSide >= -1 && unit.DetourSide <= 1 && unit.KillerOwner >= -1 && unit.KillerSource >= 0 &&
                    unit.KillerSource < (unit.KillerTower ? source.NextBuildingId : source.NextId) && rules.Element(unit.KillerElement), "원본 유닛");
                Check(unit.Statuses != null && unit.Statuses.Length == (int)StatusKind.Count && unit.LastHitTimes != null && unit.LastHitTimes.Length == rules.Elements.Count &&
                    unit.LastHitOwners != null && unit.LastHitOwners.Length == rules.Elements.Count, "원본 전투 배열 길이");
                foreach (StatusData status in unit.Statuses)
                    Check(Finite(status.Expires) && status.Expires >= 0 && Finite(status.Amount) && Finite(status.Dps) && status.Dps >= 0 && status.Owner >= -1 &&
                        status.LegionOwner >= -1 && status.Source >= 0 && status.Source < source.NextId && rules.Element(status.Element), "원본 상태 이상");
                for (int i = 0; i < unit.LastHitTimes.Length; i++)
                    Check((double.IsNegativeInfinity(unit.LastHitTimes[i]) || Finite(unit.LastHitTimes[i]) && unit.LastHitTimes[i] >= 0 && unit.LastHitTimes[i] <= time) &&
                        unit.LastHitOwners[i] >= -1, "원본 공격 귀속");
                units.Add(unit.Id, unit); previous = unit.Id;
            }
            foreach (PlayerData player in source.Players)
                Check(source.Units.Count(unit => unit.Owner == player.Id) + player.Training.Length <= rules.PopulationCap(player.BaseLevel), "원본 예약 인구");
            previous = 0;
            foreach (CoreData core in source.Cores)
            { Check(core.Id > previous && core.Id < source.NextId && !units.ContainsKey(core.Id) && core.Amount > 0 && Inside(map, core.Position) && Finite(core.Expires) && core.Expires > time, "원본 핵"); previous = core.Id; }
            previous = 0;
            foreach (AreaData area in source.Areas)
            {
                Check(area.Id > previous && area.Id < source.NextAreaId && area.Reaction >= 0 && area.Reaction < ((JArray)rules.Root["reactions"]).Count &&
                    players.ContainsKey(area.Owner) && area.Source > 0 && area.Source < source.NextId && area.Target > 0 && area.Target < source.NextId &&
                    rules.Element(area.Element) && Inside(map, area.Position) && Finite(area.Expires) && area.Expires > time - 1.0 / rules.TickRate, "원본 반응 영역");
                string reaction = (string)rules.Root["reactions"][area.Reaction]["id"];
                Check(reaction == "steam" || reaction == "sandstorm", "원본 영역 종류"); previous = area.Id;
            }
            if (source.Ecology != null) ValidateEcology(source, rules, map, units);
        }

        private static void ValidateEcology(LegacyCheckpoint source, LegacyRules rules, float map, Dictionary<int, UnitData> units)
        {
            EcologyData ecology = source.Ecology;
            int siteCount = source.RoomPlayers == 0 ? rules.MaxPlayers : source.RoomPlayers;
            int columns = (int)Math.Ceiling(Math.Sqrt(siteCount)), rows = (siteCount + columns - 1) / columns;
            float spacing = map / (columns + 1);
            double density = rules.Number(source.RoomPlayers == 0 ? "neutral.worldGroupsPerSquareKm" : "neutral.roomGroupsPerSquareKm");
            int groupCount = (int)Math.Ceiling(map * (double)map * density / 1_000_000);
            Check(Finite(ecology.MinBaseDistance) && ecology.MinBaseDistance > 0 && ecology.MinBaseDistance <= spacing &&
                ecology.Sites != null && ecology.Sites.Length == siteCount && ecology.Groups != null && ecology.Groups.Length == groupCount &&
                ecology.Schedules != null && ecology.Schedules.Length == rules.MaxPlayers, "원본 생태 배열 크기");
            var remaining = new HashSet<Vector2>();
            for (int i = 0; i < siteCount; i++)
            {
                int row = i / columns, rowCount = Math.Min(columns, siteCount - row * columns);
                remaining.Add(source.RoomPlayers == 0 ? new Vector2((i % columns + 1) * spacing, (row + 1) * spacing) :
                    new Vector2((i % columns + 1) * map / (rowCount + 1), (row + 1) * map / (rows + 1)));
            }
            foreach (Vector2 site in ecology.Sites) Check(remaining.Remove(site), "원본 입장 자리");
            foreach (PlayerData player in source.Players) Check(Array.IndexOf(ecology.Sites, player.BasePosition) >= 0, "원본 본진의 입장 자리");
            int energyGroups = (int)Math.Floor((groupCount - siteCount) * rules.Number("neutral.energyGroupRatio"));
            var live = new HashSet<int>();
            for (int i = 0; i < groupCount; i++)
            {
                GroupData group = ecology.Groups[i];
                bool starter = i < siteCount;
                int count = group.Slots?.Length ?? 0;
                Check(group.StarterSite == (starter ? i : -1) && group.Energy == (i >= groupCount - energyGroups) &&
                    rules.Element(group.Element) && rules.IsNature(group.Element) != group.Energy && Finite(group.Anchor.X) && Finite(group.Anchor.Y) &&
                    count > 0 && (count >= rules.Number("neutral.groupSizeMin") && count <= rules.Number("neutral.groupSizeMax") ||
                        !starter && count == rules.Number("neutral.groupSizeSolo")), "원본 중립 무리");
                foreach (SlotData slot in group.Slots)
                {
                    Check(slot.Unit >= 0 && slot.ReadyTick >= 0 && slot.ReadyTick - source.Tick <= Math.Max(
                        Math.Ceiling(rules.Number("neutral.respawnSeconds") * rules.TickRate), rules.TickRate), "원본 중립 재생성 시각");
                    if (slot.Unit != 0)
                        Check(units.TryGetValue(slot.Unit, out UnitData unit) && unit.Owner == -1 && unit.EcologyNeutral &&
                            unit.Element == group.Element && live.Add(slot.Unit), "원본 중립 자리 참조");
                }
            }
            foreach (UnitData unit in source.Units)
                Check(!unit.EcologyNeutral || unit.Owner >= 0 || live.Contains(unit.Id), "원본 중립 자리 누락");
            foreach (ScheduleData schedule in ecology.Schedules)
                Check(schedule.Owner >= 0 && schedule.Credit >= 0 && schedule.Credit < 60L * rules.TickRate, "원본 공급 크레딧");
        }

        private static void MapEcology(JObject json, LegacyCheckpoint source, LegacyRules old, GameRules target, WorldState geometry)
        {
            EcologyData ecology = source.Ecology;
            var mapped = new JArray();
            var activeBySite = new Dictionary<int, ScheduleData>();
            var assigned = new HashSet<int>();
            for (int index = 0; index < ecology.Schedules.Length; index++)
            {
                ScheduleData schedule = ecology.Schedules[index];
                PlayerData player = source.Players.FirstOrDefault(item => item.Id == schedule.Owner && item.Slot == index);
                if (player == null)
                {
                    Check(schedule.Credit == 0, "사라진 소유자의 공급 크레딧 자리를 확정할 수 없음");
                    continue;
                }
                int site = Array.IndexOf(ecology.Sites, player.BasePosition);
                Check(site >= 0 && assigned.Add(site), "공급 자리 연결 중복");
                activeBySite.Add(site, schedule);
            }
            long threshold = 60L * target.TickRate * target.OpeningCoreBundleSize;
            Check(old.TickRate == target.TickRate, "틱 빈도 변경");
            for (int i = 0; i < ecology.Sites.Length; i++)
            {
                Vector2 site = ecology.Sites[i];
                Vector2 start = site + new Vector2(0, target.BaseRadius + target.SlotSpacing * (target.StartFieldUnitCount + 1));
                Vector2 near = default, shared = default;
                bool found = false;
                float nearDistance = Math.Max(target.OpeningFirstCoreMinDistance, Math.Min(target.OpeningFirstCoreMaxDistance, target.VisionRadius));
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    double angle = attempt * Math.PI * 2 / 64;
                    Vector2 candidate = start + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * nearDistance;
                    if (!SupplyPoint(geometry, candidate) || !SupplyPath(geometry, ecology.Sites, start, candidate)) continue;
                    near = candidate; found = true; break;
                }
                Check(found, "기존 시설 사이의 가까운 공급 경로 부족");
                float nearest = float.MaxValue; shared = near;
                for (int j = 0; j < ecology.Sites.Length; j++)
                {
                    if (i == j) continue;
                    Vector2 candidate = (site + ecology.Sites[j]) * 0.5f;
                    float distance = Vector2.DistanceSquared(site, ecology.Sites[j]);
                    if (distance >= nearest || !SupplyPoint(geometry, candidate) ||
                        !SupplyPath(geometry, ecology.Sites, start, candidate) || !SupplyPath(geometry, ecology.Sites,
                            ecology.Sites[j] + new Vector2(0, target.BaseRadius + target.SlotSpacing * (target.StartFieldUnitCount + 1)), candidate)) continue;
                    float clearance = Math.Max(target.BuildRadius, target.BaseRadius + target.MinimumSeparation);
                    if (ecology.Sites.Any(other => Vector2.DistanceSquared(other, candidate) <= clearance * clearance)) continue;
                    nearest = distance; shared = candidate;
                }
                Check(ecology.Sites.Length == 1 || nearest != float.MaxValue, "기존 시설 사이의 공용 공급 경로 부족");
                activeBySite.TryGetValue(i, out ScheduleData schedule);
                int phase = 0;
                if (schedule.GlobalNext)
                {
                    int total = target.OpeningNearbyBundlesPerMinute + target.OpeningSharedBundlesPerMinute;
                    for (; phase < total; phase++)
                        if ((long)phase * target.OpeningSharedBundlesPerMinute / total != (long)(phase + 1) * target.OpeningSharedBundlesPerMinute / total) break;
                    Check(phase < total, "공용 공급 phase 없음");
                }
                long credit = schedule.Credit;
                Check(credit <= threshold, "대상 공급 크레딧 상한");
                long next = target.MapCoreSpawnPerPlayerPerMinute == 0 ? source.Tick : checked(source.Tick +
                    (threshold - credit + target.MapCoreSpawnPerPlayerPerMinute - 1) / target.MapCoreSpawnPerPlayerPerMinute);
                mapped.Add(new JObject { ["Site"] = i, ["NearPosition"] = JObject.FromObject(near), ["SharedPosition"] = JObject.FromObject(shared),
                    ["Credit"] = credit, ["Phase"] = phase, ["FirstGranted"] = true, ["LastTick"] = source.Tick, ["NextTick"] = next });
            }
            json["Ecology"]["Schedules"] = mapped;
            foreach (JObject group in json["Ecology"]["Groups"])
                foreach (JObject slot in group["Slots"]) slot["Reserved"] = (int)slot["Unit"] == 0;
        }

        private static bool SupplyPoint(WorldState world, Vector2 point)
        {
            if (!Inside(world.Rules.MapSize, point)) return false;
            foreach (BuildingState building in world.Buildings)
                if (building.BlocksMovement && Vector2.DistanceSquared(point, building.Position) < building.Radius * building.Radius) return false;
            return true;
        }
        private static bool SupplyPath(WorldState world, Vector2[] sites, Vector2 from, Vector2 to)
        {
            foreach (BuildingState building in world.Buildings)
                if (building.BlocksMovement && CircleCollision.Limit(from, to - from, building.Position, building.Radius) < 1) return false;
            foreach (Vector2 site in sites)
                if (CircleCollision.Limit(from, to - from, site, world.Rules.BaseRadius) < 1) return false;
            return true;
        }

        private static string BuildingKey(BuildingKind kind)
        {
            switch (kind)
            {
                case BuildingKind.Base: return "base";
                case BuildingKind.NatureForge: return "forge_nature";
                case BuildingKind.EnergyForge: return "forge_energy";
                case BuildingKind.Collector: return "collector";
                case BuildingKind.Dome: return "dome";
                default: return "towers";
            }
        }
        private static bool Inside(float map, Vector2 point) => Finite(point.X) && Finite(point.Y) && point.X >= 0 && point.Y >= 0 && point.X <= map && point.Y <= map;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Debuff(float value) => value >= -1 && value <= 0;
        private static void Check(bool valid, string field) { if (!valid) throw new ArgumentException("구형 월드 이관 검증 실패: " + field); }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class LegacyCheckpoint
        {
            public string DataHash { get; set; }
            public long Tick { get; set; }
            [JsonProperty(Required = Required.Default)] public int RoomPlayers { get; set; }
            public int NextId { get; set; } public int NextBuildingId { get; set; } public int NextAreaId { get; set; } public int Seed { get; set; }
            public PlayerData[] Players { get; set; }
            public UnitData[] Units { get; set; }
            public BuildingData[] Buildings { get; set; }
            public CoreData[] Cores { get; set; }
            public AreaData[] Areas { get; set; }
            [JsonProperty(Required = Required.AllowNull)] public EcologyData Ecology { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class PlayerData
        {
            [JsonProperty(Required = Required.Default)] public int? ColorId { get; set; }
            public string Nickname { get; set; }
            public int Id { get; set; } public int Slot { get; set; } public int MainElement { get; set; } public int BaseLevel { get; set; } public int BattleWins { get; set; }
            public Vector2 BasePosition { get; set; } public Vector2 LegionPosition { get; set; } public Vector2 Heading { get; set; }
            public long Cores { get; set; } public long GhostEndTick { get; set; } public long Trained { get; set; } public long Converted { get; set; } public long GarrisonArrivals { get; set; }
            public byte[] Explored { get; set; }
            public TrainingData[] Training { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct TrainingData
        {
            public ulong Id { get; set; }
            public int Element { get; set; } public int Cost { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class UnitData
        {
            public int Id { get; set; } public int Owner { get; set; } public int Element { get; set; } public int Target { get; set; } public int BuildingTarget { get; set; } public int Detour { get; set; } public int DetourSide { get; set; }
            public int KillerOwner { get; set; } public int KillerSource { get; set; } public int KillerElement { get; set; }
            public Vector2 Position { get; set; } public Vector2 Home { get; set; }
            public float Health { get; set; } public float Shield { get; set; } public float RangeMultiplier { get; set; } public float SpeedMultiplier { get; set; } public float Haste { get; set; } public float SmokeDebuff { get; set; } public float SteamDebuff { get; set; } public float SandDebuff { get; set; }
            public double AttackRemaining { get; set; } public double ShieldRemaining { get; set; }
            public bool Converted { get; set; } public bool Garrison { get; set; } public bool WasNeutral { get; set; } public bool EcologyNeutral { get; set; } public bool NeutralReturning { get; set; } public bool CatchingUp { get; set; } public bool KillerTower { get; set; }
            public StatusData[] Statuses { get; set; }
            public double[] LastHitTimes { get; set; }
            public int[] LastHitOwners { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct StatusData
        {
            public double Expires { get; set; }
            public float Amount { get; set; } public float Dps { get; set; }
            public int Owner { get; set; } public int Source { get; set; } public int Element { get; set; } public int LegionOwner { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct BuildingData
        {
            public int Id { get; set; } public int Owner { get; set; } public int TowerElement { get; set; } public int KillerOwner { get; set; } public int KillerSource { get; set; } public int KillerElement { get; set; }
            public BuildingKind Kind { get; set; }
            public Vector2 Position { get; set; }
            public float Health { get; set; }
            public double AttackRemaining { get; set; }
            public long ConstructionEndTick { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct CoreData
        {
            public int Id { get; set; } public int Amount { get; set; }
            public Vector2 Position { get; set; }
            public double Expires { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct AreaData
        {
            public int Id { get; set; } public int Reaction { get; set; } public int Owner { get; set; } public int Source { get; set; } public int Element { get; set; } public int Target { get; set; }
            public Vector2 Position { get; set; }
            public double Expires { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        internal sealed class EcologyData
        {
            public float MinBaseDistance { get; set; }
            public Vector2[] Sites { get; set; }
            public GroupData[] Groups { get; set; }
            public ScheduleData[] Schedules { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        internal struct GroupData
        {
            public int Element { get; set; } public int StarterSite { get; set; }
            public bool Energy { get; set; }
            public Vector2 Anchor { get; set; }
            public SlotData[] Slots { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        internal struct SlotData
        {
            public int Unit { get; set; }
            public long ReadyTick { get; set; }
        }

        [JsonObject(ItemRequired = Required.Always)]
        internal struct ScheduleData
        {
            public int Owner { get; set; }
            public long Credit { get; set; }
            public bool GlobalNext { get; set; }
        }
    }
}
