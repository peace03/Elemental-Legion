namespace ElementalLegion.Server;

/// <summary>실제 Stopwatch 표본을 정렬해 최근접 순위 p99와 틱 예산 초과를 계산한다.</summary>
public sealed class TickStatistics
{
    public double MeanMs { get; private set; }
    public double P99Ms { get; private set; }
    public double MaxMs { get; private set; }
    public int OverBudgetCount { get; private set; }

    public static TickStatistics Calculate(ReadOnlySpan<long> samples, double frequency, double tickBudgetMs = 50)
    {
        if (samples.Length == 0) throw new ArgumentException("측정 표본이 없습니다.", nameof(samples));
        if (!double.IsFinite(frequency) || frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (!double.IsFinite(tickBudgetMs) || tickBudgetMs <= 0) throw new ArgumentOutOfRangeException(nameof(tickBudgetMs));
        long[] sorted = samples.ToArray();
        double sum = 0;
        int over = 0;
        foreach (long sample in sorted)
        {
            if (sample < 0) throw new ArgumentException("음수 시간 표본입니다.", nameof(samples));
            sum += sample;
            if (sample * 1000.0 / frequency > tickBudgetMs) over++;
        }
        Array.Sort(sorted);
        return new TickStatistics
        {
            MeanMs = sum / sorted.Length * 1000 / frequency,
            P99Ms = sorted[(int)Math.Ceiling(sorted.Length * 0.99) - 1] * 1000.0 / frequency,
            MaxMs = sorted[sorted.Length - 1] * 1000.0 / frequency,
            OverBudgetCount = over
        };
    }
}
