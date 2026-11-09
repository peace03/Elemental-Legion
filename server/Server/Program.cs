using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Net;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;

namespace ElementalLegion.Server;

/// <summary>데이터 로딩과 콘솔 수명을 소유하고 환경 확인·M0 실행을 연결한다.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(ServerOptions.IsMigrationOption))
        {
            try
            {
                ServerOptions migration = ServerOptions.Parse(args, 1);
                int result = WorldSaveMigration.Run(migration.MigrationSource!, migration.MigrationOutput!, migration.SourceDataPath!, migration.TargetDataPath!);
                Console.WriteLine("월드 이관을 완료했습니다. 출력 폴더의 migration-report.json에서 파일별 검증 결과를 확인하세요.");
                return result;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or OverflowException or InvalidOperationException)
            {
                // JSON 예외에는 입력 필드가 들어갈 수 있으므로 계정 정보가 포함된 원문은 출력하지 않는다.
                Console.Error.WriteLine("월드 이관 실패: " + exception.GetType().Name + ". 원본과 기존 출력은 보존했습니다.");
                return 2;
            }
        }
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
        if (options.HostRoom) return RunHostedRoom(rules, data);
        if (options.RevokeAccount != null) return RevokeOnlineAccount(options);
        if (options.Online) return RunOnline(rules, data, options);
        if (options.Listen) return RunNetwork(rules, data, options);
        if (options.Bots == 0)
        {
            Console.WriteLine($"Elemental Legion 환경 확인: tickRate={rules.TickRate}, port={data.Root["server"]!["port"]}");
            Console.WriteLine($"gamedata SHA-256: {data.Sha256}");
            Console.WriteLine("M0 봇: --bots 15 [--report-tick]. UDP: --listen [--scenario normal|m3-demo|m4-demo|m5-demo]. 인터넷 TLS: --online [--port 443] [--save-dir 경로].");
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (options.ReportTick)
            {
                var results = ScenarioFactory.TryParseMainWorld(options.Scenario, out ScenarioKind selected)
                    ? BenchmarkRunner.Run(rules, new NetworkSettings(data), options.Bots, selected, Console.Out, cancellation.Token)
                    : BenchmarkRunner.Run(rules, options.Bots, Console.Out, cancellation.Token);
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

    private static int RunHostedRoom(GameRules rules, GameDataDocument data)
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new StreamReader(Console.OpenStandardInput());
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            using var trace = NetworkLoadTrace.Open(rules, "wss");
            PlayerHostedServer.Run(rules, new NetworkSettings(data), input, Console.Out, cancellation.Token,
                connectSocketFactory: null, trace).GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or WebSocketException or HttpRequestException or
            ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or CryptographicException)
        {
            Console.Error.WriteLine("방장 서버가 중계 연결을 시작하거나 유지하지 못했습니다.");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static int RunNetwork(GameRules rules, GameDataDocument data, ServerOptions options)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var settings = new NetworkSettings(data);
            bool persistent = options.LanRoom || options.Scenario is "normal" or "m5-demo";
            string directory = options.SaveDirectory ?? (options.LanRoom
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElementalLegion", "LanServer")
                : Path.Combine(Environment.CurrentDirectory, ".server-data", options.Scenario));
            IWorldStore? worlds = persistent ? new FileWorldStore(Path.Combine(directory, "worlds"), rules) : null;
            string worldId = options.LanRoom ? options.RestoreLanRoom ?? Guid.NewGuid().ToString("N") : options.Scenario;
            WorldSave? saved = worlds?.Load(worldId);
            LanRoomOptions? room = null;
            if (options.RestoreLanRoom != null)
            {
                if (saved?.Title == null || saved.LanPort == 0 || saved.MinimumPlayers < 2)
                    throw new InvalidDataException("복원할 LAN 방을 찾지 못했습니다.");
                room = new LanRoomOptions(saved.Title, saved.MinimumPlayers, saved.Capacity, rules.MaxPlayers, new byte[Protocol.AccountKeyBytes]);
            }
            else if (options.LanRoom)
            {
                string? proof = Console.ReadLine();
                if (proof?.Length != Protocol.AccountKeyBytes * 2) throw new ArgumentException("LAN 방장 확인 값이 필요합니다.");
                byte[] key = Convert.FromHexString(proof);
                room = new LanRoomOptions(options.RoomTitle!, options.Minimum, options.Capacity, rules.MaxPlayers, key);
                CryptographicOperations.ZeroMemory(key);
            }
            IAccountStore? accounts = persistent ? new FileAccountStore(Path.Combine(directory, "accounts")) : null;
            NetworkWorld? restored = saved == null ? null : new NetworkWorld(rules, settings, saved.World, saved.NextPlayerId,
                options.Scenario, saved.AiOwners, saved.AiDifficulties);
            using var trace = NetworkLoadTrace.Open(rules, "udp");
            using var server = new UdpGameServer(rules, settings, options.Scenario, restored, accounts, saved?.Players, room) { LoadTrace = trace };
            server.Start(options.RestoreLanRoom != null ? saved!.LanPort : options.Port ??
                (options.LanRoom ? 0 : options.Scenario == "m5-demo" ? settings.DemoPort : settings.Port));
            if (options.LanRoom) { Console.WriteLine($"ROOM_PORT={server.Port}"); Console.Out.Flush(); }
            else
            {
                Console.WriteLine($"M5 UDP 서버: protocol={Protocol.Version}; scenario={options.Scenario}; port={server.Port}; tickRate={rules.TickRate}; maxPlayers={rules.MaxPlayers}");
                Console.WriteLine($"gamedata SHA-256: {rules.DataHash}");
            }
            Task? saving = null;
            long saveInterval = (long)settings.SaveIntervalSeconds * rules.TickRate;
            long lastSave = room != null && saved == null ? -saveInterval : server.Simulation.World.Metrics.Tick;
            WorldSave Capture() => new WorldSave(worldId, server.Simulation.World.CaptureCheckpoint(),
                server.CaptureAccounts(), server.Simulation.NextPlayerId, room?.Title, room?.Minimum ?? 0,
                room?.Capacity ?? 0, room != null ? server.Port : 0, server.Simulation.CaptureAiOwners(), server.Simulation.CaptureAiDifficulties());
            void ScheduleSave()
            {
                if (worlds == null || room != null && (!server.RoomStarted || server.RoomClosed) || saving is { IsCompleted: false }) return;
                long tick = server.Simulation.World.Metrics.Tick;
                if (tick - lastSave < saveInterval) return;
                if (saving?.IsFaulted == true) { _ = saving.Exception; Console.Error.WriteLine("주기 월드 저장에 실패했습니다."); }
                WorldSave snapshot = Capture();
                saving = Task.Run(() => worlds.Save(snapshot));
                lastSave = tick;
            }
            server.Run(cancellation.Token, options.ReportNetwork ? Console.Out : null, options.ReportGameplay ? Console.Out : null,
                persistent ? ScheduleSave : null);
            if (worlds != null && (room == null || server.RoomStarted))
            {
                try { saving?.GetAwaiter().GetResult(); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                { Console.Error.WriteLine("주기 저장이 실패해 종료 시 다시 저장합니다."); }
                if (server.RoomClosed) worlds.Close(worldId);
                else worlds.Save(Capture());
            }
            if (!options.LanRoom) Console.WriteLine($"종료: ticks={server.Simulation.World.Metrics.Tick}; payloadBytes={server.SentPayloadBytes}");
            if (options.ReportNetwork) server.WriteNetworkReport(Console.Out);
            if (options.ReportGameplay) server.WriteGameplayReport(Console.Out);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or System.Net.Sockets.SocketException)
        { Console.Error.WriteLine("M5 UDP 서버 실패: " + exception.Message); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static int RunOnline(GameRules rules, GameDataDocument data, ServerOptions options)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            string path = Environment.GetEnvironmentVariable("ELEMENTAL_LEGION_CERT_PATH") ?? string.Empty;
            if (!options.RelayTest && path.Length == 0) { Console.Error.WriteLine("온라인 서버 TLS 인증서를 환경 설정에 지정하세요."); return 2; }
            using X509Certificate2? certificate = options.RelayTest ? null : X509CertificateLoader.LoadPkcs12FromFile(path,
                Environment.GetEnvironmentVariable("ELEMENTAL_LEGION_CERT_PASSWORD"), X509KeyStorageFlags.DefaultKeySet);
            var app = OnlineGameServer.Build(rules, new NetworkSettings(data), options.RelayTest ? 0 : options.Port ?? 443,
                certificate, OnlineStorage(options),
                allowRoomCreation: Environment.GetEnvironmentVariable("ELEMENTAL_LEGION_DISABLE_ROOM_CREATION") != "1",
                persistentWorldTitle: options.RelayTest ? null : Environment.GetEnvironmentVariable("ELEMENTAL_LEGION_ONLINE_WORLD_TITLE"));
            try
            {
                if (options.RelayTest)
                {
                    app.StartAsync(cancellation.Token).GetAwaiter().GetResult();
                    Console.WriteLine("RELAY_ORIGIN=" + app.Urls.Single());
                    Console.Out.Flush();
                    app.WaitForShutdownAsync(cancellation.Token).GetAwaiter().GetResult();
                }
                else app.RunAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or CryptographicException or InvalidOperationException)
        { Console.Error.WriteLine("온라인 서버 초기화 또는 실행에 실패했습니다. 인증서와 포트·저장 경로를 확인하세요."); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static string OnlineStorage(ServerOptions options) =>
        options.SaveDirectory ?? Path.Combine(Environment.CurrentDirectory, ".server-data", "online");

    private static int RevokeOnlineAccount(ServerOptions options)
    {
        try
        {
            var accounts = new FileAccountStore(Path.Combine(OnlineStorage(options), "accounts"));
            bool revoked = accounts.Revoke(options.RevokeAccount!);
            Console.WriteLine(revoked ? "온라인 계정을 폐기했습니다." : "활성 계정을 찾지 못했습니다.");
            return revoked ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Console.Error.WriteLine("온라인 계정 저장소를 확인하세요."); return 1; }
    }
}
