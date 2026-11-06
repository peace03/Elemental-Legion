using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace ElementalLegion.Server;

/// <summary>현재 서버와 확정된 차기 서버 사이에서만 백업을 전달한다.</summary>
internal sealed partial class HostedRelay
{
    public async Task Backup(HttpContext context, string id, bool download, CancellationToken stopping)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 426; return; }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = false });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        try
        {
            byte[] proof = await Authenticate(socket, lifetime.Token);
            Room? room;
            Candidate? candidate;
            HostSession? authorizedSession = null;
            lock (gate)
            {
                rooms.TryGetValue(id, out room);
                candidate = room?.Standby;
                if (room == null || room.Ended.IsCancellationRequested ||
                    (download ? candidate == null || !candidate.DownloadAllowed || candidate.Session?.Stop.IsCancellationRequested != false ||
                        !CryptographicOperations.FixedTimeEquals(proof, candidate.Proof) :
                        room.Active?.Stop.IsCancellationRequested != false || !CryptographicOperations.FixedTimeEquals(proof, room.DataProof)))
                    room = null;
                if (room != null) authorizedSession = download ? candidate!.Session : room.Active;
            }
            if (room == null) { await OnlineGameServer.RejectSocket(socket, "invalidTicket"); return; }
            using var roomLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, room.Ended.Token, authorizedSession!.Stop.Token);
            if (download)
            {
                while (!roomLifetime.IsCancellationRequested)
                {
                    // 예열과 최종 복원 사이 대기는 후보 수명에 묶고 실제 전송에만 제한 시간을 적용한다.
                    byte[] request = await ReadText(socket, 4, roomLifetime.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(roomLifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(settings.MigrationCandidateSeconds));
                    byte[] snapshot;
                    lock (gate)
                    {
                        if (!request.AsSpan().SequenceEqual("NEXT"u8) || room.Standby != candidate ||
                            candidate!.Session != authorizedSession || !candidate.DownloadAllowed ||
                            candidate.Generation != room.Generation + 1 || room.Backup == null)
                            throw new InvalidDataException("invalidPacket");
                        candidate.DownloadAllowed = false;
                        snapshot = room.Backup;
                    }
                    await MigrationSnapshotTransfer.Write(socket, snapshot, timeout.Token);
                }
                return;
            }
            if (!await room.UploadGate.WaitAsync(0, roomLifetime.Token)) throw new InvalidDataException("rateLimited");
            try
            {
                while (!roomLifetime.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(roomLifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(settings.MigrationCandidateSeconds + settings.MigrationBackupSeconds));
                    byte[] compressed = await MigrationSnapshotTransfer.Read(socket, timeout.Token);
                    HostMigrationSnapshot state = MigrationSnapshotCodec.Decode(compressed, rules);
                    lock (gate)
                    {
                        if (room.Active?.Stop.IsCancellationRequested != false ||
                            !CryptographicOperations.FixedTimeEquals(proof, room.DataProof) ||
                            state.RoomId != id || state.Generation != room.Generation ||
                            room.BackupState is { } previous && previous.Generation == state.Generation &&
                            (state.BackupNumber <= previous.BackupNumber || state.Tick < previous.Tick))
                            throw new InvalidDataException("invalidPacket");
                        room.Backup = compressed;
                        room.BackupState = state;
                        room.BackupAt = clock.GetUtcNow();
                    }
                    await socket.SendAsync("OK"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                }
            }
            finally { room.UploadGate.Release(); }
        }
        catch (Exception error) when (TransportFailure(error))
        {
            if (error is InvalidDataException) await OnlineGameServer.RejectSocket(socket, "invalidPacket");
        }
    }
}
