using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Bots;

/// <summary>실제 클라이언트와 자식 서버를 사용해 반복 승계와 남은 참가자의 연속성을 측정한다.</summary>
internal static class MigrationLoadRunner
{
    private sealed class EpochObservation
    {
        internal long LastPoseAt, LastTick = -1, ReadyAt, FreezeAt, SnapshotTick = -1;
        internal int BackupAgeMs = -1;
    }

    private sealed class Observation
    {
        internal readonly Dictionary<ulong, EpochObservation> Epochs = new();
        internal ulong OfferedGeneration;
        internal EpochObservation At(ulong generation)
        {
            if (!Epochs.TryGetValue(generation, out var value)) Epochs.Add(generation, value = new());
            return value;
        }
    }

    private sealed record OwnedProcess(int Id, long StartedUtcTicks);
    private sealed record CycleClient(string Name, int PlayerId, double GapMs, double? FreezeToReadyMs, long RolledBackTicks, double? BackupAgeMs);
    private sealed record Cycle(int Number, ulong FromGeneration, ulong ToGeneration, int PreviousHost,
        int RemainingPlayers, double RequestToReadyMs, double MaximumGapMs, double? MaximumFreezeMs,
        long MaximumRollbackTicks, double? BackupAgeMs, CycleClient[] Clients);

    internal static async Task<int> Run(LoadOptions options, GameRules rules, NetworkSettings settings, Stream report, CancellationToken stop)
    {
        var clock = Stopwatch.StartNew();
        var peers = Enumerable.Range(0, options.Bots).Select(index => new LoadPeer(rules, settings, index, new Random(options.Seed + index))).ToArray();
        var observations = peers.Select(_ => new Observation()).ToArray();
        string prefix = Path.Combine(Path.GetDirectoryName(options.Report)!, Path.GetFileNameWithoutExtension(options.Report) + ".migration");
        using var room = new HostedLoadRoom(options, rules, prefix + ".bot01.1.ticks.ndjson");
        var companions = new HostMigrationClient[peers.Length];
        var accounts = new string[peers.Length];
        var joined = new bool[peers.Length];
        var identities = new int[peers.Length];
        var owned = new Dictionary<(int Peer, ulong Generation), OwnedProcess>();
        var activeTraces = new Dictionary<ulong, string>();
        var launchedTraces = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var cycles = new List<Cycle>();
        var errors = new List<string>();
        var expectedDisconnect = new HashSet<int>();
        var unexpectedDisconnect = new HashSet<string>(StringComparer.Ordinal);
        double measuredSeconds = 0;
        Stopwatch? measurement = null;
        long firstMeasuredTick = -1;
        ulong firstMeasuredGeneration = 1;
        MigrationTraceSummary? traces = null;
        double? traceP99 = null;
        try
        {
            string traceName = Path.GetFileName(prefix) + ".bot";
            if (Directory.EnumerateFiles(Path.GetDirectoryName(prefix)!).Any(path =>
                Path.GetFileName(path).StartsWith(traceName, StringComparison.Ordinal)))
                throw new IOException("동일한 실행 이름의 서버 틱 표본이 이미 있습니다.");
            for (int index = 0; index < peers.Length; index++)
            {
                int captured = index;
                peers[index].Connection.PoseReceived += pose =>
                {
                    var observation = observations[captured].At(peers[captured].Connection.HostGeneration);
                    observation.LastPoseAt = Stopwatch.GetTimestamp();
                    observation.LastTick = pose.Tick;
                };
                peers[index].Connection.HostStandbyRequested += offer =>
                {
                    observations[captured].OfferedGeneration = offer.Generation;
                    launchedTraces.Add(TracePath(captured, offer.Generation));
                };
                peers[index].Connection.MigrationChanged += state =>
                {
                    long now = Stopwatch.GetTimestamp();
                    if (state.Phase == MigrationPhase.Recovering)
                        observations[captured].At(peers[captured].Connection.HostGeneration).FreezeAt = now;
                    if (state.Phase == MigrationPhase.Resyncing)
                    {
                        var epoch = observations[captured].At(state.Generation);
                        epoch.SnapshotTick = state.Tick;
                        epoch.BackupAgeMs = state.BackupAgeMs;
                    }
                    if (state.Phase == MigrationPhase.None && state.Reason == "ready") observations[captured].At(state.Generation).ReadyAt = now;
                    if (state.IsHost && state.Phase is MigrationPhase.Resyncing or MigrationPhase.None)
                        activeTraces[state.Generation] = TracePath(captured, state.Generation);
                };
                companions[index] = new HostMigrationClient(peers[index].Connection, options.Relay!, options.HostServer!,
                    settings.MigrationCandidateSeconds, prefix + $".bot{index + 1:00}");
                accounts[index] = await room.CreateAccount(peers[index].Name, stop);
            }
            launchedTraces.Add(TracePath(0, 1));
            await room.StartHost(accounts[0], stop);
            activeTraces[1] = TracePath(0, 1);
            owned.Add((0, 1), Identify(room.HostProcessId));
            await room.Connect(peers[0], accounts[0], stop); joined[0] = true;
            await Wait(() => peers[0].Connection.Lobby.HasValue, 20);
            await WaitForListing(1);
            for (int index = 1; index < peers.Length; index++)
            {
                await room.Connect(peers[index], accounts[index], stop);
                joined[index] = true;
            }
            await Wait(() => peers.All(peer => peer.Connection.Lobby.HasValue) && peers[0].Connection.Lobby!.Value.Members.Length == peers.Length, 30);
            foreach (LoadPeer peer in peers) peer.Connection.SetLobbyReady(peer.Element, true);
            await Wait(() => peers[0].Connection.Lobby?.Members.All(member => member.Ready) == true, 20);
            peers[0].Connection.StartRoom();
            await Wait(() => peers.All(peer => peer.Ready), 30);
            for (int index = 0; index < peers.Length; index++) identities[index] = peers[index].Connection.PlayerId;
            if (identities.Distinct().Count() != peers.Length) throw new InvalidDataException("참가자의 플레이어 ID가 중복됐습니다.");
            Console.WriteLine($"승계 측정 {peers.Length}명 입장 완료. {options.MigrationMode} {options.MigrationCycles}회.");
            await Play(options.WarmupSeconds);
            firstMeasuredTick = peers.Max(peer => peer.Pose.Tick);
            firstMeasuredGeneration = peers[0].Connection.HostGeneration;
            measurement = Stopwatch.StartNew();
            for (int cycle = 0; cycle < options.MigrationCycles; cycle++)
            {
                await Play(Math.Max(1, options.DurationSeconds / (double)options.MigrationCycles));
                int host = CurrentHost();
                ulong generation = peers[host].Connection.HostGeneration;
                await Wait(() => Enumerable.Range(0, peers.Length).Any(index => index != host &&
                    observations[index].OfferedGeneration > generation && companions[index].ServerProcessId > 0 &&
                    owned.ContainsKey((index, observations[index].OfferedGeneration))), 15);
                long requested = Stopwatch.GetTimestamp();
                bool graceful = options.MigrationMode == "graceful";
                if (graceful)
                {
                    expectedDisconnect.Add(host);
                    peers[host].Connection.RequestHostLeave();
                }
                else
                {
                    if (!owned.TryGetValue((host, generation), out OwnedProcess? process))
                        throw new IOException("현재 호스트의 소유한 자식 프로세스를 확인하지 못했습니다.");
                    using var current = Process.GetProcessById(process.Id);
                    if (current.StartTime.ToUniversalTime().Ticks != process.StartedUtcTicks)
                        throw new IOException("호스트 프로세스 ID가 다른 프로세스로 바뀌었습니다.");
                    current.Kill();
                }
                int[] remaining = Enumerable.Range(0, peers.Length).Where(index => !graceful || index != host).ToArray();
                await Wait(() => remaining.All(index => peers[index].Ready && peers[index].Connection.HostGeneration > generation &&
                    observations[index].At(peers[index].Connection.HostGeneration).ReadyAt > 0), settings.MigrationDeadlineSeconds + 5);
                ulong next = peers[remaining[0]].Connection.HostGeneration;
                if (remaining.Any(index => peers[index].Connection.HostGeneration != next))
                    throw new InvalidDataException("참가자가 같은 서버 세대로 복구되지 않았습니다.");
                var clients = remaining.Select(index =>
                {
                    EpochObservation old = observations[index].At(generation), restored = observations[index].At(next);
                    return new CycleClient(peers[index].Name, peers[index].Connection.PlayerId,
                        Stopwatch.GetElapsedTime(old.LastPoseAt, restored.ReadyAt).TotalMilliseconds,
                        old.FreezeAt == 0 ? null : Stopwatch.GetElapsedTime(old.FreezeAt, restored.ReadyAt).TotalMilliseconds,
                        Math.Max(0, old.LastTick - restored.SnapshotTick), restored.BackupAgeMs < 0 ? null : restored.BackupAgeMs);
                }).ToArray();
                double? maximumFreeze = clients.Where(client => client.FreezeToReadyMs.HasValue)
                    .Select(client => client.FreezeToReadyMs).DefaultIfEmpty().Max();
                cycles.Add(new Cycle(cycle + 1, generation, next, identities[host], remaining.Length,
                    Stopwatch.GetElapsedTime(requested).TotalMilliseconds, clients.Max(client => client.GapMs), maximumFreeze,
                    clients.Max(client => client.RolledBackTicks), clients.Max(client => client.BackupAgeMs), clients));
                Console.WriteLine($"승계 {cycle + 1}/{options.MigrationCycles}: 세대 {generation} → {next}, 남은 {remaining.Length}명, 최대 위치 공백 {clients.Max(client => client.GapMs):F1}ms");
                ulong[] before = peers.Select(peer => peer.Pose.AppliedSequence).ToArray();
                await Wait(() => remaining.All(index => peers[index].Pose.AppliedSequence > before[index]), 3);
                if (graceful) await Reconnect(host);
                await WaitForListing(peers.Length);
                _ = CurrentHost();
            }
            if (options.Reconnect) await Reconnect(Enumerable.Range(0, peers.Length).First(index => !peers[index].Connection.IsHostServer));
        }
        catch (Exception error) when (Expected(error))
        {
            errors.Add(error is OperationCanceledException ? "승계 측정이 취소되었거나 제한 시간을 넘었습니다." : error.Message);
        }
        finally
        {
            measuredSeconds = measurement?.Elapsed.TotalSeconds ?? 0;
            foreach (LoadPeer peer in peers) peer.Dispose();
            // 마지막 참가자 종료에 따른 서버의 표본 쓰기 정리를 기다린다.
            await Task.Delay(500, CancellationToken.None);
            foreach (HostMigrationClient? companion in companions) companion?.Dispose();
        }
        try
        {
            var summary = MigrationTraceSummary.Read(prefix, rules, firstMeasuredTick, observations.SelectMany(observation =>
                observation.Epochs.Select(item => (Generation: item.Key, Tick: item.Value.LastTick))), activeTraces, launchedTraces, firstMeasuredGeneration);
            traces = summary;
            traceP99 = summary.P99Ms;
            if (summary.Samples == 0) errors.Add("승계 측정의 서버 틱 표본이 없습니다.");
            else if (summary.P99Ms > 1000d / rules.TickRate) errors.Add("백업을 포함한 서버 틱 p99가 틱 예산을 초과했습니다.");
        }
        catch (Exception error) when (Expected(error)) { errors.Add(error.Message); }
        double? gapP95 = Percentile(cycles.Select(cycle => cycle.MaximumGapMs));
        double? freezeP95 = Percentile(cycles.Where(cycle => cycle.MaximumFreezeMs.HasValue).Select(cycle => cycle.MaximumFreezeMs!.Value));
        ulong[] unrequestedGenerations = activeTraces.Keys.Where(generation => generation != 1 &&
            cycles.All(cycle => cycle.ToGeneration != generation)).Order().ToArray();
        if (unrequestedGenerations.Length > 0) errors.Add("요청한 장애 주입 이외에 서버 세대가 변경됐습니다.");
        if (cycles.Count != options.MigrationCycles) errors.Add("요청한 승계 횟수를 완료하지 못했습니다.");
        if (options.Profile != "external" && options.MigrationMode == "crash" && gapP95 > 5000) errors.Add("강제 장애 복구 p95가 5초를 넘었습니다.");
        if (options.Profile != "external" && options.MigrationMode == "graceful" && (freezeP95 == null || freezeP95 > 500))
            errors.Add("정상 인계의 전환 공백 p95를 확인하지 못했거나 500ms를 넘었습니다.");
        await JsonSerializer.SerializeAsync(report, new
        {
            StartedUtc = DateTimeOffset.UtcNow - clock.Elapsed, Protocol.Version, rules.DataHash, rules.TickRate,
            options.Bots, options.Seed, options.Profile, options.MigrationCycles, options.MigrationMode,
            options.WarmupSeconds, options.DurationSeconds, MeasuredSeconds = measuredSeconds,
            DurationBasis = "duration divided between cycles; transitions and reconnects add elapsed time",
            NetworkConditionVerification = "profile is operator supplied; runner does not inject or verify netem",
            DelayPerDirectionMs = options.Profile == "netem" ? 40 : (int?)null,
            JitterPerDirectionMs = options.Profile == "netem" ? 10 : (int?)null,
            LossPerDirectionPercent = options.Profile == "netem" ? 1 : (int?)null,
            CompletedCycles = cycles.Count, LastPoseToBaselineP95Ms = gapP95, FreezeToBaselineP95Ms = freezeP95,
            UnrequestedGenerations = unrequestedGenerations,
            FreezeToBaselineBasis = "client Recovering notice receipt to complete baseline",
            BackupAgeBasis = "relayValidatedBackupToResyncNotice",
            BackupPayloadBasis = "compressed snapshot bytes acknowledged by relay; transport framing excluded",
            BackupPayloadBytesPerSecond = measuredSeconds > 0 ? traces?.Backups.CompressedBytes / measuredSeconds : null,
            ServerTickP99Ms = traceP99, Traces = traces, UnexpectedDisconnects = unexpectedDisconnect.ToArray(), Cycles = cycles,
            Clients = peers.Select((peer, index) => new { peer.Name, PlayerId = identities[index],
                peer.Connection.SentPayloadBytes, peer.Connection.ReceivedPayloadBytes }),
            Passed = errors.Count == 0, Errors = errors
        }, new JsonSerializerOptions { WriteIndented = true }, CancellationToken.None);
        foreach (string error in errors) Console.Error.WriteLine(error);
        Console.WriteLine($"{(errors.Count == 0 ? "PASS" : "FAIL")} {options.Report}");
        return errors.Count == 0 ? 0 : 3;

        void Poll()
        {
            for (int index = 0; index < peers.Length; index++)
            {
                LoadPeer peer = peers[index];
                peer.Poll(); companions[index]?.Poll();
                int process = companions[index]?.ServerProcessId ?? -1;
                ulong offered = observations[index].OfferedGeneration;
                if (process > 0 && offered > 1 && !owned.ContainsKey((index, offered))) owned.Add((index, offered), Identify(process));
                if (joined[index] && !expectedDisconnect.Contains(index) && peer.Connection.State == ConnectionState.Disconnected)
                {
                    unexpectedDisconnect.Add(peer.Name + ": " + peer.Connection.Error);
                    throw new IOException(peer.Name + "가 예기치 않게 연결 종료됐습니다. " + peer.Connection.Error);
                }
                if (identities[index] > 0 && peer.Connection.State == ConnectionState.Connected && peer.Connection.PlayerId != identities[index])
                    throw new InvalidDataException(peer.Name + "의 플레이어 ID가 바뀌었습니다.");
                if (!peer.Ready || clock.Elapsed.TotalSeconds < peer.NextMove) continue;
                peer.Move(measure: false);
                peer.NextMove = clock.Elapsed.TotalSeconds + rules.DeltaTime;
            }
        }

        async Task Wait(Func<bool> done, double seconds)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < seconds)
            {
                stop.ThrowIfCancellationRequested();
                Poll();
                if (done()) return;
                await Task.Delay(2, stop);
            }
            throw new IOException("승계 클라이언트의 상태 갱신 시간이 초과됐습니다.");
        }

        async Task Play(double seconds)
        {
            double end = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < end)
            {
                stop.ThrowIfCancellationRequested(); Poll(); await Task.Delay(2, stop);
            }
        }

        async Task WaitForListing(int expected)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 20)
            {
                if (await room.ListedPlayers(stop) == expected) return;
                await Play(.1);
            }
            throw new IOException("방 목록의 접속 인원이 갱신되지 않았습니다.");
        }

        async Task Reconnect(int index)
        {
            expectedDisconnect.Add(index);
            peers[index].Connection.Disconnect(); peers[index].RestartMovement();
            await WaitForListing(peers.Length - 1);
            await room.Connect(peers[index], accounts[index], stop);
            await Wait(() => peers[index].Ready, 20);
            if (!peers[index].Connection.Resumed || peers[index].Connection.PlayerId != identities[index])
                throw new IOException("나간 방장이 기존 플레이어 ID로 복귀하지 못했습니다.");
            expectedDisconnect.Remove(index);
        }

        int CurrentHost()
        {
            int[] hosts = Enumerable.Range(0, peers.Length).Where(index => peers[index].Ready && peers[index].Connection.IsHostServer).ToArray();
            if (hosts.Length != 1) throw new IOException("판정 권한을 가진 호스트가 정확히 한 명이 아닙니다.");
            return hosts[0];
        }

        string TracePath(int index, ulong generation) => prefix + $".bot{index + 1:00}.{generation}.ticks.ndjson";
    }

    private static OwnedProcess Identify(int id)
    {
        if (id <= 0) throw new IOException("소유한 서버 프로세스를 확인하지 못했습니다.");
        using var process = Process.GetProcessById(id);
        return new OwnedProcess(id, process.StartTime.ToUniversalTime().Ticks);
    }

    private static double? Percentile(IEnumerable<double> values) => LoadTickSummary.Percentile(values.ToList(), .95);
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or OperationCanceledException or HttpRequestException or
        InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException or Win32Exception or
        KeyNotFoundException or FormatException;
}
