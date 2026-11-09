using System.Globalization;

namespace ElementalLegion.Server;

/// <summary>기존 환경 확인과 M0의 실행 인수를 구분해 검증한다.</summary>
public sealed class ServerOptions
{
    public int Bots { get; private set; }
    public bool ReportTick { get; private set; }
    public bool Listen { get; private set; }
    public bool ReportNetwork { get; private set; }
    public bool ReportGameplay { get; private set; }
    public string Scenario { get; private set; }="normal";
    public bool Online { get; private set; }
    public bool RelayTest { get; private set; }
    public bool HostRoom { get; private set; }
    public string? RevokeAccount { get; private set; }
    private bool createLanRoom;
    public bool LanRoom => createLanRoom || RestoreLanRoom != null;
    public string? RestoreLanRoom { get; private set; }
    public string? RoomTitle { get; private set; }
    public string? SaveDirectory { get; private set; }
    public string? MigrationSource { get; private set; }
    public string? MigrationOutput { get; private set; }
    public string? SourceDataPath { get; private set; }
    public string? TargetDataPath { get; private set; }
    public int? Port { get; private set; }
    public int Minimum { get; private set; } = 2;
    public int Capacity { get; private set; }

    public static ServerOptions Parse(string[] args, int maxPlayers)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new ServerOptions { Capacity = maxPlayers };
        var parameters = new HashSet<string>();
        bool scenarioSpecified=false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--migrate-world":
                    options.MigrationSource = ReadValue(args, ref i, parameters); break;
                case "--migration-output":
                    options.MigrationOutput = ReadValue(args, ref i, parameters); break;
                case "--source-data":
                    options.SourceDataPath = ReadValue(args, ref i, parameters); break;
                case "--target-data":
                    options.TargetDataPath = ReadValue(args, ref i, parameters); break;
                case "--host-room":
                    if (args.Length != 1) throw new ArgumentException("--host-room은 시작 정보를 표준 입력으로만 받습니다.");
                    options.HostRoom = true; break;
                case "--online":
                    if (options.Online) throw new ArgumentException("--online이 중복됐습니다.");
                    options.Online = true; break;
                case "--relay-test":
                    if (options.RelayTest) throw new ArgumentException("--relay-test가 중복됐습니다.");
                    options.RelayTest = true; break;
                case "--lan-room":
                    if (options.createLanRoom) throw new ArgumentException("--lan-room이 중복됐습니다.");
                    options.createLanRoom = true; break;
                case "--resume-lan-room":
                    options.RestoreLanRoom = ReadId(args, ref i, parameters); break;
                case "--revoke-account":
                    options.RevokeAccount = ReadId(args, ref i, parameters); break;
                case "--title":
                    options.RoomTitle = ReadValue(args, ref i, parameters); break;
                case "--save-dir":
                    options.SaveDirectory = ReadValue(args, ref i, parameters); break;
                case "--port":
                    options.Port = ReadInt(args, ref i, parameters, 0, ushort.MaxValue); break;
                case "--minimum":
                    options.Minimum = ReadInt(args, ref i, parameters, 2, maxPlayers); break;
                case "--capacity":
                    options.Capacity = ReadInt(args, ref i, parameters, 2, maxPlayers); break;
                case "--scenario":
                    if (scenarioSpecified || ++i >= args.Length || args[i] is not ("normal" or "m3-demo" or "m4-demo" or "m5-demo") && !ScenarioFactory.TryParseMainWorld(args[i], out _))
                        throw new ArgumentException("--scenario에는 normal, m3-demo, m4-demo, m5-demo 또는 main-world-defense/recovery/dense 중 하나를 한 번 지정하세요.");
                    options.Scenario=args[i];scenarioSpecified=true;break;
                case "--report-gameplay":
                    if(options.ReportGameplay)throw new ArgumentException("--report-gameplay가 중복됐습니다.");
                    options.ReportGameplay=true;break;
                case "--report-network":
                    if (options.ReportNetwork) throw new ArgumentException("--report-network가 중복됐습니다.");
                    options.ReportNetwork = true;
                    break;
                case "--listen":
                    if (options.Listen) throw new ArgumentException("--listen이 중복됐습니다.");
                    options.Listen = true;
                    break;
                case "--bots":
                    if (options.Bots != 0 || ++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out int bots) || bots < 1 || bots > maxPlayers)
                        throw new ArgumentException($"--bots는 1~{maxPlayers} 정수를 한 번 지정해야 합니다.");
                    options.Bots = bots;
                    break;
                case "--report-tick":
                    if (options.ReportTick) throw new ArgumentException("--report-tick이 중복됐습니다.");
                    options.ReportTick = true;
                    break;
                default: throw new ArgumentException("알 수 없는 옵션: " + args[i]);
            }
        }
        if (args.Any(IsMigrationOption))
        {
            if (args.Length != 8 || parameters.Count != 4 || options.MigrationSource == null || options.MigrationOutput == null ||
                options.SourceDataPath == null || options.TargetDataPath == null)
                throw new ArgumentException("월드 이관은 --migrate-world, --migration-output, --source-data, --target-data를 한 번씩만 지정해 별도로 실행하세요.");
            return options;
        }
        if (options.ReportTick && options.Bots == 0) throw new ArgumentException("--report-tick에는 --bots가 필요합니다.");
        if (options.Listen && options.Bots != 0) throw new ArgumentException("--listen과 M0 --bots 모드는 함께 사용할 수 없습니다.");
        if (options.Online && (options.Listen || options.Bots != 0 || options.Port == 0))
            throw new ArgumentException("--online은 별도 실행 모드이며 고정 포트를 사용합니다.");
        if (options.RelayTest && (!options.Online || options.Port.HasValue || options.SaveDirectory == null))
            throw new ArgumentException("--relay-test에는 --online과 별도 --save-dir이 필요합니다. 로컬 포트는 자동으로 배정합니다.");
        if (options.LanRoom && (!options.Listen || options.Scenario != "normal"))
            throw new ArgumentException("LAN 방에는 --listen이 필요합니다.");
        if (options.createLanRoom && (options.RestoreLanRoom != null || options.Minimum > options.Capacity ||
            !ElementalLegion.Shared.Net.M5Protocol.IsSafeText(options.RoomTitle, ElementalLegion.Shared.Net.M5Protocol.TitleLimit)))
            throw new ArgumentException("--lan-room에는 --listen, --title과 유효한 인원이 필요합니다.");
        if (!options.createLanRoom && (parameters.Contains("--title") || parameters.Contains("--minimum") || parameters.Contains("--capacity")))
            throw new ArgumentException("방 제목과 인원은 LAN 대기실에서만 사용합니다.");
        if (options.RestoreLanRoom != null && options.Port.HasValue)
            throw new ArgumentException("LAN 방을 복원할 때는 저장된 포트를 사용합니다.");
        if (options.RevokeAccount != null && (options.Listen || options.Online || options.Bots != 0 || options.Port.HasValue))
            throw new ArgumentException("--revoke-account는 온라인 저장 경로만 지정해 별도로 실행하세요.");
        if ((options.Port.HasValue || options.SaveDirectory != null) && !options.Listen && !options.Online && options.RevokeAccount == null)
            throw new ArgumentException("포트와 저장 경로는 서버 모드에서만 사용합니다.");
        if (options.ReportNetwork && !options.Listen) throw new ArgumentException("--report-network에는 --listen이 필요합니다.");
        bool benchmarkScenario = ScenarioFactory.TryParseMainWorld(options.Scenario, out _);
        if (benchmarkScenario && (!options.ReportTick || options.Bots == 0 || options.Listen || options.Online))
            throw new ArgumentException("main-world 시나리오에는 --bots와 --report-tick이 필요합니다.");
        if ((options.ReportGameplay || scenarioSpecified && !benchmarkScenario) && !options.Listen) throw new ArgumentException("시나리오·게임 집계에는 --listen이 필요합니다.");
        return options;
    }

    private static string ReadId(string[] args, ref int index, HashSet<string> parameters)
    {
        string value = ReadValue(args, ref index, parameters);
        if (!Guid.TryParseExact(value, "N", out Guid id) || id == Guid.Empty)
            throw new ArgumentException("하이픈 없는 ID가 필요합니다.");
        return id.ToString("N");
    }

    internal static bool IsMigrationOption(string option) => option is "--migrate-world" or "--migration-output" or "--source-data" or "--target-data";

    private static string ReadValue(string[] args, ref int index, HashSet<string> parameters)
    {
        string option = args[index];
        if (!parameters.Add(option) || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException(option + "에는 값이 한 번 필요합니다.");
        return args[index];
    }

    private static int ReadInt(string[] args, ref int index, HashSet<string> parameters, int minimum, int maximum)
    {
        string value = ReadValue(args, ref index, parameters);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < minimum || number > maximum)
            throw new ArgumentException("서버 숫자 옵션의 범위가 잘못됐습니다.");
        return number;
    }
}
