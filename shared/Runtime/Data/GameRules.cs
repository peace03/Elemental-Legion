using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;

namespace ElementalLegion.Shared.Data
{
    public enum ElementGroup { Nature, Energy }
    public enum UnitRole { Melee, Mid, Ranged }
    public enum EffectKind { None, Chill, Burn, SmokeAura, Wet, Chain, Lifesteal, ShieldAura, HasteAura, Splash, Pierce, Radiation, Heal }
    public enum ReactionKind { Electrocute, Freeze, Steam, BurnSpread, Sandstorm, PlasmaBurst }
    public enum StatusKind { Wet, Chill, Burn, Radiation, Freeze, EarthMark, Smoke, Count }

    /// <summary>월드마다 공유하는 불변 원소 정의다. JSON은 로딩 때만 읽는다.</summary>
    public sealed class ElementDefinition
    {
        public string Id { get; internal set; }
        public ElementGroup Group { get; internal set; }
        public UnitRole Role { get; internal set; }
        public float Hp { get; internal set; }
        public float Attack { get; internal set; }
        public float AttacksPerSecond { get; internal set; }
        public float Range { get; internal set; }
        public EffectKind Effect { get; internal set; }
        public float Amount { get; internal set; }
        public float Duration { get; internal set; }
        public float Radius { get; internal set; }
        public float DamagePerSecond { get; internal set; }
        public float Ratio { get; internal set; }
        public float Interval { get; internal set; }
        public float Absorb { get; internal set; }
        public float Heal { get; internal set; }
        public float Width { get; internal set; }
        public int ExtraTargets { get; internal set; }
        public int MaxTargets { get; internal set; }
    }

    /// <summary>반응의 원소·상태 참조를 정수 인덱스로 검증해 보관한다.</summary>
    public sealed class ReactionDefinition
    {
        public string Id { get; internal set; }
        public ReactionKind Kind { get; internal set; }
        public int Trigger { get; internal set; }
        public ulong RequiredMask { get; internal set; }
        public StatusKind Status { get; internal set; }
        public int PreviousHitElement { get; internal set; } = -1;
        public float Window { get; internal set; }
        public float DamageMultiplier { get; internal set; } = 1;
        public float ChainBonus { get; internal set; }
        public float SpeedDebuff { get; internal set; }
        public float RangeDebuff { get; internal set; }
        public float Duration { get; internal set; }
        public float Radius { get; internal set; }
        public float Damage { get; internal set; }
        public float DamagePerSecond { get; internal set; }
    }

    /// <summary>M0에서 사용하는 데이터만 검증하고 틱 중 재사용할 형태로 변환한다.</summary>
    public sealed class GameRules
    {
        private readonly Dictionary<string, int> elementIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly float[,] matchups;
        private readonly Dictionary<int, int> populationCaps = new Dictionary<int, int>();
        private readonly Dictionary<int, HashSet<string>> unlocks = new Dictionary<int, HashSet<string>>();
        public ReadOnlyCollection<ElementDefinition> Elements { get; }
        public ReadOnlyCollection<ReactionDefinition> Reactions { get; }
        public string DataHash { get; }
        public int TickRate { get; }
        public double DeltaTime => 1.0 / TickRate;
        public float MapSize { get; }
        public float LegionSpeed { get; }
        public float VisionRadius { get; }
        public float CellSize { get; }
        public float SlotSpacing { get; }
        public float LeashDistance { get; }
        public float GarrisonJoinRadius { get; }
        public float RallySpeedMultiplier { get; }
        public float BaseRadius { get; }
        public float NatureForgeRadius { get; }
        public float EnergyForgeRadius { get; }
        public int MaxPlayers { get; }
        public int MaxPopulation { get; }
        public int MaxBaseLevel { get; }
        public int CommandRateLimit { get; }
        public int StartUnitCount { get; }
        public int StartingCores { get; }
        public ReadOnlyCollection<int> StartingElements { get; }
        public int NatureCost { get; }
        public int EnergyCost { get; }
        public decimal MainDiscount { get; }
        public double ConversionChance { get; }
        public double NeutralConversionChance { get; }
        public decimal DropRatio { get; }
        public float ConvertedHealthRatio { get; }
        public float CorePickupRadius { get; }
        public double CoreLifetime { get; }
        public double NatureTrainSeconds { get; }
        public double EnergyTrainSeconds { get; }
        public float NeutralAttackMultiplier { get; }
        public float ConvertedNeutralAttackMultiplier { get; }
        public float NeutralGroupsPerSquareKm { get; }
        public int NeutralMaxGroupSize { get; }

        public GameRules(GameDataDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            JObject root = document.Root;
            DataHash = document.Sha256;
            TickRate = Integer(root, "world.tickRate", 1);
            MapSize = Positive(root, "world.mapSize");
            LegionSpeed = Positive(root, "world.legionSpeed");
            VisionRadius = Positive(root, "world.visionRadius");
            CellSize = Positive(root, "world.visionCellSize");
            if (MapSize / CellSize > 4096) throw Invalid("world.visionCellSize", "격자 크기가 월드에 비해 너무 작습니다.");
            SlotSpacing = Positive(root, "legion.formation.slotSpacing");
            LeashDistance = Positive(root, "legion.leashDistance");
            GarrisonJoinRadius = Positive(root, "legion.garrisonJoinRadius");
            RallySpeedMultiplier = Positive(root, "legion.rally.speedMultiplier");
            BaseRadius = Positive(root, "buildings.base.radius");
            NatureForgeRadius = Positive(root, "buildings.forge_nature.radius");
            EnergyForgeRadius = Positive(root, "buildings.forge_energy.radius");
            MaxPlayers = Integer(root, "server.maxPlayers", 1, 63);
            CommandRateLimit = Integer(root, "server.commandRateLimitPerSecond", 1);
            MaxPopulation = Integer(root, "population.max", 1);
            StartUnitCount = Integer(root, "start.unitCount", 0, MaxPopulation);
            StartingCores = Integer(root, "start.startingCores", 0);
            NatureCost = Integer(root, "economy.natureUnitCost", 1);
            EnergyCost = Integer(root, "economy.energyUnitCost", 1);
            MainDiscount = (decimal)Ratio(root, "economy.mainElementDiscount");
            ConversionChance = Ratio(root, "economy.conversionChance");
            NeutralConversionChance = Ratio(root, "neutral.conversionChance");
            DropRatio = (decimal)Ratio(root, "economy.coreDropRatio");
            ConvertedHealthRatio = (float)Ratio(root, "economy.convertedHealthRatio");
            if (ConvertedHealthRatio <= 0) throw Invalid("economy.convertedHealthRatio", "0보다 커야 합니다.");
            CorePickupRadius = Positive(root, "economy.corePickupRadius");
            CoreLifetime = Positive(root, "economy.droppedCoreLifetimeSeconds");
            NatureTrainSeconds = Positive(root, "training.natureTrainSeconds");
            EnergyTrainSeconds = Positive(root, "training.energyTrainSeconds");
            NeutralAttackMultiplier = Positive(root, "neutral.attackMultiplier");
            ConvertedNeutralAttackMultiplier = Positive(root, "neutral.attackMultiplierAfterConversion");
            NeutralGroupsPerSquareKm = Positive(root, "neutral.groupsPerSquareKm");
            NeutralMaxGroupSize = Integer(root, "neutral.groupSizeMax", 1);
            Require(root, "economy.coreDropRounding", "floor");
            Require(root, "economy.mainElementDiscountAppliesTo", "nature");
            Require(root, "economy.conversionOwner", "last_hit");
            Require(root, "economy.convertedUnitAlwaysDropsCore", true);
            Require(root, "economy.towerKillsConvert", false);
            Require(root, "economy.overPopulationBecomesCore", true);
            Require(root, "legion.formation.meleeFront", true);

            var definitions = new List<ElementDefinition>();
            foreach (JToken token in Array(root, "elements"))
            {
                string id = Text(token, "id");
                if (elementIndices.ContainsKey(id) || definitions.Count >= 63) throw Invalid("elements", "원소 ID 중복 또는 개수 초과: " + id);
                var definition = new ElementDefinition
                {
                    Id = id,
                    Group = ParseGroup(Text(token, "group")),
                    Role = ParseRole(Text(token, "role")),
                    Hp = Positive(token, "hp"), Attack = Positive(token, "attack"),
                    AttacksPerSecond = Positive(token, "attacksPerSecond"), Range = Positive(token, "range")
                };
                JToken effectToken = token["effect"] ?? throw Invalid(id, "effect가 누락됐습니다.");
                if (effectToken is JObject effect) ReadEffect(definition, effect);
                else if (effectToken.Type != JTokenType.Null) throw Invalid(id, "effect는 객체 또는 null이어야 합니다.");
                if (token["targeting"] != null && Text(token, "targeting") != "nearest_injured_ally") throw Invalid(id, "알 수 없는 표적 방식입니다.");
                if (definition.Effect == EffectKind.Heal && token.Value<string>("targeting") != "nearest_injured_ally") throw Invalid(id, "회복 표적 방식이 누락됐습니다.");
                elementIndices.Add(id, definitions.Count);
                definitions.Add(definition);
            }
            if (definitions.Count == 0) throw Invalid("elements", "원소가 비어 있습니다.");
            Elements = definitions.AsReadOnly();
            matchups = BuildMatchups(root);
            var starting = new List<int>();
            foreach (JToken id in Array(root, "start.allowedElements"))
            {
                int index = ElementIndex((string)id);
                if (Elements[index].Group != ElementGroup.Nature || starting.Contains(index)) throw Invalid("start.allowedElements", "자연 원소를 중복 없이 지정해야 합니다.");
                starting.Add(index);
            }
            if (starting.Count == 0) throw Invalid("start.allowedElements", "시작 원소가 없습니다.");
            StartingElements = starting.AsReadOnly();

            var inherited = new HashSet<string>(StringComparer.Ordinal);
            int previousCap = 0, previousLevel = 0;
            foreach (JToken level in Array(root, "baseLevels"))
            {
                int number = Integer(level, "level", 1);
                int cap = Integer(level, "populationCap", 1, MaxPopulation);
                if (number != previousLevel + 1 || cap < previousCap) throw Invalid("baseLevels", "레벨과 인구 상한이 순서대로 증가해야 합니다.");
                foreach (JToken name in Array(level, "unlocks")) inherited.Add((string)name);
                populationCaps.Add(number, cap);
                unlocks.Add(number, new HashSet<string>(inherited, StringComparer.Ordinal));
                previousLevel = number;
                previousCap = cap;
            }
            if (previousLevel == 0 || previousCap != MaxPopulation) throw Invalid("baseLevels", "최대 인구 레벨이 없습니다.");
            MaxBaseLevel = previousLevel;
            var reactions = new List<ReactionDefinition>();
            var reactionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken reaction in Array(root, "reactions"))
            {
                var parsed = ReadReaction(reaction);
                if (!reactionIds.Add(parsed.Id)) throw Invalid("reactions", "반응 ID가 중복됐습니다.");
                reactions.Add(parsed);
            }
            Reactions = reactions.AsReadOnly();
        }

        public int ElementIndex(string id)
        {
            if (id == null || !elementIndices.TryGetValue(id, out int index)) throw Invalid("element", "없는 원소: " + id);
            return index;
        }

        public bool TryElementIndex(string id, out int index)
        {
            index = -1;
            return id != null && elementIndices.TryGetValue(id, out index);
        }

        public float Matchup(int attacker, int defender) => matchups[attacker, defender];
        public int PopulationCap(int level) => populationCaps.TryGetValue(level, out int cap) ? cap : throw Invalid("baseLevel", "없는 레벨입니다.");
        public bool IsUnlocked(int level, string item) => unlocks.TryGetValue(level, out HashSet<string> names) && names.Contains(item);
        public int TrainingCost(int element, int mainElement) => (int)(BaseCost(element) * (Elements[element].Group == ElementGroup.Nature && element == mainElement ? 1 - MainDiscount : 1));
        public int BaseCost(int element) => Elements[element].Group == ElementGroup.Nature ? NatureCost : EnergyCost;
        public int DropAmount(int element) => (int)decimal.Floor(BaseCost(element) * DropRatio);

        private float[,] BuildMatchups(JObject root)
        {
            var result = new float[Elements.Count, Elements.Count];
            var positions = new int[Elements.Count];
            var seen = new HashSet<int>();
            var ringSizes = new int[2];
            for (int group = 0; group < 2; group++)
            {
                JArray ring = Array(root, group == 0 ? "rings.nature" : "rings.energy");
                if (ring.Count < 1) throw Invalid("rings", "빈 고리입니다.");
                ringSizes[group] = ring.Count;
                for (int position = 0; position < ring.Count; position++)
                {
                    int element = ElementIndex((string)ring[position]);
                    if ((int)Elements[element].Group != group || !seen.Add(element)) throw Invalid("rings", "원소 그룹 불일치 또는 중복입니다.");
                    positions[element] = position;
                }
            }
            if (seen.Count != Elements.Count) throw Invalid("rings", "고리에 없는 원소가 있습니다.");
            var beats = new HashSet<int>();
            foreach (JToken offset in Array(root, "matchups.beatsOffsets"))
            {
                int value = offset.Value<int>();
                if (value < 1 || value >= Math.Min(ringSizes[0], ringSizes[1]) || !beats.Add(value)) throw Invalid("matchups.beatsOffsets", "잘못된 고리 오프셋입니다.");
            }
            float advantage = Positive(root, "matchups.sameGroupAdvantage"), disadvantage = Positive(root, "matchups.sameGroupDisadvantage");
            float counter = Positive(root, "matchups.energyVsCounterNature"), other = Positive(root, "matchups.energyVsOtherNature"), nature = Positive(root, "matchups.natureVsEnergy");
            for (int a = 0; a < Elements.Count; a++)
            {
                int counterElement = -1;
                if (Elements[a].Group == ElementGroup.Energy)
                {
                    counterElement = ElementIndex(Text(root, "crossCounters." + Elements[a].Id));
                    if (Elements[counterElement].Group != ElementGroup.Nature) throw Invalid("crossCounters", "자연 원소를 지정해야 합니다.");
                }
                for (int b = 0; b < Elements.Count; b++)
                {
                    if (a == b) result[a, b] = 1;
                    else if (Elements[a].Group == Elements[b].Group)
                    {
                        int size = ringSizes[(int)Elements[a].Group];
                        int offset = (positions[b] - positions[a] + size) % size;
                        bool forward = beats.Contains(offset), backward = beats.Contains(size - offset);
                        if (forward == backward) throw Invalid("matchups.beatsOffsets", "모든 고리 쌍의 우열이 하나로 정해져야 합니다.");
                        result[a, b] = forward ? advantage : disadvantage;
                    }
                    else result[a, b] = Elements[a].Group == ElementGroup.Nature ? nature : b == counterElement ? counter : other;
                }
            }
            return result;
        }

        private static void ReadEffect(ElementDefinition definition, JObject effect)
        {
            string type = Text(effect, "type");
            switch (type)
            {
                case "on_hit_attack_speed_debuff": definition.Effect = EffectKind.Chill; definition.Amount = Debuff(effect, "amount"); definition.Duration = Positive(effect, "durationSeconds"); Require(effect, "appliesStatus", "chill"); break;
                case "burn": definition.Effect = EffectKind.Burn; definition.DamagePerSecond = Positive(effect, "damagePerSecond"); definition.Duration = Positive(effect, "durationSeconds"); Require(effect, "appliesStatus", "burn"); break;
                case "enemy_range_aura": definition.Effect = EffectKind.SmokeAura; definition.Amount = Debuff(effect, "amount"); definition.Radius = Positive(effect, "radius"); Require(effect, "appliesStatus", "smoke"); break;
                case "apply_status": definition.Effect = EffectKind.Wet; definition.Duration = Positive(effect, "durationSeconds"); Require(effect, "appliesStatus", "wet"); break;
                case "chain": definition.Effect = EffectKind.Chain; definition.ExtraTargets = Integer(effect, "extraTargets", 0); definition.Ratio = (float)Ratio(effect, "damageRatio"); definition.Radius = Positive(effect, "radius"); break;
                case "lifesteal": definition.Effect = EffectKind.Lifesteal; definition.Ratio = (float)Ratio(effect, "ratio"); break;
                case "ally_shield_aura": definition.Effect = EffectKind.ShieldAura; definition.Absorb = Positive(effect, "absorb"); definition.Interval = Positive(effect, "intervalSeconds"); definition.Radius = Positive(effect, "radius"); break;
                case "ally_attack_speed_aura": definition.Effect = EffectKind.HasteAura; definition.Amount = Positive(effect, "amount"); definition.Radius = Positive(effect, "radius"); break;
                case "splash": definition.Effect = EffectKind.Splash; definition.Radius = Positive(effect, "radius"); break;
                case "pierce": definition.Effect = EffectKind.Pierce; definition.MaxTargets = Integer(effect, "maxTargets", 1); definition.Width = Positive(effect, "width"); break;
                case "radiation_dot": definition.Effect = EffectKind.Radiation; definition.DamagePerSecond = Positive(effect, "damagePerSecond"); definition.Duration = Positive(effect, "durationSeconds"); Require(effect, "stacks", false); break;
                case "heal_nearest_injured_ally": definition.Effect = EffectKind.Heal; definition.Heal = Positive(effect, "healAmount"); break;
                default: throw Invalid("effect.type", "알 수 없는 효과: " + type);
            }
        }

        private ReactionDefinition ReadReaction(JToken token)
        {
            string id = Text(token, "id");
            var definition = new ReactionDefinition { Id = id, Trigger = ElementIndex(Text(token, "triggerElement")), Status = StatusKind.Count };
            foreach (JToken required in Array(token, "requiresInLegion")) definition.RequiredMask |= 1UL << ElementIndex((string)required);
            if (definition.RequiredMask == 0 || (definition.RequiredMask & (1UL << definition.Trigger)) == 0) throw Invalid(id, "발동 원소가 조합에 없습니다.");
            JToken effect = token["effect"] ?? throw Invalid(id, "효과가 없습니다.");
            string status = token.Value<string>("status");
            if (status != null) definition.Status = status == "wet" ? StatusKind.Wet : status == "burn" ? StatusKind.Burn : status == "smoke" ? StatusKind.Smoke : throw Invalid(id, "알 수 없는 상태입니다.");
            switch (id)
            {
                case "electrocute": definition.Kind = ReactionKind.Electrocute; definition.DamageMultiplier = Positive(effect, "triggerDamageMultiplier"); definition.ChainBonus = Positive(effect, "chainRadiusBonus"); break;
                case "freeze": definition.Kind = ReactionKind.Freeze; definition.SpeedDebuff = Debuff(effect, "attackSpeedDebuff"); definition.Duration = Positive(effect, "durationSeconds"); break;
                case "steam": definition.Kind = ReactionKind.Steam; definition.RangeDebuff = Debuff(effect, "areaRangeDebuff"); definition.Duration = Positive(effect, "durationSeconds"); definition.Radius = Positive(effect, "radius"); break;
                case "burn_spread": definition.Kind = ReactionKind.BurnSpread; Require(effect, "spreadStatus", "burn"); definition.Radius = Positive(effect, "radius"); break;
                case "sandstorm": definition.Kind = ReactionKind.Sandstorm; definition.PreviousHitElement = ElementIndex(Text(token, "triggerAfterHitBy")); definition.Window = Positive(token, "triggerWindowSeconds"); definition.Duration = Positive(effect, "durationSeconds"); definition.Radius = Positive(effect, "radius"); definition.DamagePerSecond = Positive(effect, "damagePerSecond"); definition.RangeDebuff = Debuff(effect, "areaRangeDebuff"); break;
                case "plasma_burst": definition.Kind = ReactionKind.PlasmaBurst; definition.Damage = Positive(effect, "damage"); definition.Radius = Positive(effect, "radius"); break;
                default: throw Invalid("reactions", "알 수 없는 반응: " + id);
            }
            if (definition.PreviousHitElement < 0 && definition.Status == StatusKind.Count) throw Invalid(id, "발동 상태가 없습니다.");
            return definition;
        }

        private static ElementGroup ParseGroup(string value) => value == "nature" ? ElementGroup.Nature : value == "energy" ? ElementGroup.Energy : throw Invalid("group", value);
        private static UnitRole ParseRole(string value) => value == "melee" ? UnitRole.Melee : value == "mid" ? UnitRole.Mid : value == "ranged" ? UnitRole.Ranged : throw Invalid("role", value);
        private static JToken Token(JToken root, string path) => root.SelectToken(path) ?? throw Invalid(path, "필수 값이 없습니다.");
        private static string Text(JToken root, string path)
        {
            JToken value = Token(root, path);
            if (value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value)) throw Invalid(path, "문자열이 필요합니다.");
            return (string)value;
        }
        private static JArray Array(JToken root, string path) => Token(root, path) as JArray ?? throw Invalid(path, "배열이 필요합니다.");
        private static double Number(JToken root, string path)
        {
            JToken token = Token(root, path);
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw Invalid(path, "숫자가 필요합니다.");
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue) throw Invalid(path, "유한한 수가 필요합니다.");
            return value;
        }
        private static int Integer(JToken root, string path, int min, int max = int.MaxValue)
        {
            double value = Number(root, path);
            if (value < min || value > max || value != Math.Truncate(value)) throw Invalid(path, "정수 범위를 벗어났습니다.");
            return (int)value;
        }
        private static float Positive(JToken root, string path)
        {
            double value = Number(root, path);
            if (value <= 0) throw Invalid(path, "0보다 커야 합니다.");
            return (float)value;
        }
        private static double Ratio(JToken root, string path)
        {
            double value = Number(root, path);
            if (value < 0 || value > 1) throw Invalid(path, "0~1이어야 합니다.");
            return value;
        }
        private static float Debuff(JToken root, string path)
        {
            double value = Number(root, path);
            if (value <= -1 || value > 0) throw Invalid(path, "-1 초과 0 이하여야 합니다.");
            return (float)value;
        }
        private static void Require(JToken root, string path, object expected)
        {
            if (!JToken.DeepEquals(Token(root, path), JToken.FromObject(expected))) throw Invalid(path, "M0에서 지원하지 않는 규칙입니다.");
        }
        private static ArgumentException Invalid(string path, string message) => new ArgumentException(path + ": " + message);
    }
}
