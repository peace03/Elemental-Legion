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

    public static ServerOptions Parse(string[] args, int maxPlayers)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new ServerOptions();
        bool scenarioSpecified=false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenario":
                    if(scenarioSpecified||++i>=args.Length||args[i]!="normal"&&args[i]!="m3-demo")throw new ArgumentException("--scenario에는 normal 또는 m3-demo를 한 번 지정하세요.");
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
        if (options.ReportTick && options.Bots == 0) throw new ArgumentException("--report-tick에는 --bots가 필요합니다.");
        if (options.Listen && options.Bots != 0) throw new ArgumentException("--listen과 M0 --bots 모드는 함께 사용할 수 없습니다.");
        if (options.ReportNetwork && !options.Listen) throw new ArgumentException("--report-network에는 --listen이 필요합니다.");
        if((options.ReportGameplay||scenarioSpecified)&&!options.Listen)throw new ArgumentException("시나리오·게임 집계에는 --listen이 필요합니다.");
        return options;
    }
}
