using System.Diagnostics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ElementalLegion.Server;

/// <summary>소켓 콜백과 월드를 한 실행 스레드에서 갱신하고 관측 가능한 상태만 전송한다.</summary>
public sealed partial class UdpGameServer : IDisposable
{
    internal NetworkLoadTrace? LoadTrace { get; init; }
    private readonly GameRules rules;
    private readonly NetworkSettings settings;
    private readonly NetManager manager;
    private readonly NetDataWriter writer = new();
    private sealed class Session
    {
        public readonly int Owner;
        public readonly NetPeer Peer;
        public readonly FogReplicationState Fog;
        public readonly NetworkTrafficCounter Traffic;
        public readonly GameplayReplicationState Gameplay;
        public readonly WorldView LastView;
        public readonly ReliableSendWindow Window = new();
        public bool SentGameplay;
        public bool Resumed;
        public byte[]? AccountKey;
        public bool TrafficReported, GameplayReported;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public Session(int owner, NetPeer peer, GameRules rules, WorldView view)
        { Owner = owner; Peer = peer; Fog = new FogReplicationState(rules); Gameplay = new GameplayReplicationState(rules); Traffic = new NetworkTrafficCounter(owner); LastView = view; }
    }
    private readonly Dictionary<int, Session> sessions = new();
    private readonly Dictionary<int, NetworkTrafficReport> finished = new();
    private readonly Queue<int> finishedOrder = new();
    private readonly Queue<NetworkTrafficReport> reports = new();
    private readonly Dictionary<int, GameplayReport> finishedGameplay = new();
    private readonly Queue<GameplayReport> gameplayReports = new();
    public NetworkWorld Simulation { get; private set; }
    public int Port => manager.LocalPort;
    public long SentPayloadBytes { get; private set; }
    public string LastSocketError { get; private set; } = string.Empty;

    public UdpGameServer(GameRules rules, NetworkSettings settings, string scenario = "normal",
        NetworkWorld? restored = null, IAccountStore? accounts = null, Dictionary<string, int>? owners = null,
        LanRoomOptions? room = null)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Simulation = restored ?? new NetworkWorld(rules, settings, scenario, waitingRoom: room != null);
        accountStore = accounts ?? (room == null ? null : new LanAccounts());
        accountOwners = owners ?? new Dictionary<string, int>();
        lanRoom = room;
        if (room != null && restored != null) roomStarted = hostClaimed = hostJoined = true;
        chat = new ChatRouter(settings);
        var listener = new EventBasedNetListener();
        manager = new NetManager(listener) { AutoRecycle = true, ChannelsCount = 4, EnableStatistics = true };
        listener.DeliveryEvent += (peer, tag) => { if (TrySession(peer, out Session? session)) session.Window.Delivered(tag); };
        listener.ConnectionRequestEvent += request =>
        {
            int helloBytes = request.Data.AvailableBytes;
            JoinFailure failure = Protocol.ReadHello(request.Data, rules, out int element, out string nickname,
                out byte[]? resumeKey, out byte[]? hostProof, out bool requestAccount);
            if (failure == JoinFailure.None && (room != null || accounts != null && (requestAccount || resumeKey != null)))
            {
                QueueAdmission(request, element, nickname, resumeKey, hostProof, helloBytes);
                return;
            }
            int player = -1;
            if (failure == JoinFailure.None) failure = Simulation.TryJoin(element, out player, nickname);
            if (failure != JoinFailure.None)
            {
                writer.Reset(); writer.Put((byte)failure); request.Reject(writer); return;
            }
            NetPeer peer = request.Accept();
            var session = new Session(player, peer, Simulation.Rules, Simulation.World.GetView(player));
            session.Traffic.RecordReceived(helloBytes, session.Clock.Elapsed.TotalSeconds);
            sessions.Add(peer.Id, session);
        };
        listener.PeerConnectedEvent += peer =>
        {
            if (!TrySession(peer, out Session? session)) return;
            Welcome(session);
        };
        listener.PeerDisconnectedEvent += (peer, _) =>
        {
            if (pending.TryGetValue(peer.Id, out var entry) && ReferenceEquals(entry.Peer, peer))
            {
                pending.Remove(peer.Id);
                if (entry.Host && !hostJoined) hostClaimed = false;
                DiscardAdmission(entry);
            }
            int index = lobby.FindIndex(member => ReferenceEquals(member.Peer, peer));
            if (index >= 0)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(lobby[index].Account.Token);
                lobby.RemoveAt(index);
                PublishLobby();
            }
            if (TrySession(peer, out Session? session)) { sessions.Remove(peer.Id); Finish(session); }
        };
        listener.NetworkErrorEvent += (_, error) => LastSocketError = error.ToString();
        listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            if (!TrySession(peer, out Session? session))
            {
                if (channel == 0 && method == DeliveryMethod.ReliableOrdered) ReceiveLobby(peer, reader);
                return;
            }
            session.Traffic.RecordReceived(reader.AvailableBytes, session.Clock.Elapsed.TotalSeconds);
            if (channel == M5Protocol.ChatChannel && method == DeliveryMethod.ReliableOrdered)
            {
                if (M5Protocol.TryReadChat(reader, settings, out var request))
                    chat.Send(Simulation, session.Owner, request, DeliverChat);
                return;
            }
            ulong sequence = 0;
            CommandAdmission admission = CommandAdmission.InvalidInput;
            try
            {
                if (channel == 0 && method == DeliveryMethod.ReliableOrdered && Protocol.TryReadCommand(reader, Simulation.Rules, out SimCommand command))
                { sequence = command.Sequence; admission = Simulation.Command(session.Owner, command); }
                writer.Reset();
                if (admission == CommandAdmission.Accepted) { writer.Put((byte)PacketKind.Accepted); writer.Put(sequence); }
                else { writer.Put((byte)PacketKind.Rejected); writer.Put(sequence); writer.Put((byte)admission); }
                Send(session, DeliveryMethod.ReliableOrdered);
            }
            catch (ReplicationOverflowException) { manager.DisconnectPeerForce(peer); }
        };
    }

    public void Start(int? port = null)
    {
        if (!manager.Start(port ?? settings.Port)) throw new IOException("서버 UDP 포트를 열지 못했습니다.");
        if (lanRoom != null) StartDiscovery();
    }

    public void Poll()
    {
        manager.PollEvents(Math.Max(1, rules.MaxPlayers * rules.CommandRateLimit));
        discovery?.PollEvents(16);
        ProcessAdmissions();
    }

    public void Step()
    {
        if (lanRoom != null && !roomStarted) return;
        Simulation.Step();
        long tick = Simulation.World.Metrics.Tick;
        for (int peerIndex = manager.ConnectedPeerList.Count - 1; peerIndex >= 0; peerIndex--)
        {
            NetPeer peer = manager.ConnectedPeerList[peerIndex];
            if (!TrySession(peer, out Session? session)) continue;
            if (!Simulation.World.TryGetView(session.Owner, out WorldView view))
            {
                EndSession(session, SessionEndReason.BaseDestroyed);
                continue;
            }
            if (Simulation.HasWinner)
            {
                roomFinished = true;
                EndSession(session, SessionEndReason.Victory);
                continue;
            }
            if (session.Window.HasExpired(session.Clock.Elapsed.TotalSeconds)) { manager.DisconnectPeerForce(peer); continue; }
            try
            {
            session.Gameplay.Capture(view);
            if (tick % settings.LegionEveryTicks == 0)
            { Protocol.WritePose(writer, view); Send(session, DeliveryMethod.Unreliable); }
            if (tick % settings.UnitsEveryTicks == 0)
            {
                SendFog(session, view);
                SendGameplay(session, view);
                for (int part = 0; part < Protocol.ChunkCount(view.Units.Count); part++)
                { Protocol.WriteUnits(writer, view, Simulation.Rules.MapSize, part, session.Fog.Revision); Send(session, DeliveryMethod.Unreliable); }
            }
            SampleTransport(session);
            }
            catch (ReplicationOverflowException) { manager.DisconnectPeerForce(peer); }
        }
    }

    public NetworkTrafficReport GetTraffic(int playerId)
    {
        foreach (Session session in sessions.Values)
            if (session.Owner == playerId) { SampleTransport(session); return session.Traffic.Snapshot(session.Clock.Elapsed.TotalSeconds); }
        if (finished.TryGetValue(playerId, out NetworkTrafficReport? report)) return report;
        throw new ArgumentOutOfRangeException(nameof(playerId));
    }

    public void WriteNetworkReport(TextWriter output)
    {
        DrainReports(output);
        foreach (Session session in sessions.Values)
        {
            if (session.TrafficReported) continue;
            GetTraffic(session.Owner).Write(output);
            session.TrafficReported = true;
        }
    }

    public GameplayReport GetGameplayReport(int playerId)
    {
        foreach (Session session in sessions.Values)
            if (session.Owner == playerId) return new GameplayReport(session.LastView, rules.TargetConvertedShare);
        if (finishedGameplay.TryGetValue(playerId, out GameplayReport? report)) return report;
        throw new ArgumentOutOfRangeException(nameof(playerId));
    }

    public void WriteGameplayReport(TextWriter output)
    {
        DrainGameplayReports(output);
        foreach (Session session in sessions.Values)
        {
            if (session.GameplayReported) continue;
            GetGameplayReport(session.Owner).Write(output);
            session.GameplayReported = true;
        }
    }

    public void Run(CancellationToken cancellation, TextWriter? reportOutput = null, TextWriter? gameplayOutput = null,
        Action? afterStep = null)
    {
        var clock = Stopwatch.StartNew(); double deadline = clock.Elapsed.TotalSeconds;
        while (!cancellation.IsCancellationRequested && !RoomClosed)
        {
            Poll();
            // 출력은 틱 및 소켓 콜백 밖에서만 수행한다.
            if (reportOutput != null) DrainReports(reportOutput);
            if (gameplayOutput != null) DrainGameplayReports(gameplayOutput);
            double now = clock.Elapsed.TotalSeconds;
            if (now < deadline) { cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(deadline - now, rules.DeltaTime))); continue; }
            long previousTick = Simulation.World.Metrics.Tick;
            long started = LoadTrace == null ? 0 : Stopwatch.GetTimestamp();
            Step();
            if (Simulation.World.Metrics.Tick != previousTick) LoadTrace?.Record(Simulation, started);
            afterStep?.Invoke(); deadline += rules.DeltaTime;
            // 장시간 일시 중단 뒤 월드 시간을 건너뛰지 않고 대기 기준만 다시 맞춘다.
            if (clock.Elapsed.TotalSeconds - deadline > rules.DeltaTime * rules.TickRate) deadline = clock.Elapsed.TotalSeconds;
        }
    }

    public void Dispose()
    {
        discovery?.Stop();
        manager.Stop();
        foreach (var admission in pending.Values) DiscardAdmission(admission);
        foreach (var member in lobby) System.Security.Cryptography.CryptographicOperations.ZeroMemory(member.Account.Token);
        foreach (Session session in sessions.Values) Finish(session);
        sessions.Clear();
    }

    private void SendFog(Session session, WorldView view)
    {
        session.Fog.Prepare(view);
        // 표시 해제는 큰 변경분보다 먼저 도착해 즉시 표현을 제거한다.
        for (int part = 0; part < session.Fog.ForgetChunkCount; part++)
        { session.Fog.WriteForget(writer, part); Send(session, DeliveryMethod.ReliableOrdered, 1); }
        for (int part = 0; part < session.Fog.ChunkCount; part++)
        { session.Fog.WriteChunk(writer, part); Send(session, DeliveryMethod.ReliableOrdered, 1); }
        session.Fog.Commit();
    }

    private void SendGameplay(Session session, WorldView view)
    {
        session.Gameplay.Prepare(view, session.Fog.Revision);
        for (int part = 0; part < session.Gameplay.ChunkCount; part++)
        { session.Gameplay.WriteChunk(writer, part); Send(session, DeliveryMethod.ReliableOrdered, 2, !session.SentGameplay); }
        session.Gameplay.Commit(); session.SentGameplay = true;
    }

    private void Send(Session session, DeliveryMethod method, byte channel = 0, bool initialSync = false)
    {
        if (method == DeliveryMethod.ReliableOrdered)
            session.Peer.SendWithDeliveryEvent(writer, channel, method, session.Window.Record(writer.Length, session.Clock.Elapsed.TotalSeconds));
        else session.Peer.Send(writer, channel, method);
        session.Traffic.RecordSent(writer.Length, (PacketKind)writer.Data[0], session.Clock.Elapsed.TotalSeconds, initialSync);
        SentPayloadBytes += writer.Length;
    }

    private static void SampleTransport(Session session)
    {
        NetStatistics stats = session.Peer.Statistics;
        session.Traffic.SampleTransport(stats.BytesSent, stats.BytesReceived, stats.PacketsSent, stats.PacketsReceived, session.Clock.Elapsed.TotalSeconds);
    }

    private bool TrySession(NetPeer peer, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Session? session)
        => sessions.TryGetValue(peer.Id, out session) && ReferenceEquals(session.Peer, peer);

    private void EndSession(Session session, SessionEndReason reason)
    {
        Protocol.WriteSessionEndData(writer,reason,session.LastView.Journey);
        byte[] result=writer.CopyData();
        sessions.Remove(session.Peer.Id);
        Finish(session);
        manager.DisconnectPeer(session.Peer, result);
    }

    private void Finish(Session session)
    {
        SampleTransport(session);
        NetworkTrafficReport report = session.Traffic.Snapshot(session.Clock.Elapsed.TotalSeconds);
        var gameplay = new GameplayReport(session.LastView, rules.TargetConvertedShare);
        finished[session.Owner] = report;
        finishedGameplay[session.Owner] = gameplay;
        finishedOrder.Enqueue(session.Owner);
        if (finishedOrder.Count > rules.MaxPlayers)
        {
            int expired = finishedOrder.Dequeue();
            finished.Remove(expired);
            finishedGameplay.Remove(expired);
        }
        if (!session.TrafficReported)
        {
            if (reports.Count == rules.MaxPlayers) reports.Dequeue();
            reports.Enqueue(report);
        }
        if (!session.GameplayReported)
        {
            if (gameplayReports.Count == rules.MaxPlayers) gameplayReports.Dequeue();
            gameplayReports.Enqueue(gameplay);
        }
        session.Window.Clear();
        Simulation.Leave(session.Owner);
    }

    private void DrainReports(TextWriter output)
    {
        while (reports.Count > 0)
            reports.Dequeue().Write(output);
    }

    private void DrainGameplayReports(TextWriter output)
    {
        while (gameplayReports.Count > 0)
            gameplayReports.Dequeue().Write(output);
    }
}
