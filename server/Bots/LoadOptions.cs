using System.Globalization;
using ElementalLegion.Shared.Net;

namespace ElementalLegion.Bots;

internal sealed record LoadOptions(int Bots, string Host, int Port, Uri? Relay, string? HostServer,
    int Seed, int WarmupSeconds, int DurationSeconds, string Profile, bool Reconnect, string Report, string ServerTrace)
{
    internal const string TraceVariable = "ELEMENTAL_LEGION_LOAD_TRACE";
    internal const string Usage = "--bots N [--host HOST --port PORT --server-trace PATH | --relay HTTPS --host-server PATH] " +
        "[--warmup-seconds 30] [--duration-seconds 300] [--seed 1] [--profile baseline|netem|external] [--reconnect] [--report PATH]";

    internal static LoadOptions Parse(string[] args, int maxPlayers, NetworkSettings settings)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool reconnect = false;
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            if (name == "--reconnect" && !reconnect) { reconnect = true; continue; }
            if (name is not ("--bots" or "--host" or "--port" or "--server-trace" or "--relay" or "--host-server"
                or "--warmup-seconds" or "--duration-seconds" or "--seed" or "--profile" or "--report") ||
                ++index == args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(name, args[index])) throw new ArgumentException($"잘못되었거나 중복된 인수입니다. {name}");
        }

        int count = Integer("--bots", 1, maxPlayers, maxPlayers);
        int port = Integer("--port", 1, ushort.MaxValue, settings.Port);
        string host = values.GetValueOrDefault("--host", "127.0.0.1");
        string profile = values.GetValueOrDefault("--profile", "baseline");
        if (profile is not ("baseline" or "netem" or "external")) throw new ArgumentException("잘못된 측정 프로필입니다.");
        string report = Path.GetFullPath(values.GetValueOrDefault("--report", $".artifacts/m6/{Guid.NewGuid():N}.json"));
        Uri? relay = null;
        string? hostServer = values.GetValueOrDefault("--host-server");
        string? serverTrace = values.GetValueOrDefault("--server-trace") ?? Environment.GetEnvironmentVariable(TraceVariable);
        if (values.TryGetValue("--relay", out string? address))
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out relay) || relay.Scheme != "https" ||
                relay.AbsolutePath != "/" || relay.Query.Length != 0 || relay.Fragment.Length != 0 || relay.UserInfo.Length != 0 ||
                count < 2 || hostServer == null || !File.Exists(hostServer) || values.ContainsKey("--server-trace") ||
                values.ContainsKey("--host") || values.ContainsKey("--port"))
                throw new ArgumentException("인터넷 측정에는 HTTPS 중계 주소와 방장 서버 실행 파일, 봇 2명 이상이 필요합니다.");
            hostServer = Path.GetFullPath(hostServer);
            serverTrace = Path.ChangeExtension(report, ".ticks.ndjson");
        }
        else if (hostServer != null || profile == "external" || string.IsNullOrWhiteSpace(serverTrace))
            throw new ArgumentException("UDP 서버가 기록하는 틱 표본 파일을 --server-trace로 지정하세요.");

        serverTrace = Path.GetFullPath(serverTrace);
        if (string.Equals(serverTrace, report, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("결과와 서버 틱 표본은 서로 다른 파일이어야 합니다.");
        return new LoadOptions(count, host, port, relay, hostServer, Integer("--seed", 0, int.MaxValue, 1),
            Integer("--warmup-seconds", 0, 300, 30), Integer("--duration-seconds", 1, 1800, 300),
            profile, reconnect, report, serverTrace);

        int Integer(string name, int min, int max, int fallback)
        {
            if (!values.TryGetValue(name, out string? value)) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result < min || result > max)
                throw new ArgumentException($"{name}는 {min}~{max} 범위의 정수여야 합니다.");
            return result;
        }
    }
}
