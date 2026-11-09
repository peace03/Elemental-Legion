using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

public enum ScenarioKind { Distributed, Clustered, Continuous, MainWorldDefense, MainWorldRecovery, MainWorldDense }

/// <summary>성능 검증용 배치를 생성한다. 최대 인구·레벨·혼합 원소는 시나리오에만 적용한다.</summary>
public static class ScenarioFactory
{
    // M0 비교 부하는 인구 상한이 늘어나도 같은 15×50기를 유지한다.
    private const int BenchmarkUnitsPerPlayer = 50;
    public static WorldSetup Create(GameRules rules, int bots, ScenarioKind kind, bool fullPopulation = true)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (bots < 1 || bots > rules.MaxPlayers) throw new ArgumentOutOfRangeException(nameof(bots));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (IsMainWorld(kind)) return CreateMainWorld(rules, bots, kind, ScenarioKind.Clustered);
        var setup = new WorldSetup { CollectTimings = true };
        int columns = (int)Math.Ceiling(Math.Sqrt(bots));
        float baseSpacing = rules.MapSize / (columns + 1);
        Vector2 center = new(rules.MapSize * 0.5f);
        var legionPositions = new Vector2[bots];
        int population = fullPopulation ? BenchmarkUnitsPerPlayer : rules.StartUnitCount;
        int formationColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(population)));
        for (int player = 0; player < bots; player++)
        {
            Vector2 basePosition = new((player % columns + 1) * baseSpacing, (player / columns + 1) * baseSpacing);
            double angle = 2 * Math.PI * player / bots;
            Vector2 legionPosition = kind == ScenarioKind.Distributed ? basePosition + new Vector2(0, rules.BaseRadius + rules.VisionRadius) : center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * rules.LeashDistance;
            legionPositions[player] = legionPosition;
            string main = rules.Elements[rules.StartingElements[player % rules.StartingElements.Count]].Id;
            setup.Players.Add(new PlayerSpawn(player + 1, main, basePosition, legionPosition, fullPopulation ? rules.MaxBaseLevel : 1, rules.StartingCores, true, fullPopulation));
            for (int unit = 0; unit < population; unit++)
            {
                string element = fullPopulation ? rules.Elements[(unit + player) % rules.Elements.Count].Id : main;
                Vector2 offset = new((unit % formationColumns - (formationColumns - 1) * 0.5f) * rules.SlotSpacing, (unit / formationColumns - (formationColumns - 1) * 0.5f) * rules.SlotSpacing);
                setup.Units.Add(new UnitSpawn(player + 1, element, legionPosition + offset));
            }
        }
        int groups = (int)Math.Ceiling((double)rules.MapSize * rules.MapSize / 1_000_000 * rules.NeutralGroupsPerSquareKm);
        int groupColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(rules.NeutralBenchmarkMaxGroupSize)));
        for (int group = 0; group < groups; group++)
        {
            ElementDefinition element = rules.Elements[group % rules.Elements.Count];
            Vector2 position;
            if (element.Group == ElementGroup.Energy)
                position = center + new Vector2(group % 5 - 2, group / 5 % 5 - 2) * rules.SlotSpacing * 2;
            else if (kind == ScenarioKind.Distributed)
                position = legionPositions[group % bots] + new Vector2(rules.VisionRadius * 0.5f, (group / bots % 3 - 1) * rules.SlotSpacing * 2);
            else position = center + new Vector2(group % 6 - 2.5f, group / 6 - 2.5f) * rules.SlotSpacing * 2;
            for (int unit = 0; unit < rules.NeutralBenchmarkMaxGroupSize; unit++)
            {
                Vector2 offset = new(unit % groupColumns - (groupColumns - 1) * 0.5f, unit / groupColumns - (groupColumns - 1) * 0.5f);
                setup.Units.Add(new UnitSpawn(-1, element.Id, Vector2.Clamp(position + offset * rules.SlotSpacing, Vector2.Zero, new Vector2(rules.MapSize))));
            }
        }
        return setup;
    }

    public static bool IsMainWorld(ScenarioKind kind) => kind is ScenarioKind.MainWorldDefense or ScenarioKind.MainWorldRecovery or ScenarioKind.MainWorldDense;

    public static bool TryParseMainWorld(string name, out ScenarioKind kind)
    {
        kind = name switch
        {
            "main-world-defense" => ScenarioKind.MainWorldDefense,
            "main-world-recovery" => ScenarioKind.MainWorldRecovery,
            "main-world-dense" => ScenarioKind.MainWorldDense,
            _ => ScenarioKind.Distributed
        };
        return IsMainWorld(kind);
    }

    /// <summary>밀집의 최악 인구와 실제 생태를 쓰는 행동 시험을 구분한다.</summary>
    public static WorldSetup CreateMainWorld(GameRules rules, int bots, ScenarioKind kind, ScenarioKind layout)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (bots < 1 || bots > rules.MaxPlayers) throw new ArgumentOutOfRangeException(nameof(bots));
        if (!IsMainWorld(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (layout is not (ScenarioKind.Distributed or ScenarioKind.Clustered or ScenarioKind.Continuous)) throw new ArgumentOutOfRangeException(nameof(layout));
        var setup = new WorldSetup { CollectTimings = true, CameraVisibility = true };
        int columns = (int)Math.Ceiling(Math.Sqrt(rules.MaxPlayers));
        float spacing = rules.MapSize / (columns + 1);
        Vector2 center = new(rules.MapSize * 0.5f);
        bool dense = kind == ScenarioKind.MainWorldDense;
        for (int player = 0; player < bots; player++)
        {
            Vector2 home = new((player % columns + 1) * spacing, (player / columns + 1) * spacing);
            double angle = 2 * Math.PI * player / bots;
            Vector2 field = dense && layout != ScenarioKind.Distributed
                ? center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * rules.LeashDistance
                : home + new Vector2(0, rules.BaseRadius + rules.SlotSpacing * (rules.StartFieldUnitCount + 1));
            if (kind == ScenarioKind.MainWorldDefense && player == 0) field = home + new Vector2(0, rules.VisionRadius * 4);
            if (kind == ScenarioKind.MainWorldDefense && player == 1) field = new Vector2(spacing + rules.BaseRadius + 1, spacing);
            if (kind == ScenarioKind.MainWorldRecovery) field = center + new Vector2(player % 2 * rules.SlotSpacing, player / 2 * rules.SlotSpacing);
            string main = rules.Elements[rules.StartingElements[player % rules.StartingElements.Count]].Id;
            setup.Players.Add(new PlayerSpawn(player + 1, main, home, field, dense ? rules.MaxBaseLevel : 1,
                rules.StartingCores, !dense, !dense));
            int population = dense ? rules.MaxPopulation : rules.StartUnitCount;
            int formationColumns = (int)Math.Ceiling(Math.Sqrt(population));
            for (int unit = 0; unit < population; unit++)
            {
                bool guard = !dense && unit >= rules.StartFieldUnitCount;
                Vector2 offset = new((unit % formationColumns - (formationColumns - 1) * 0.5f) * rules.SlotSpacing,
                    (unit / formationColumns - (formationColumns - 1) * 0.5f) * rules.SlotSpacing);
                Vector2 point = guard ? home + new Vector2((unit - rules.StartFieldUnitCount) * rules.SlotSpacing, rules.BaseRadius + rules.SlotSpacing * 2) : field + offset;
                string element = dense ? rules.Elements[(unit + player) % rules.Elements.Count].Id : main;
                setup.Units.Add(new UnitSpawn(player + 1, element, point, garrison: guard));
            }
        }
        if (!dense) return setup;
        int neutralCount = checked((int)Math.Ceiling((double)rules.MapSize * rules.MapSize / 1_000_000 * rules.NeutralWorldGroupsPerSquareKm) * rules.NeutralMaxGroupSize);
        for (int unit = 0; unit < neutralCount; unit++)
        {
            Vector2 anchor = layout == ScenarioKind.Distributed ? setup.Players[unit % bots].LegionPosition : center;
            Vector2 offset = new((unit % 32 - 15.5f) * rules.SlotSpacing, (unit / 32 % 32 - 15.5f) * rules.SlotSpacing);
            setup.Units.Add(new UnitSpawn(-1, rules.Elements[unit % rules.Elements.Count].Id,
                Vector2.Clamp(anchor + offset, Vector2.Zero, new Vector2(rules.MapSize))));
        }
        return setup;
    }

    /// <summary>소유자당 상한 시험과 구분하는 40 대 80, 합계 120기의 교전이다.</summary>
    public static WorldSetup CreateRecoveryBattle(GameRules rules)
    {
        var setup = CreateMainWorld(rules, 2, ScenarioKind.MainWorldRecovery, ScenarioKind.Continuous);
        setup.Units.Clear();
        Vector2 center = new(rules.MapSize * 0.5f);
        for (int player = 0; player < 2; player++)
        {
            PlayerSpawn spawn = setup.Players[player];
            setup.Players[player] = new PlayerSpawn(spawn.Id, spawn.MainElement, spawn.BasePosition, center + new Vector2(player * 6, 0), rules.MaxBaseLevel);
            for (int unit = 0; unit < (player == 0 ? 40 : 80); unit++)
                setup.Units.Add(new UnitSpawn(player + 1, spawn.MainElement, center + new Vector2(player * 6 + unit % 8 * rules.SlotSpacing, unit / 8 * rules.SlotSpacing)));
        }
        return setup;
    }
}
