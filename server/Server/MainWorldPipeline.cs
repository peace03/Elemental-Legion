using System.Diagnostics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Server;

/// <summary>실제 복제·제한 큐·주기 캡처까지 포함한 서버 틱 경계의 CPU 비용이다.</summary>
public sealed class MainWorldPipelineResult
{
    public TickStatistics? Statistics { get; internal set; }
    public double BudgetMs { get; internal set; }
    public int OwnersMeasured { get; internal set; }
    public int MinActiveOwners { get; internal set; } = int.MaxValue;
    public long OwnerFrames { get; internal set; }
    public long QueuedBytes { get; internal set; }
    public long DrainedBytes { get; internal set; }
    public long ReliablePackets { get; internal set; }
    public long SnapshotChunks { get; internal set; }
    public int MaxPacketBytes { get; internal set; }
    public int MaxPendingBytes { get; internal set; }
    public int QueueFailures { get; internal set; }
    public int MigrationCaptures { get; internal set; }
    public int CheckpointCaptures { get; internal set; }
    public long CaptureAllocatedBytes { get; internal set; }
    public long AllocatedBytes { get; internal set; }
    public long ReplicationTicks { get; internal set; }
    public long EventCaptureTicks { get; internal set; }
    public long PrepareTicks { get; internal set; }
    public long WriteQueueTicks { get; internal set; }
    public long DrainTicks { get; internal set; }
    public long MigrationCaptureTicks { get; internal set; }
    public long CheckpointCaptureTicks { get; internal set; }
    public bool Passed => Statistics != null && Statistics.P99Ms <= BudgetMs && QueueFailures == 0;
}

/// <summary>WSS와 같은 복제 및 출력 큐를 실행하며 소켓 I/O는 수행하지 않는다.</summary>
internal sealed class MainWorldPipeline : IDisposable
{
    private readonly GameRules rules;
    private readonly NetworkSettings settings;
    private readonly MainWorldPipelineResult result;
    private readonly OnlinePeer[] peers;
    private readonly NetDataWriter writer = new();
    private readonly long backupEveryTicks, checkpointEveryTicks;

    internal MainWorldPipeline(GameRules rules, NetworkSettings settings, WorldSetup setup, MainWorldPipelineResult result)
    {
        this.rules = rules; this.settings = settings; this.result = result;
        backupEveryTicks = checked((long)Math.Ceiling(settings.MigrationBackupSeconds * rules.TickRate));
        checkpointEveryTicks = checked((long)settings.SaveIntervalSeconds * rules.TickRate);
        peers = new OnlinePeer[setup.Players.Count];
        for (int index = 0; index < peers.Length; index++)
        {
            PlayerSpawn player = setup.Players[index];
            peers[index] = new OnlinePeer(new AccountIdentity(player.Id.ToString(), player.Nickname), rules.ElementIndex(player.MainElement))
            { Owner = player.Id, Fog = new FogReplicationState(rules), Gameplay = new GameplayReplicationState(rules) };
        }
        result.OwnersMeasured = Math.Max(result.OwnersMeasured, peers.Length);
    }

    internal void Tick(SimulationWorld world, BotBrain[] brains, long tick)
    {
        long started = Stopwatch.GetTimestamp();
        Drain();
        result.DrainTicks += Stopwatch.GetTimestamp() - started;
        int active = 0;
        foreach (OnlinePeer peer in peers)
        {
            if (!world.TryGetView(peer.Owner, out WorldView view)) continue;
            bool ghost = false;
            if (world.HasGhosts)
                foreach (BuildingView building in view.Buildings)
                    if (building.OwnerId == peer.Owner && building.Kind == BuildingKind.Base && building.GhostEndTick != 0) { ghost = true; break; }
            if (ghost) continue;
            active++;
            if (peer.Disconnected || peer.IsSlow()) { result.QueueFailures++; peer.Abort(); continue; }
            long ownerStarted = Stopwatch.GetTimestamp(), capturesBefore = result.EventCaptureTicks, preparesBefore = result.PrepareTicks;
            try
            {
                peer.Gameplay!.Capture(view);
                result.EventCaptureTicks += Stopwatch.GetTimestamp() - ownerStarted;
                bool includeUnits = tick % settings.UnitsEveryTicks == 0;
                if (includeUnits)
                {
                    var fog = peer.Fog!;
                    long preparing = Stopwatch.GetTimestamp();
                    fog.Prepare(view);
                    result.PrepareTicks += Stopwatch.GetTimestamp() - preparing;
                    for (int part = 0; part < fog.ForgetChunkCount; part++) { fog.WriteForget(writer, part); Queue(peer); }
                    for (int part = 0; part < fog.ChunkCount; part++) { fog.WriteChunk(writer, part); Queue(peer); }
                    preparing = Stopwatch.GetTimestamp(); fog.Commit();
                    peer.Gameplay.Prepare(view, fog.Revision);
                    result.PrepareTicks += Stopwatch.GetTimestamp() - preparing;
                    for (int part = 0; part < peer.Gameplay.ChunkCount; part++) { peer.Gameplay.WriteChunk(writer, part); Queue(peer); }
                    preparing = Stopwatch.GetTimestamp(); peer.Gameplay.Commit();
                    result.PrepareTicks += Stopwatch.GetTimestamp() - preparing;
                }
                byte[]? pose = null;
                byte[][]? units = null;
                if (tick % settings.LegionEveryTicks == 0)
                { Protocol.WritePose(writer, view); RecordPacket(); pose = writer.CopyData(); result.SnapshotChunks++; }
                if (includeUnits)
                {
                    units = new byte[Protocol.ChunkCount(view.Units.Count)][];
                    for (int part = 0; part < units.Length; part++)
                    {
                        Protocol.WriteUnits(writer, view, rules.MapSize, part, peer.Fog!.Revision);
                        RecordPacket(); units[part] = writer.CopyData(); result.SnapshotChunks++;
                    }
                }
                if (pose != null || units != null) peer.ReplaceSnapshot(pose, units);
                result.OwnerFrames++;
            }
            catch (ReplicationOverflowException) { result.QueueFailures++; peer.Abort(); }
            finally { result.WriteQueueTicks += Stopwatch.GetTimestamp() - ownerStarted - (result.EventCaptureTicks - capturesBefore) - (result.PrepareTicks - preparesBefore); }
        }
        result.MinActiveOwners = Math.Min(result.MinActiveOwners, active);
        result.ReplicationTicks += Stopwatch.GetTimestamp() - started;
        // 고정 부하의 새 월드에서도 표본의 연속 틱을 기준으로 캡처 주기를 지킨다.
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (tick % backupEveryTicks == 0)
        {
            started = Stopwatch.GetTimestamp();
            GC.KeepAlive(world.CaptureMigrationState());
            foreach (BotBrain brain in brains) GC.KeepAlive(brain.CaptureState());
            result.MigrationCaptureTicks += Stopwatch.GetTimestamp() - started;
            result.MigrationCaptures++;
        }
        if (tick % checkpointEveryTicks == 0)
        {
            started = Stopwatch.GetTimestamp();
            GC.KeepAlive(world.CaptureCheckpoint());
            result.CheckpointCaptureTicks += Stopwatch.GetTimestamp() - started;
            result.CheckpointCaptures++;
        }
        result.CaptureAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        long draining = Stopwatch.GetTimestamp(); Drain();
        long drainTicks = Stopwatch.GetTimestamp() - draining;
        result.DrainTicks += drainTicks; result.ReplicationTicks += drainTicks;
    }

    private void Queue(OnlinePeer peer)
    {
        RecordPacket();
        peer.Send(writer);
        result.ReliablePackets++;
        result.MaxPendingBytes = Math.Max(result.MaxPendingBytes, peer.PendingBytes);
        if (peer.Disconnected) throw new ReplicationOverflowException();
    }

    private void RecordPacket()
    {
        if (writer.Length > Protocol.MaxDatagramBytes) throw new InvalidOperationException("복제 청크가 패킷 상한을 넘었습니다.");
        result.MaxPacketBytes = Math.Max(result.MaxPacketBytes, writer.Length);
        result.QueuedBytes += writer.Length;
    }

    /// <summary>정상 하류 처리 조건에서 실제 큐의 배출·완료 정산만 실행한다. 네트워크 전달 성공을 뜻하지 않는다.</summary>
    internal void Drain()
    {
        foreach (OnlinePeer peer in peers)
            while (peer.TryTakeOutput(out byte[]? packet, out byte[][]? batch, out bool close))
            {
                if (close) throw new InvalidOperationException("벤치 큐가 예상하지 못한 종료 상태입니다.");
                if (packet != null) result.DrainedBytes += packet.Length;
                if (batch != null) foreach (byte[] part in batch) result.DrainedBytes += part.Length;
                peer.CompleteOutput();
            }
    }

    public void Dispose()
    {
        Drain();
        foreach (OnlinePeer peer in peers) peer.Abort();
    }
}
