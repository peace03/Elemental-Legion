using System.Net.WebSockets;
using System.Security.Cryptography;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;

namespace ElementalLegion.Server;

/// <summary>중계가 단 하나의 호스트 세대와 승계 후보를 소유한다.</summary>
internal sealed partial class HostedRelay
{
    private async Task PrepareCandidate(Room room, bool duringMigration = false)
    {
        Candidate? expired = null;
        Candidate? candidate = null;
        string? key = null;
        lock (gate)
        {
            if (room.Ended.IsCancellationRequested || room.Migrating && !duringMigration || room.Players.Count < 2 || !room.CreatorPaired) return;
            if (room.Standby is { } previous &&
                (previous.Slot.Finished.Task.IsCompleted || previous.Session?.Stop.IsCancellationRequested == true ||
                 previous.Session == null && previous.Deadline <= clock.GetUtcNow()))
            {
                expired = previous;
                previous.Slot.Capable = false;
                room.Standby = null;
            }
            if (room.Standby != null) return;
            Slot? slot = room.Players.Values.Where(value => value.Capable && !value.Leaving &&
                !value.Finished.Task.IsCompleted && !value.Revoked.IsCancellationRequested && value.Account.Id != room.Creator.Id)
                .OrderBy(value => value.Order).FirstOrDefault();
            if (slot != null)
            {
                key = NewKey(out byte[] proof);
                room.Standby = candidate = new Candidate(slot, proof,
                    clock.GetUtcNow().AddSeconds(settings.MigrationCandidateSeconds), checked(room.Generation + 1));
            }
        }
        expired?.Session?.Stop.Cancel();
        if (candidate == null) return;
        var writer = new NetDataWriter();
        HostMigrationProtocol.WriteStandby(writer, room.Info.Id, key!, candidate.Generation);
        try { await SendPlayer(candidate.Slot, room.Generation, writer.CopyData(), room.Ended.Token); }
        catch (Exception error) when (TransportFailure(error))
        {
            lock (gate)
            {
                candidate.Slot.Capable = false;
                if (room.Standby == candidate) room.Standby = null;
            }
        }
    }

    private Task BeginMigration(Room room, bool graceful, string? leavingAccount)
    {
        lock (gate)
        {
            if (room.Ended.IsCancellationRequested) return Task.CompletedTask;
            if (room.MigrationTask is { IsCompleted: false }) return room.MigrationTask;
            room.Graceful = graceful;
            room.MigrationStarted = clock.GetUtcNow();
            if (!graceful)
            {
                room.Migrating = true;
                if (room.Active != null) { room.Active.Retiring = true; room.Active.Stop.Cancel(); }
            }
            room.MigrationTask = Task.Run(() => Migrate(room, graceful, leavingAccount));
            return room.MigrationTask;
        }
    }

    private async Task Migrate(Room room, bool graceful, string? leavingAccount)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(room.Ended.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.MigrationDeadlineSeconds));
        HostSession? previous;
        lock (gate) previous = room.Active;
        try
        {
            if (!graceful) await Notify(room, MigrationPhase.Recovering, checked(room.Generation + 1), "", deadline.Token);
            var attempted = new HashSet<string>(StringComparer.Ordinal);
            while (!deadline.IsCancellationRequested)
            {
                Candidate? candidate;
                lock (gate)
                {
                    candidate = room.Standby;
                    if (candidate != null && (candidate.Slot.Leaving || candidate.Slot.Finished.Task.IsCompleted ||
                        attempted.Contains(candidate.Slot.Account.Id))) room.Standby = candidate = null;
                }
                if (candidate == null)
                {
                    await PrepareCandidate(room, duringMigration: true);
                    lock (gate) candidate = room.Standby;
                }
                if (candidate == null) break;
                attempted.Add(candidate.Slot.Account.Id);
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(settings.MigrationCandidateSeconds));
                    await candidate.Connected.Task.WaitAsync(attempt.Token);
                    if (candidate.Session == null) throw new InvalidDataException("candidateUnavailable");
                    if (room.Backup == null && graceful && previous != null)
                    {
                        await Control(previous, new { kind = "backup" }, attempt.Token);
                        await WaitForBackup(room, attempt.Token);
                    }
                    if (room.Backup == null) throw new InvalidDataException("backupUnavailable");
                    if (graceful && previous != null && !previous.Stop.IsCancellationRequested)
                    {
                        await RestoreCandidate(room, candidate, attempt.Token);
                        lock (gate) { room.Migrating = true; room.FinalBackup = NewSignal(); }
                        await Notify(room, MigrationPhase.Recovering, candidate.Generation, "", attempt.Token);
                        try
                        {
                            await Control(previous, new { kind = "freeze", leavingAccountId = leavingAccount }, attempt.Token);
                            await room.FinalBackup.Task.WaitAsync(attempt.Token);
                        }
                        catch (Exception error) when (TransportFailure(error) || error is TimeoutException)
                        { graceful = false; }
                    }
                    await RestoreCandidate(room, candidate, attempt.Token);
                    await CommitCandidate(room, candidate, previous, attempt.Token);
                    return;
                }
                catch (Exception error) when (TransportFailure(error) || error is TimeoutException)
                {
                    candidate.Session?.Stop.Cancel();
                    bool fenced;
                    lock (gate)
                    {
                        candidate.Slot.Capable = false;
                        if (room.Standby == candidate) room.Standby = null;
                        fenced = room.Generation >= candidate.Generation;
                        if (fenced) room.Migrating = true;
                    }
                    if (deadline.IsCancellationRequested) break;
                    if (fenced)
                    {
                        graceful = false;
                        if (previous != null) { previous.Retiring = true; previous.Stop.Cancel(); previous = null; }
                        await Notify(room, MigrationPhase.Recovering, checked(room.Generation + 1), "", deadline.Token);
                    }
                    else if (previous != null && !previous.Stop.IsCancellationRequested && graceful)
                    {
                        await Control(previous, new { kind = "resume" }, deadline.Token);
                        lock (gate) room.Migrating = false;
                        await Notify(room, MigrationPhase.None, room.Generation, "", deadline.Token);
                    }
                }
            }
            await CloseRoom(room, "migrationUnavailable");
        }
        catch (Exception error) when (TransportFailure(error) || error is TimeoutException)
        { if (!room.Ended.IsCancellationRequested) await CloseRoom(room, "migrationFailed"); }
    }

    private async Task WaitForBackup(Room room, CancellationToken cancellation)
    {
        while (true)
        {
            lock (gate) if (room.Backup != null) return;
            await Task.Delay(25, cancellation);
        }
    }

    private async Task RestoreCandidate(Room room, Candidate candidate, CancellationToken cancellation)
    {
        ulong sourceGeneration;
        string[] accounts;
        lock (gate)
        {
            if (room.BackupState == null || room.Backup == null) throw new InvalidDataException("backupUnavailable");
            sourceGeneration = room.BackupState.Generation;
            candidate.DownloadAllowed = true;
            candidate.Restored = NewSignal();
            accounts = room.Players.Values.Where(slot => !slot.Leaving && !slot.Finished.Task.IsCompleted)
                .Select(slot => slot.Account.Id).ToArray();
        }
        await Control(candidate.Session!, new { kind = "restore", snapshotGeneration = sourceGeneration, accounts,
            creatorId = candidate.Slot.Account.Id, creatorNickname = candidate.Slot.Account.Nickname }, cancellation);
        await candidate.Restored.Task.WaitAsync(TimeSpan.FromSeconds(settings.MigrationCandidateSeconds), cancellation);
    }

    private async Task CommitCandidate(Room room, Candidate candidate, HostSession? previous, CancellationToken cancellation)
    {
        Slot[] players, leaving;
        long tick;
        lock (gate)
        {
            if (room.Standby != candidate || candidate.Session == null || candidate.Session.Stop.IsCancellationRequested ||
                candidate.Slot.Finished.Task.IsCompleted || !accounts.IsActive(candidate.Slot.Account.Id))
                throw new InvalidDataException("candidateUnavailable");
            room.Migrating = true;
            room.AwaitingBridges = true;
            room.Generation = candidate.Generation;
            room.Creator = candidate.Slot.Account;
            room.Active = candidate.Session;
            room.DataProof = candidate.Proof;
            room.Standby = null;
            if (previous != null) previous.Retiring = true;
            tick = room.BackupState!.Tick;
            players = room.Players.Values.Where(value => !value.Leaving && !value.Finished.Task.IsCompleted).ToArray();
            leaving = room.Players.Values.Where(value => value.Leaving).ToArray();
            foreach (Slot slot in players)
            {
                slot.Bridge?.Socket.Abort();
                slot.Bridge = null;
                slot.Key = NewKey(out byte[] proof);
                slot.Proof = proof;
                slot.Deadline = clock.GetUtcNow() + PairTimeout;
                slot.Paired = NewSignal();
            }
        }
        if (players.Length == 0) { await CloseRoom(room, "roomClosed"); return; }
        await Notify(room, MigrationPhase.Resyncing, room.Generation, "", cancellation);
        await Control(candidate.Session!, new { kind = "activate", accounts = players.Select(value => value.Account.Id).ToArray(),
            creatorId = room.Creator.Id, creatorNickname = room.Creator.Nickname }, cancellation);
        await candidate.Activated.Task.WaitAsync(TimeSpan.FromSeconds(settings.MigrationCandidateSeconds), cancellation);
        lock (gate) room.Migrating = false;
        foreach (Slot slot in players) await Invite(room, slot, cancellation);
        await Task.WhenAll(players.Select(slot => Task.WhenAny(slot.Paired.Task, slot.Finished.Task))).WaitAsync(cancellation);
        lock (gate) room.AwaitingBridges = false;
        foreach (Slot slot in leaving)
        {
            await Notice(room, slot, MigrationPhase.None, room.Generation, tick, false, "left", cancellation);
            slot.Revoked.Cancel();
        }
        if (previous != null)
        {
            try { await Control(previous, new { kind = "retire" }, cancellation); }
            catch (Exception error) when (TransportFailure(error)) { }
            previous.Stop.Cancel();
        }
        await PrepareCandidate(room);
    }

    private async Task Notify(Room room, MigrationPhase phase, ulong generation, string reason, CancellationToken cancellation)
    {
        Slot[] players;
        long tick;
        lock (gate) { players = room.Players.Values.ToArray(); tick = room.BackupState?.Tick ?? 0; }
        await Task.WhenAll(players.Where(value => !value.Finished.Task.IsCompleted).Select(value =>
            Notice(room, value, phase, generation, tick, value.Account.Id == room.Creator.Id, reason, cancellation)));
    }

    private async Task Notice(Room room, Slot slot, MigrationPhase phase, ulong generation, long tick,
        bool isHost, string reason, CancellationToken cancellation)
    {
        var writer = new NetDataWriter();
        ulong wireGeneration;
        int backupAgeMs;
        lock (gate)
        {
            wireGeneration = room.Generation;
            backupAgeMs = room.Backup == null ? -1 : (int)Math.Min(int.MaxValue,
                Math.Max(0, (clock.GetUtcNow() - room.BackupAt).TotalMilliseconds));
        }
        HostMigrationProtocol.WriteMigration(writer, phase, generation, tick, isHost, reason, backupAgeMs: backupAgeMs);
        try { await SendPlayer(slot, wireGeneration, writer.CopyData(), cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (TransportFailure(error)) { slot.Revoked.Cancel(); }
    }

    private async Task CloseRoom(Room room, string reason)
    {
        HostSession[] sessions;
        lock (gate)
        {
            if (!rooms.Remove(room.Info.Id)) return;
            foreach (string key in tickets.Where(value => value.Value.RoomId == room.Info.Id).Select(value => value.Key).ToArray()) tickets.Remove(key);
            sessions = new[] { room.Active, room.Standby?.Session }.OfType<HostSession>().Distinct().ToArray();
            foreach (HostSession session in sessions) session.Retiring = true;
        }
        await Notify(room, MigrationPhase.Failed, room.Generation, reason, CancellationToken.None);
        await Task.WhenAll(sessions.Select(async session =>
        {
            try { await Control(session, new { kind = "retire" }, CancellationToken.None); }
            catch (Exception error) when (TransportFailure(error)) { }
        }));
        room.Ended.Cancel();
        room.Active?.Stop.Cancel();
        room.Standby?.Session?.Stop.Cancel();
        if (room.Backup != null) CryptographicOperations.ZeroMemory(room.Backup);
        CryptographicOperations.ZeroMemory(room.DataProof);
        if (room.PasswordDigest != null) CryptographicOperations.ZeroMemory(room.PasswordDigest);
        room.Backup = null;
        room.BackupState = null;
    }

    private static bool TransportFailure(Exception error) => error is OperationCanceledException or WebSocketException or
        IOException or InvalidDataException or ObjectDisposedException;
}
