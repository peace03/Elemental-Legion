using System.Globalization;

namespace ElementalLegion.Server;

/// <summary>기존 환경 확인과 M0의 실행 인수를 구분해 검증한다.</summary>
public sealed class ServerOptions
{
    public int Bots { get; private set; }
    public bool ReportTick { get; private set; }

    public static ServerOptions Parse(string[] args, int maxPlayers)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new ServerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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
        return options;
    }
}
