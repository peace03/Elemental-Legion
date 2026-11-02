using System;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>난수 추출과 상태 변경을 분리한 전향 판정이다. 실제 추출은 월드 RNG 하나가 담당한다.</summary>
    public static class ConversionRules
    {
        public static bool ShouldConvert(GameRules rules, bool alreadyConverted, bool towerKill, bool neutralVictim, int availablePopulation, double roll)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (double.IsNaN(roll) || roll < 0 || roll >= 1) throw new ArgumentOutOfRangeException(nameof(roll));
            return !alreadyConverted && !towerKill && availablePopulation > 0 && roll < (neutralVictim ? rules.NeutralConversionChance : rules.ConversionChance);
        }
    }
}
