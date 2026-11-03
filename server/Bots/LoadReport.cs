using System.Diagnostics;
using System.Text.Json;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Bots;

internal sealed record LoadTickSummary(int Samples, double MeanMs, double P99Ms, double MaxMs, int OverBudget,
    int MinConnected, int MaxConnected, int MinPlayerUnits, int MaxPlayerUnits, int MaxNeutralUnits, long Attacks)
{
    private sealed record Header(int Version, string DataHash, int TickRate, string Transport);
    private sealed record TickRecord(long Tick, double Milliseconds, int Connected, int PlayerUnits, int NeutralUnits, long Attacks);

    internal static async Task<LoadTickSummary> Read(string path, string transport, int version, string dataHash, int tickRate,
        long firstTick, long lastTick, long traceBytesAtStart, CancellationToken stop)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(3))
        {
            stop.ThrowIfCancellationRequested();
            if (File.Exists(path) && ReadAvailable() is { } result) return result;
            await Task.Delay(100, stop);
        }
        throw new InvalidDataException("서버 틱 표본이 누락됐습니다. 새 표본 파일로 서버를 실행하세요.");

        LoadTickSummary? ReadAvailable()
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= traceBytesAtStart) return null;
            using var input = new StreamReader(stream);
            if (input.ReadLine() is not { } headerLine) return null;
            var header = JsonSerializer.Deserialize<Header>(headerLine);
            if (header == null || header.Version != version || header.DataHash != dataHash || header.TickRate != tickRate || header.Transport != transport)
                throw new InvalidDataException("서버 틱 표본의 프로토콜, 게임 데이터 또는 전송 경로가 일치하지 않습니다.");
            var samples = new List<double>();
            int minConnected = int.MaxValue, maxConnected = 0, minUnits = int.MaxValue, maxUnits = 0, maxNeutral = 0;
            long previous = firstTick - 1, initialAttacks = 0;
            while (input.ReadLine() is { } line)
            {
                TickRecord? sample;
                try { sample = JsonSerializer.Deserialize<TickRecord>(line); }
                catch (JsonException) when (input.EndOfStream) { return null; }
                if (sample == null || !double.IsFinite(sample.Milliseconds) || sample.Milliseconds < 0)
                    throw new InvalidDataException("잘못된 서버 틱 표본입니다.");
                if (sample.Tick < firstTick) continue;
                if (sample.Tick != previous + 1) throw new InvalidDataException("서버 틱 표본이 연속적이지 않습니다.");
                previous = sample.Tick;
                if (sample.Tick == firstTick) { initialAttacks = sample.Attacks; continue; }
                samples.Add(sample.Milliseconds);
                minConnected = Math.Min(minConnected, sample.Connected);
                maxConnected = Math.Max(maxConnected, sample.Connected);
                minUnits = Math.Min(minUnits, sample.PlayerUnits);
                maxUnits = Math.Max(maxUnits, sample.PlayerUnits);
                maxNeutral = Math.Max(maxNeutral, sample.NeutralUnits);
                if (sample.Tick == lastTick)
                    return new LoadTickSummary(samples.Count, samples.Average(), Percentile(samples, .99)!.Value,
                        samples.Max(), samples.Count(value => value > 1000d / tickRate), minConnected, maxConnected,
                        minUnits, maxUnits, maxNeutral, sample.Attacks - initialAttacks);
            }
            return null;
        }
    }

    internal static double? Percentile(List<double> samples, double fraction)
    {
        if (samples.Count == 0) return null;
        samples.Sort();
        return samples[(int)Math.Ceiling(samples.Count * fraction) - 1];
    }

    internal static long TraceLength(string path)
    {
        if (!File.Exists(path)) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Length;
    }

    internal static List<double> AppliedLatencies(IEnumerable<LoadPeer.MoveSample> moves) => moves
        .Where(move => move.Response == CommandAdmission.Accepted && move.Applied != 0)
        .Select(move => Stopwatch.GetElapsedTime(move.Sent, move.Applied).TotalMilliseconds).ToList();
}
