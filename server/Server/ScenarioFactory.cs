using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

public enum ScenarioKind { Distributed, Clustered, Continuous }

/// <summary>성능 검증용 배치를 생성한다. 최대 인구·레벨·혼합 원소는 시나리오에만 적용한다.</summary>
public static class ScenarioFactory
{
    public static WorldSetup Create(GameRules rules, int bots, ScenarioKind kind, bool fullPopulation = true)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (bots < 1 || bots > rules.MaxPlayers) throw new ArgumentOutOfRangeException(nameof(bots));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var setup = new WorldSetup { CollectTimings = true };
        int columns = (int)Math.Ceiling(Math.Sqrt(bots));
        float baseSpacing = rules.MapSize / (columns + 1);
        Vector2 center = new(rules.MapSize * 0.5f);
        var legionPositions = new Vector2[bots];
        int population = fullPopulation ? rules.MaxPopulation : rules.StartUnitCount;
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
}
