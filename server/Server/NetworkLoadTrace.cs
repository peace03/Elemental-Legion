using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;

namespace ElementalLegion.Server;

/// <summary>M6 측정 표본을 틱에서 수집하고 파일 쓰기는 별도 작업에서 처리한다.</summary>
internal sealed class NetworkLoadTrace : IDisposable
{
    internal const string PathVariable = "ELEMENTAL_LEGION_LOAD_TRACE";

    private readonly record struct Sample(long Tick, double Milliseconds, int Connected, int PlayerUnits, int NeutralUnits, long Attacks, long? AllocatedBytes);

    private readonly Channel<Sample> samples = Channel.CreateBounded<Sample>(new BoundedChannelOptions(2048)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Task writer;
    private readonly string path;
    private readonly SemaphoreSlim backupWriter = new(1, 1);
    private long lastTick;
    internal long LastTick => Interlocked.Read(ref lastTick);

    private NetworkLoadTrace(FileStream stream, GameRules rules, string transport)
    {
        path = stream.Name;
        writer = Task.Run(async () =>
        {
            await using var output = new StreamWriter(stream);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Protocol.Version, rules.DataHash, rules.TickRate, Transport = transport
            }));
            await output.FlushAsync();
            var sinceFlush = Stopwatch.StartNew();
            await foreach (var sample in samples.Reader.ReadAllAsync())
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(sample));
                if (sinceFlush.ElapsedMilliseconds < 250) continue;
                await output.FlushAsync();
                sinceFlush.Restart();
            }
        });
    }

    internal static NetworkLoadTrace? Open(GameRules rules, string transport)
    {
        string? path = Environment.GetEnvironmentVariable(PathVariable);
        return string.IsNullOrEmpty(path) ? null : Create(path, rules, transport);
    }

    internal static NetworkLoadTrace Create(string path, GameRules rules, string transport) => new(
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), rules, transport);

    internal void Record(NetworkWorld world, long started, long? allocatedBefore = null)
    {
        var metrics = world.World.Metrics;
        // 표본을 잃으면 보고서가 틱 번호의 빈 구간을 감지해 측정을 실패 처리한다.
        if (!samples.Writer.TryWrite(new Sample(metrics.Tick, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            world.ConnectedCount, metrics.PlayerUnits, metrics.NeutralUnits, metrics.Attacks,
            allocatedBefore.HasValue ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore.Value) : null)))
            throw new InvalidDataException("서버 틱 표본 버퍼가 가득 차 측정을 계속할 수 없습니다.");
        Interlocked.Exchange(ref lastTick, metrics.Tick);
    }

    /// <summary>압축과 전송 비용은 틱 파일과 분리하며 마지막 검증된 백업으로 장애를 재현한다.</summary>
    internal async Task RecordBackup(ulong generation, ulong backupNumber, long tick, byte[] compressed,
        double encodeMilliseconds, long encodeAllocatedBytes, double transferMilliseconds)
    {
        if (generation == 0 || backupNumber == 0 || tick < 0 || compressed.Length == 0 ||
            compressed.Length > MigrationSnapshotCodec.MaxCompressedBytes || encodeAllocatedBytes < 0 ||
            !double.IsFinite(encodeMilliseconds) || encodeMilliseconds < 0 || !double.IsFinite(transferMilliseconds) || transferMilliseconds < 0)
            throw new ArgumentException("승계 측정 값이 잘못됐습니다.");
        await backupWriter.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(path + ".migration.ndjson", JsonSerializer.Serialize(new
            {
                Generation = generation, BackupNumber = backupNumber, Tick = tick, CompressedBytes = compressed.Length,
                EncodeMilliseconds = encodeMilliseconds, EncodeAllocatedBytes = encodeAllocatedBytes, TransferMilliseconds = transferMilliseconds
            }) + Environment.NewLine);
            string temporary = path + ".snapshot.tmp";
            await File.WriteAllBytesAsync(temporary, compressed);
            File.Move(temporary, path + ".snapshot.br", overwrite: true);
        }
        finally { backupWriter.Release(); }
    }

    internal void RecordFault(Exception error, ulong generation, long tick)
    {
        try
        {
            var faults = new List<object>();
            for (Exception? current = error; current != null && faults.Count < 8; current = current.InnerException)
                faults.Add(new { Type = current.GetType().FullName, Stack = current.StackTrace is { } stack ? stack[..Math.Min(stack.Length, 8192)] : null });
            // 예외 메시지는 입력값을 포함할 수 있어 타입과 호출 위치만 보관한다.
            File.WriteAllText(path + ".fault.json", JsonSerializer.Serialize(new { Generation = generation, Tick = tick, Faults = faults }));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        samples.Writer.TryComplete();
        writer.GetAwaiter().GetResult();
        backupWriter.Dispose();
    }
}
