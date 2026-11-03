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

    private readonly record struct Sample(long Tick, double Milliseconds, int Connected, int PlayerUnits, int NeutralUnits, long Attacks);

    private readonly Channel<Sample> samples = Channel.CreateBounded<Sample>(new BoundedChannelOptions(2048)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Task writer;

    private NetworkLoadTrace(FileStream stream, GameRules rules, string transport)
    {
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

    internal void Record(NetworkWorld world, long started)
    {
        var metrics = world.World.Metrics;
        // 표본을 잃으면 보고서가 틱 번호의 빈 구간을 감지해 측정을 실패 처리한다.
        samples.Writer.TryWrite(new Sample(metrics.Tick, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            world.ConnectedCount, metrics.PlayerUnits, metrics.NeutralUnits, metrics.Attacks));
    }

    public void Dispose()
    {
        samples.Writer.TryComplete();
        writer.GetAwaiter().GetResult();
    }
}
