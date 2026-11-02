using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using System.Diagnostics;

namespace ElementalLegion.Server;

/// <summary>데이터 로딩과 콘솔 수명을 소유하고 환경 확인·M0 실행을 연결한다.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        GameDataDocument data;
        GameRules rules;
        try
        {
            data = new GameDataDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
            rules = new GameRules(data);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine("게임 데이터 초기화 실패: " + exception.Message);
            return 1;
        }
        ServerOptions options;
        try { options = ServerOptions.Parse(args, rules.MaxPlayers); }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        if (options.Bots == 0)
        {
            Console.WriteLine($"Elemental Legion 환경 확인: tickRate={rules.TickRate}, port={data.Root["server"]!["port"]}");
            Console.WriteLine($"gamedata SHA-256: {data.Sha256}");
            Console.WriteLine("M0 봇 실행: --bots 15 [--report-tick]. UDP 접속은 M1에서 구현합니다.");
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (options.ReportTick)
            {
                var results = BenchmarkRunner.Run(rules, options.Bots, Console.Out, cancellation.Token);
                return results.All(result => result.Passed) ? 0 : 3;
            }
            WorldSetup setup = ScenarioFactory.Create(rules, options.Bots, ScenarioKind.Distributed, false);
            var world = new SimulationWorld(rules, setup, BenchmarkRunner.Seed);
            BotBrain[] bots = BenchmarkRunner.CreateBots(rules, setup);
            Console.WriteLine($"M0 서버 내부 봇 {options.Bots}명, {rules.TickRate}Hz. UDP 소켓을 열지 않습니다. Ctrl+C로 종료합니다.");
            var clock = Stopwatch.StartNew();
            double deadline = 0;
            while (!cancellation.IsCancellationRequested)
            {
                BenchmarkRunner.StepBots(world, setup, bots); world.Step();
                deadline += rules.DeltaTime;
                double remaining = deadline - clock.Elapsed.TotalSeconds;
                if (remaining > 0) cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(remaining));
            }
            Console.WriteLine($"종료: ticks={world.Metrics.Tick}; units={world.Metrics.PlayerUnits}+{world.Metrics.NeutralUnits}; attacks={world.Metrics.Attacks}");
            return 0;
        }
        catch (OperationCanceledException) { Console.WriteLine("M0 측정을 취소했습니다."); return 0; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
