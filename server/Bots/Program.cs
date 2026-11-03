using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Bots;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine(LoadOptions.Usage);
            return 0;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        try
        {
            var data = new GameDataDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
            var rules = new GameRules(data);
            var settings = new NetworkSettings(data);
            LoadOptions options = LoadOptions.Parse(args, rules.MaxPlayers, settings);
            Directory.CreateDirectory(Path.GetDirectoryName(options.Report)!);
            await using var report = new FileStream(options.Report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            return await Run(options, rules, settings, report, stop.Token);
        }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); Console.Error.WriteLine(LoadOptions.Usage); return 2; }
        catch (Exception error) when (ExpectedFailure(error)) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static async Task<int> Run(LoadOptions options, GameRules rules, NetworkSettings settings, Stream report, CancellationToken stop)
    {
        var random = new Random(options.Seed);
        var peers = Enumerable.Range(0, options.Bots).Select(index => new LoadPeer(rules, settings, index, random)).ToArray();
        using var room = options.Relay == null ? null : new HostedLoadRoom(options, rules);
        string transport = room == null ? "udp" : "wss";
        string[]? accounts = room == null ? null : new string[peers.Length];
        var errors = new List<string>();
        var clock = Stopwatch.StartNew();
        long startTick = -1, endTick = -1;
        double duration = 0;
        LoadTickSummary? ticks = null;
        double? resumeMs = null;
        var bytes = new (long Sent, long Received)[peers.Length];
        var poseTicks = new (long First, long Last)[peers.Length];
        var connectedIds = new int[peers.Length];
        long traceBytesAtStart = 0;
        try
        {
            using var join = CancellationTokenSource.CreateLinkedTokenSource(stop);
            join.CancelAfter(TimeSpan.FromSeconds(60));
            if (room == null)
            {
                foreach (LoadPeer peer in peers) ConnectUdp(peer, resume: false);
            }
            else
            {
                for (int index = 0; index < peers.Length; index++) accounts![index] = await room.CreateAccount(peers[index].Name, join.Token);
                await room.StartHost(accounts![0], join.Token);
                await room.Connect(peers[0], accounts[0], join.Token);
                await Wait(() => peers[0].Connection.Lobby.HasValue, join.Token, () => CheckPeers(peers.Take(1)));
                await WaitForListing(1, join.Token);
                for (int index = 1; index < peers.Length; index++) await room.Connect(peers[index], accounts[index], join.Token);
                await Wait(() => peers.All(peer => peer.Connection.Lobby.HasValue) &&
                    peers[0].Connection.Lobby!.Value.Members.Length == peers.Length, join.Token, () => CheckPeers(peers));
                foreach (LoadPeer peer in peers) peer.Connection.SetLobbyReady(peer.Element, true);
                await Wait(() => peers[0].Connection.Lobby?.Members.All(member => member.Ready) == true, join.Token, () => CheckLobby(peers));
                peers[0].Connection.StartRoom();
            }
            await Wait(() => peers.All(peer => peer.Ready), join.Token, () => CheckLobby(peers));
            for (int index = 0; index < peers.Length; index++) connectedIds[index] = peers[index].Connection.PlayerId;
            if (connectedIds.Distinct().Count() != peers.Length) throw new InvalidDataException("클라이언트별 플레이어 ID가 다르지 않습니다.");
            Console.WriteLine($"{transport} {peers.Length}명 입장 완료. 워밍업 {options.WarmupSeconds}초, 측정 {options.DurationSeconds}초.");
            for (int index = 0; index < peers.Length; index++)
                peers[index].NextMove = clock.Elapsed.TotalSeconds + rules.DeltaTime * index / peers.Length;
            await Play(options.WarmupSeconds, measure: false);

            startTick = peers.Max(peer => peer.Pose.Tick);
            traceBytesAtStart = LoadTickSummary.TraceLength(options.ServerTrace);
            for (int index = 0; index < peers.Length; index++)
            {
                bytes[index] = (peers[index].Connection.SentPayloadBytes, peers[index].Connection.ReceivedPayloadBytes);
                poseTicks[index].First = peers[index].Pose.Tick;
            }
            double measurementStarted = clock.Elapsed.TotalSeconds;
            try { await Play(options.DurationSeconds, measure: true); }
            finally
            {
                duration = clock.Elapsed.TotalSeconds - measurementStarted;
                endTick = peers.Max(peer => peer.Pose.Tick);
                for (int index = 0; index < peers.Length; index++)
                {
                    bytes[index] = (peers[index].Connection.SentPayloadBytes - bytes[index].Sent,
                        peers[index].Connection.ReceivedPayloadBytes - bytes[index].Received);
                    poseTicks[index].Last = peers[index].Pose.Tick;
                }
            }
            // UDP 채널 순서와 재전송으로 늦게 도착한 마지막 확인을 수집한다.
            await Wait(() => peers.All(peer => peer.Moves.All(move => move.Response.HasValue &&
                (move.Response != CommandAdmission.Accepted || move.Applied != 0))), stop, () => CheckPeers(peers), timeoutSeconds: 2, timeoutIsFailure: false);
            foreach (LoadPeer peer in peers) peer.EndMeasurement();

            if (options.Reconnect) resumeMs = await VerifyReconnect();
        }
        catch (Exception error) when (ExpectedFailure(error)) { errors.Add(error is OperationCanceledException ? "실행이 취소되었거나 준비 시간이 초과됐습니다." : error.Message); }
        finally { foreach (LoadPeer peer in peers) peer.Dispose(); }

        if (startTick >= 0 && endTick > startTick)
        {
            try { ticks = await LoadTickSummary.Read(options.ServerTrace, transport, Protocol.Version, rules.DataHash, rules.TickRate, startTick, endTick, traceBytesAtStart, stop); }
            catch (Exception error) when (ExpectedFailure(error)) { errors.Add(error.Message); }
        }

        double? p95 = LoadTickSummary.Percentile(LoadTickSummary.AppliedLatencies(peers.SelectMany(peer => peer.Moves)), .95);
        if (ticks == null || ticks.MinConnected != peers.Length || ticks.MaxConnected != peers.Length)
            errors.Add("측정 구간의 서버 접속 인원을 확인하지 못했거나 요청한 인원과 다릅니다.");
        int minimumTicks = Math.Max(1, (options.DurationSeconds - 1) * rules.TickRate);
        if (poseTicks.Any(range => range.Last - range.First < minimumTicks))
            errors.Add("하나 이상의 클라이언트에서 측정 시간만큼 위치 틱이 진행되지 않았습니다.");
        if (ticks?.P99Ms > 1000d / rules.TickRate) errors.Add("서버 틱 p99가 틱 예산을 초과했습니다.");
        if (p95 == null || options.Profile != "external" && p95 > 250) errors.Add("이동 적용 표본이 없거나 p95 기준을 초과했습니다.");
        if (options.Bots == rules.MaxPlayers && options.DurationSeconds >= 300 && ticks?.Attacks == 0)
            errors.Add("측정 구간에 교전이 없었습니다.");
        var clients = peers.Select((peer, index) =>
        {
            var latency = LoadTickSummary.AppliedLatencies(peer.Moves);
            return new
            {
                peer.Name, PlayerId = connectedIds[index], FirstPoseTick = poseTicks[index].First,
                LastPoseTick = poseTicks[index].Last, Moves = peer.Moves.Count, Applied = latency.Count,
                P95Ms = LoadTickSummary.Percentile(latency, .95),
                NoResponse = peer.Moves.Count(move => !move.Response.HasValue),
                AcceptedWithoutPose = peer.Moves.Count(move => move.Response == CommandAdmission.Accepted && move.Applied == 0),
                Rejected = peer.Moves.Where(move => move.Response.HasValue && move.Response != CommandAdmission.Accepted)
                    .GroupBy(move => move.Response!.Value.ToString()).ToDictionary(group => group.Key, group => group.Count()),
                PayloadSentKiBps = bytes[index].Sent / 1024d / Math.Max(duration, 1e-9),
                PayloadReceivedKiBps = bytes[index].Received / 1024d / Math.Max(duration, 1e-9)
            };
        }).ToArray();
        if (clients.Any(client => client.Applied == 0)) errors.Add("이동 적용을 확인하지 못한 봇이 있습니다.");

        await JsonSerializer.SerializeAsync(report, new
        {
            StartedUtc = DateTimeOffset.UtcNow - clock.Elapsed,
            Transport = transport, Protocol.Version, rules.DataHash, rules.TickRate, options.Bots, options.Seed, options.Profile,
            DelayPerDirectionMs = options.Profile == "netem" ? 40 : (int?)null,
            JitterPerDirectionMs = options.Profile == "netem" ? 10 : (int?)null,
            LossPerDirectionPercent = options.Profile == "netem" ? 1 : (int?)null,
            options.WarmupSeconds, options.DurationSeconds, MeasuredSeconds = duration, StartTick = startTick, EndTick = endTick,
            TickBudgetMs = 1000d / rules.TickRate, Ticks = ticks, MoveAppliedP95Ms = p95, ResumeMs = resumeMs,
            Passed = errors.Count == 0, Errors = errors, Clients = clients
        }, new JsonSerializerOptions { WriteIndented = true });
        foreach (string error in errors) Console.Error.WriteLine(error);
        Console.WriteLine($"{(errors.Count == 0 ? "PASS" : "FAIL")} {options.Report}");
        return errors.Count == 0 ? 0 : 3;

        void ConnectUdp(LoadPeer peer, bool resume) => peer.Connection.Connect(options.Host, options.Port,
            peer.Element, peer.Name, resumeKey: resume ? peer.ResumeKey : null, requestAccount: true);

        async Task Play(double seconds, bool measure)
        {
            double end = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < end)
            {
                stop.ThrowIfCancellationRequested();
                foreach (LoadPeer peer in peers) peer.Poll();
                CheckPeers(peers);
                double now = clock.Elapsed.TotalSeconds;
                if (now >= end) break;
                SendMoves(now, measure);
                await Task.Delay(2, stop);
            }
        }

        void SendMoves(double now, bool measure)
        {
            foreach (LoadPeer peer in peers)
            {
                if (!peer.Ready || now < peer.NextMove) continue;
                peer.Move(measure);
                peer.NextMove += rules.DeltaTime;
                if (peer.NextMove < now) peer.NextMove = now + rules.DeltaTime;
            }
        }

        async Task Wait(Func<bool> done, CancellationToken cancellation, Action check,
            double timeoutSeconds = 15, bool timeoutIsFailure = true)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < timeoutSeconds)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (LoadPeer peer in peers) peer.Poll();
                check();
                if (done()) return;
                await Task.Delay(2, cancellation);
            }
            if (timeoutIsFailure) throw new IOException("클라이언트 상태 갱신 시간이 초과됐습니다.");
        }

        async Task WaitForListing(int count, CancellationToken cancellation, Action? check = null)
        {
            while (await room!.ListedPlayers(cancellation) != count)
            {
                foreach (LoadPeer peer in peers) peer.Poll();
                check?.Invoke();
                await Task.Delay(250, cancellation);
            }
        }

        async Task<double> VerifyReconnect()
        {
            LoadPeer peer = peers[^1];
            int owner = peer.Connection.PlayerId;
            var others = peers.Take(peers.Length - 1).ToArray();
            long[] previous = others.Select(other => other.Pose.Tick).ToArray();
            if (room == null && peer.ResumeKey == null) throw new IOException("UDP 서버가 복귀 키를 발급하지 않았습니다.");
            var timer = Stopwatch.StartNew();
            using var resume = CancellationTokenSource.CreateLinkedTokenSource(stop);
            resume.CancelAfter(TimeSpan.FromSeconds(30));
            peer.Connection.Disconnect();
            peer.RestartMovement();
            if (room == null)
            {
                await Wait(() => timer.ElapsedMilliseconds >= 500, resume.Token, ContinuePlaying);
            }
            else
            {
                await WaitForListing(peers.Length - 1, resume.Token, ContinuePlaying);
            }
            await Wait(() => others.Select((other, index) => other.Pose.Tick > previous[index]).All(advanced => advanced),
                resume.Token, ContinuePlaying);
            if (room == null) ConnectUdp(peer, resume: true);
            else await room.Connect(peer, accounts![^1], resume.Token);
            await Wait(() => peer.Ready, resume.Token, () => { CheckPeers(peers); SendMoves(clock.Elapsed.TotalSeconds, false); }, timeoutSeconds: 30);
            if (!peer.Connection.Resumed || peer.Connection.PlayerId != owner) throw new IOException("기존 플레이어 ID로 복귀하지 못했습니다.");
            await Wait(() => peer.Pose.AppliedSequence > 0,
                resume.Token, () => { CheckPeers(peers); SendMoves(clock.Elapsed.TotalSeconds, false); }, timeoutSeconds: 30);
            return timer.Elapsed.TotalMilliseconds;

            void ContinuePlaying()
            {
                CheckPeers(others);
                SendMoves(clock.Elapsed.TotalSeconds, false);
            }
        }
    }

    private static void CheckPeers(IEnumerable<LoadPeer> peers)
    {
        foreach (LoadPeer peer in peers)
        {
            var connection = peer.Connection;
            if (connection.State != ConnectionState.Disconnected) continue;
            string reason = connection.Defeated ? "전투 탈락" : connection.Victorious ? "승리 종료" :
                connection.JoinFailure != JoinFailure.None ? connection.JoinFailure.ToString() : connection.Error;
            throw new IOException($"{peer.Name} 연결 종료. {reason}");
        }
    }

    private static void CheckLobby(IEnumerable<LoadPeer> peers)
    {
        CheckPeers(peers);
        foreach (LoadPeer peer in peers)
            if (peer.Connection.LastLobbyError is { } error) throw new IOException($"{peer.Name} 대기실 요청 거절. {error}");
    }

    private static bool ExpectedFailure(Exception error) => error is IOException or InvalidDataException or OperationCanceledException or HttpRequestException
        or InvalidOperationException or ArgumentException or JsonException or SocketException or FormatException or KeyNotFoundException
        or UnauthorizedAccessException or Win32Exception;
}
