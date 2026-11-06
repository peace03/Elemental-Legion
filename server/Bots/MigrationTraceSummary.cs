using System.Globalization;
using System.Text.Json;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;

namespace ElementalLegion.Bots;

internal sealed record MigrationTraceGeneration(ulong Generation, string File, int Samples, long FirstTick, long LastTick,
    long PotentialUnflushedTicks, long Attacks);
internal sealed record MigrationBackupSummary(int Samples, long CompressedBytes, long EncodeAllocatedBytes,
    double MeanEncodeMs, double? P95EncodeMs, double MaxEncodeMs, double MeanTransferMs, double? P95TransferMs, double MaxTransferMs);

/// <summary>각 서버 파일 안의 연속성을 검사하고 세대가 다른 같은 틱은 별도 표본으로 합산한다.</summary>
internal sealed record MigrationTraceSummary(int Samples, double MeanMs, double? P99Ms, double MaxMs, int OverBudget,
    int MinConnected, int MaxConnected, int MinPlayerUnits, int MaxPlayerUnits, int MaxNeutralUnits, long Attacks,
    MigrationTraceGeneration[] Generations, int AllocationSamples, double? MeanAllocatedBytes, long? MaxAllocatedBytes,
    MigrationBackupSummary Backups)
{
    private sealed record Header(int Version, string DataHash, int TickRate, string Transport);
    private sealed record Sample(long Tick, double Milliseconds, int Connected, int PlayerUnits, int NeutralUnits, long Attacks, long? AllocatedBytes = null);
    private sealed record Backup(ulong Generation, ulong BackupNumber, long Tick, long CompressedBytes,
        double EncodeMilliseconds, long EncodeAllocatedBytes, double TransferMilliseconds);

    internal static MigrationTraceSummary Read(string prefix, GameRules rules, long firstMeasuredTick,
        IEnumerable<(ulong Generation, long Tick)> observed, IReadOnlyDictionary<ulong, string> activeFiles,
        IReadOnlyCollection<string> launchedFiles, ulong firstMeasuredGeneration = 1)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var launched = launchedFiles.Select(Path.GetFullPath).ToHashSet(comparer);
        var lastObserved = observed.Where(item => item.Tick >= 0).GroupBy(item => item.Generation)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Tick));
        ulong lastMeasuredGeneration = lastObserved.Keys.DefaultIfEmpty(0UL).Max();
        var selected = new List<Sample>();
        var backups = new List<Backup>();
        var generations = new List<MigrationTraceGeneration>();
        var seen = new HashSet<ulong>();
        string directory = Path.GetDirectoryName(prefix)!;
        string name = Path.GetFileName(prefix);
        foreach (string path in Directory.EnumerateFiles(directory).Where(path =>
            Path.GetFileName(path).StartsWith(name + ".bot", StringComparison.Ordinal) && path.EndsWith(".ticks.ndjson", StringComparison.Ordinal)))
        {
            if (!launched.Contains(Path.GetFullPath(path)))
                throw new InvalidDataException("이 실행에서 시작하지 않은 서버의 틱 표본이 포함됐습니다.");
            string stem = path[..^".ticks.ndjson".Length];
            if (!ulong.TryParse(stem[(stem.LastIndexOf('.') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out ulong generation) || generation == 0)
                throw new InvalidDataException("서버 표본의 세대를 확인하지 못했습니다.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var input = new StreamReader(stream);
            Header? header = JsonSerializer.Deserialize<Header>(input.ReadLine() ?? "{}");
            if (header == null || header.Version != Protocol.Version || header.DataHash != rules.DataHash ||
                header.TickRate != rules.TickRate || header.Transport != "wss")
                throw new InvalidDataException("승계 서버 틱 표본의 프로토콜·데이터·전송 경로가 다릅니다.");
            var records = new List<Sample>();
            while (input.ReadLine() is { } line)
            {
                Sample sample = JsonSerializer.Deserialize<Sample>(line) ?? throw new InvalidDataException("서버 틱 표본이 비어 있습니다.");
                if (sample.Tick < 0 || !double.IsFinite(sample.Milliseconds) || sample.Milliseconds < 0 ||
                    sample.Connected < 0 || sample.Connected > rules.MaxPlayers || sample.PlayerUnits < 0 || sample.NeutralUnits < 0 ||
                    sample.Attacks < 0 || sample.AllocatedBytes < 0)
                    throw new InvalidDataException("승계 서버 틱 표본의 값이 잘못됐습니다.");
                if (records.Count > 0 && (sample.Tick != records[^1].Tick + 1 || sample.Attacks < records[^1].Attacks))
                    throw new InvalidDataException("같은 서버 세대의 틱 표본이 누락되거나 역행했습니다.");
                records.Add(sample);
            }
            if (records.Count == 0) continue;
            if (!activeFiles.TryGetValue(generation, out string? activePath) ||
                !comparer.Equals(Path.GetFullPath(path), Path.GetFullPath(activePath)) || !lastObserved.ContainsKey(generation))
                throw new InvalidDataException("관측한 호스트와 세대가 다른 서버의 틱 표본이 포함됐습니다.");
            if (!seen.Add(generation)) throw new InvalidDataException("같은 세대에 둘 이상의 판정 서버가 틱을 기록했습니다.");
            long last = lastObserved[generation];
            // 세대가 끝난 서버의 최종 동결 캡처는 마지막 클라이언트 위치보다 뒤에 있을 수 있다.
            bool Measured(long tick) => (generation > firstMeasuredGeneration || generation == firstMeasuredGeneration && tick > firstMeasuredTick) &&
                (generation < lastMeasuredGeneration || tick <= last);
            Sample[] measured = records.Where(sample => Measured(sample.Tick)).ToArray();
            selected.AddRange(measured);
            long attacks = measured.Length < 2 ? 0 : measured[^1].Attacks - measured[0].Attacks;
            generations.Add(new MigrationTraceGeneration(generation, path, measured.Length, records[0].Tick,
                records[^1].Tick, Math.Max(0, last - records[^1].Tick), attacks));
            string backupFile = path + ".migration.ndjson";
            if (File.Exists(backupFile))
            {
                ulong previous = 0;
                foreach (string line in File.ReadLines(backupFile))
                {
                    Backup row = JsonSerializer.Deserialize<Backup>(line) ?? throw new InvalidDataException("승계 백업 표본이 비어 있습니다.");
                    if (row.Generation != generation || row.BackupNumber <= previous || row.Tick < 0 || row.CompressedBytes <= 0 ||
                        row.CompressedBytes > 8 * 1024 * 1024 || row.EncodeAllocatedBytes < 0 ||
                        !double.IsFinite(row.EncodeMilliseconds) || row.EncodeMilliseconds < 0 ||
                        !double.IsFinite(row.TransferMilliseconds) || row.TransferMilliseconds < 0)
                        throw new InvalidDataException("승계 백업 표본의 세대·순서·측정 값이 잘못됐습니다.");
                    previous = row.BackupNumber;
                    if (Measured(row.Tick)) backups.Add(row);
                }
            }
        }
        if (lastObserved.Keys.Any(generation => !seen.Contains(generation)))
            throw new InvalidDataException("관측한 서버 세대의 틱 표본 파일이 누락되거나 비어 있습니다.");
        long[] allocations = selected.Where(sample => sample.AllocatedBytes.HasValue).Select(sample => sample.AllocatedBytes!.Value).ToArray();
        return new MigrationTraceSummary(selected.Count,
            selected.Count == 0 ? 0 : selected.Average(sample => sample.Milliseconds),
            LoadTickSummary.Percentile(selected.Select(sample => sample.Milliseconds).ToList(), .99),
            selected.Count == 0 ? 0 : selected.Max(sample => sample.Milliseconds),
            selected.Count(sample => sample.Milliseconds > 1000d / rules.TickRate),
            selected.Count == 0 ? 0 : selected.Min(sample => sample.Connected), selected.Count == 0 ? 0 : selected.Max(sample => sample.Connected),
            selected.Count == 0 ? 0 : selected.Min(sample => sample.PlayerUnits), selected.Count == 0 ? 0 : selected.Max(sample => sample.PlayerUnits),
            selected.Count == 0 ? 0 : selected.Max(sample => sample.NeutralUnits), generations.Sum(epoch => epoch.Attacks),
            generations.OrderBy(epoch => epoch.Generation).ToArray(), allocations.Length,
            allocations.Length == 0 ? null : allocations.Average(), allocations.Length == 0 ? null : allocations.Max(),
            new MigrationBackupSummary(backups.Count, backups.Sum(row => row.CompressedBytes), backups.Sum(row => row.EncodeAllocatedBytes),
                backups.Count == 0 ? 0 : backups.Average(row => row.EncodeMilliseconds),
                LoadTickSummary.Percentile(backups.Select(row => row.EncodeMilliseconds).ToList(), .95),
                backups.Count == 0 ? 0 : backups.Max(row => row.EncodeMilliseconds),
                backups.Count == 0 ? 0 : backups.Average(row => row.TransferMilliseconds),
                LoadTickSummary.Percentile(backups.Select(row => row.TransferMilliseconds).ToList(), .95),
                backups.Count == 0 ? 0 : backups.Max(row => row.TransferMilliseconds)));
    }
}
