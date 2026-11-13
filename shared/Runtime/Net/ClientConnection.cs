using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib;
using LiteNetLib.Utils;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ElementalLegion.Tests")]

namespace ElementalLegion.Shared.Net
{
    public enum ConnectionState { Disconnected, Connecting, Connected, Lobby }

    /// <summary>UDP와 온라인 WSS 연결에서 게임 패킷과 복제 상태를 함께 처리한다.</summary>
    public sealed class ClientConnection : IDisposable
    {
        private const double WelcomeTimeoutSeconds = 10;
        private readonly GameRules rules;
        private readonly NetworkSettings settings;
        private readonly NetManager manager;
        private readonly NetDataWriter writer = new NetDataWriter();
        private UnitSnapshotAssembler units, migrationUnits;
        private NetPeer peer;
        private WebSocketClientTransport webSocket;
        private bool supportsChatReports;
        private readonly NetDataReader packetReader = new NetDataReader();
        private readonly Queue<(byte[] Data, byte Channel, DeliveryMethod Method)> deferred = new Queue<(byte[], byte, DeliveryMethod)>();
        private int deferredBytes;
        private double deferredSince;
        private long latestPose = -1;
        private VisibilityFrameBuffer visibilityFrames;
        private readonly int[] forgotten = new int[Protocol.MaxUnits];
        private readonly ObservedEntityId[] entitiesForgotten = new ObservedEntityId[FogProtocol.ForgetPerChunk];
        private readonly Dictionary<ObservedEntityId, long> migrationForgotten = new Dictionary<ObservedEntityId, long>();
        private const int MaxMigrationForgotten = Protocol.MaxUnits + GameplayCodec.MaxBuildings + GameplayCodec.MaxObjects * 2 + GameplayCodec.MaxCandidates;
        private readonly Stopwatch clock = new Stopwatch();
        public GameRules WorldRules { get; private set; }
        private GameplayReplica gameplay, displayedGameplay;
        private FogStateReplica fog, displayedFog;
        private LegionPose? stagedPose;
        private readonly LegionPose[] poseBuffer = new LegionPose[4];
        private readonly LegionPose[] stagedPoses = new LegionPose[4];
        private int stagedPoseCount;
        private bool stagingMigration, stagedUnits, stagedGameplay, hostingCapable;
        private bool? advertisedHosting;
        private ulong advertisedGeneration;
        private double lastPoseAt, terminalLeaveUntil;
        private int migrationBackupAgeMs = -1;
        public GameplayReplica Gameplay => displayedGameplay ?? gameplay;
        public FogStateReplica Fog => displayedFog ?? fog;
        public ulong HostGeneration { get; private set; }
        public MigrationPhase MigrationStatus { get; private set; }
        public bool IsHostServer { get; private set; }
        /// <summary>패배한 방장은 중계의 인계 완료 응답을 받을 때까지 서버와 연결을 유지한다.</summary>
        public bool AwaitingHostLeave { get; private set; }
        public bool IsMigrating => AwaitingHostLeave || MigrationStatus == MigrationPhase.Recovering || MigrationStatus == MigrationPhase.Resyncing || MigrationStatus == MigrationPhase.Failed;
        public double LastPoseAgeSeconds => State == ConnectionState.Connected ? clock.Elapsed.TotalSeconds - lastPoseAt : 0;
        public bool HostingCapable
        {
            get => hostingCapable;
            set { hostingCapable = value; AdvertiseHosting(); }
        }
        public event Action<HostStandbyOffer> HostStandbyRequested;
        public event Action<HostMigrationNotice> MigrationChanged;
        public ConnectionState State { get; private set; }
        public int PlayerId { get; private set; } = -1;
        public JoinFailure JoinFailure { get; private set; }
        public bool Defeated { get; private set; }
        public bool Victorious { get; private set; }
        public JourneyResultView FinalJourney { get; private set; }
        public LobbyState? Lobby { get; private set; }
        public LobbyError? LastLobbyError { get; private set; }
        public byte[] IssuedAccountKey { get; private set; }
        public bool Resumed { get; private set; }
        public bool IsOnline => webSocket != null;
        public bool CanReportChat => IsOnline && supportsChatReports;
        public bool DeferGameplay { get; set; }
        public long SentPayloadBytes { get; private set; }
        public long ReceivedPayloadBytes { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public CommandAdmission LastCommandFailure { get; private set; } = CommandAdmission.Accepted;
        public event Action<LegionPose> PoseReceived;
        public event Action<UnitSnapshotAssembler> UnitsReceived;
        public event Action<FogStateReplica> FogChanged;
        public event Action<GameplayFrame> GameplayReceived;
        public event Action<ulong, CommandAdmission> CommandResponse;
        public event Action<ChatMessage> ChatReceived;
        public event Action<GameRules> WorldConfigured;
        public event Action<long, ObservedEntityId[], int> EntitiesForgotten;
        /// <summary>버퍼는 재사용되므로 구독자는 콜백 안에서 ID를 소비한다.</summary>
        public event Action<long, int[], int> UnitsForgotten;

        public ClientConnection(GameRules rules, NetworkSettings settings)
        {
            this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            ConfigureWorld(rules);
            var listener = new EventBasedNetListener();
            manager = new NetManager(listener) { AutoRecycle = true, ChannelsCount = M5Protocol.ChatChannel + 1 };
            listener.PeerDisconnectedEvent += (source, info) =>
            {
                if (source != peer) return;
                if(info.Reason==DisconnectReason.RemoteConnectionClose&&State==ConnectionState.Connected&&info.AdditionalData!=null&&
                    Protocol.TryReadSessionEndData(info.AdditionalData,PlayerId,out var reason,out var journey))
                {
                    FinalJourney=journey;Defeated=reason==SessionEndReason.BaseDestroyed;Victorious=reason==SessionEndReason.Victory;
                }
                else if (info.AdditionalData != null && info.AdditionalData.AvailableBytes == 1)
                {
                    byte code = info.AdditionalData.GetByte();
                    if (info.Reason == DisconnectReason.ConnectionRejected && code <= (byte)JoinFailure.StorageUnavailable)
                        JoinFailure = (JoinFailure)code;
                }
                else if (info.AdditionalData != null && info.AdditionalData.AvailableBytes == 2 &&
                    info.AdditionalData.GetByte() == (byte)PacketKind.JoinError)
                {
                    byte code = info.AdditionalData.GetByte();
                    if (code <= (byte)JoinFailure.StorageUnavailable) JoinFailure = (JoinFailure)code;
                }
                ResetSession(info.Reason.ToString());
            };
            listener.NetworkErrorEvent += (_, error) => { Error = error.ToString(); };
            listener.NetworkReceiveEvent += OnReceive;
        }

        public void Connect(string host, int element, string nickname = PlayerNickname.Default) => Connect(host, settings.Port, element, nickname);

        /// <summary>통합 검증처럼 임시 포트에 연결할 때 사용한다. 게임 실행은 JSON 포트를 사용한다.</summary>
        public void Connect(string host, int port, int element, string nickname = PlayerNickname.Default,
            byte[] resumeKey = null, byte[] hostProof = null, bool requestAccount = false)
        {
            if (port <= 0 || port > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("서버 주소가 필요합니다.", nameof(host));
            BeginConnection();
            try
            {
                Protocol.WriteHello(writer, rules.DataHash, element, nickname, resumeKey, hostProof, requestAccount);
                if (!manager.Start()) throw new InvalidOperationException("클라이언트 UDP 소켓을 열지 못했습니다.");
                peer = manager.Connect(host, port, writer) ?? throw new InvalidOperationException("UDP 연결을 시작하지 못했습니다.");
            }
            catch { Disconnect(); throw; }
        }

        public void ConnectOnline(Uri endpoint, byte[] ticket, bool supportsReports = true)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "wss" ||
                endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
                throw new ArgumentException("보안 WebSocket 주소가 필요합니다.", nameof(endpoint));
            if (ticket == null || ticket.Length != 32) throw new ArgumentException("입장권 형식이 잘못됐습니다.", nameof(ticket));
            BeginConnection();
            webSocket = new WebSocketClientTransport(endpoint, ticket);
            supportsChatReports = supportsReports;
        }

        private void BeginConnection()
        {
            Disconnect();
            JoinFailure = JoinFailure.None;
            Defeated = Victorious = Resumed = false;
            FinalJourney=default;
            if (IssuedAccountKey != null) Array.Clear(IssuedAccountKey, 0, IssuedAccountKey.Length);
            IssuedAccountKey = null;
            State = ConnectionState.Connecting;
            clock.Restart();
        }

        public void Poll()
        {
            if (deferred.Count > 0 && clock.Elapsed.TotalSeconds - deferredSince > 2)
            {
                Disconnect(); Error = "전투 씬 전환 중 동기화 대기 시간이 초과됐습니다.";
                return;
            }
            if (!DeferGameplay && State == ConnectionState.Connected)
            {
                for (int i = 0; i < 256 && deferred.Count > 0; i++)
                {
                    var packet = deferred.Dequeue();
                    deferredBytes -= packet.Data.Length;
                    packetReader.SetSource(packet.Data);
                    ReceivePacket(packetReader, packet.Channel, packet.Method, draining: true);
                }
                if (deferred.Count > 0) return;
            }
            if (manager.IsRunning) manager.PollEvents(256);
            if (webSocket != null)
            {
                for (int i = 0; i < 256 && webSocket != null && webSocket.TryReceive(out byte[] packet); i++)
                {
                    ReceivedPayloadBytes += packet.Length;
                    if (!OnlineFrame.TryRead(packet, packet.Length, out ulong generation, out byte[] body))
                    { Disconnect(); Error = "인터넷 프레임 형식이 잘못됐습니다."; return; }
                    PacketKind framedKind = (PacketKind)body[0];
                    if (framedKind != PacketKind.HostMigration)
                    {
                        if (State == ConnectionState.Connecting && HostGeneration == 0) HostGeneration = generation;
                        if (generation != HostGeneration) continue;
                    }
                    else
                    {
                        if (generation < HostGeneration || !HostMigrationProtocol.TryReadMigration(new NetDataReader(body), out HostMigrationNotice envelope)) continue;
                        bool nextRecovery = envelope.Phase == MigrationPhase.Recovering && generation != ulong.MaxValue && envelope.Generation == generation + 1;
                        if (envelope.Generation != generation && !nextRecovery) continue;
                    }
                    packet = body;
                    packetReader.SetSource(packet);
                    PacketKind kind = (PacketKind)packet[0];
                    byte channel = kind == PacketKind.ChatMessage ? M5Protocol.ChatChannel : kind == PacketKind.Gameplay ? (byte)2 :
                        kind == PacketKind.FogBaseline || kind == PacketKind.FogDelta || kind == PacketKind.Forget ? (byte)1 : (byte)0;
                    DeliveryMethod method = kind == PacketKind.Pose || kind == PacketKind.Units
                        ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
                    ReceivePacket(packetReader, channel, method);
                }
                if (webSocket?.Failure is string failure)
                { Disconnect(); Error = failure; }
            }
            if (State == ConnectionState.Connecting && clock.Elapsed.TotalSeconds > WelcomeTimeoutSeconds)
            {
                Disconnect();
                Error = "서버 입장 응답을 받지 못했습니다.";
                return;
            }
            if (AwaitingHostLeave && clock.Elapsed.TotalSeconds >= terminalLeaveUntil) Disconnect();
            if (!AwaitingHostLeave && gameplay.HasExpired(clock.Elapsed.TotalSeconds))
            { string reason=gameplay.FailureReason; Disconnect(); Error = reason.Length>0?reason:"신뢰 동기화 대기가 2초 또는 1MiB를 초과했습니다."; }
        }

        public void SendTrain(ulong sequence, int element)
            => SendTrain(sequence,element,1,4);
        public void SendTrain(ulong sequence,int element,int count,byte targetSquad)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteTrain(writer, sequence, element,count,targetSquad); Send();
        }

        public void SendCancelTraining(ulong sequence, ulong orderId)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteCancelTraining(writer, sequence, orderId);
            Send();
        }

        public void SendRally(ulong sequence) => SendRally(sequence, 1, 0);
        public void SendRally(ulong sequence, byte target, int count)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteRally(writer, sequence, target, count); Send();
        }

        public void SendBuild(ulong sequence, BuildingKind kind, int cellX, int cellZ, int towerElement = -1)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteBuild(writer, sequence, kind, cellX, cellZ, towerElement);
            Send();
        }

        public void SendUpgradeBase(ulong sequence)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteUpgradeBase(writer, sequence);
            Send();
        }

        public void SendUpgradeFacility(ulong sequence, int buildingId, FacilityUpgradeBranch branch, byte targetRank)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteUpgradeFacility(writer, sequence, buildingId, branch, targetRank);
            Send();
        }

        public void SendMove(ulong sequence, Vector2 direction) => SendMove(sequence, 1, direction);
        public void SendMove(ulong sequence, byte mask, Vector2 direction)
        {
            if (State != ConnectionState.Connected || AwaitingHostLeave || (MigrationStatus == MigrationPhase.Recovering || MigrationStatus == MigrationPhase.Failed)) return;
            Protocol.WriteMove(writer, sequence, mask, direction);
            Send();
        }

        public void SendMoveTo(ulong sequence, byte mask, Vector2 destination)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteMoveTo(writer, sequence, mask, destination); Send();
        }

        public void SendTransfer(ulong sequence, byte from, byte to, int count)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteTransfer(writer, sequence, from, to, count); Send();
        }

        public void SendQuickTransfer(ulong sequence, byte from, byte to)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteQuickTransfer(writer, sequence, from, to); Send();
        }

        public void SendFocus(ulong sequence, byte anchor)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            Protocol.WriteFocus(writer, sequence, anchor); Send();
        }

        public void SendRecall(ulong sequence,byte squad)
        {
            if(State!=ConnectionState.Connected||IsMigrating)return;
            Protocol.WriteRecall(writer,sequence,squad);Send();
        }

        public void SendAcknowledgeJourney(ulong sequence)
        {
            if(State!=ConnectionState.Connected||IsMigrating)return;
            Protocol.WriteAcknowledgeJourney(writer,sequence);Send();
        }

        public void SetLobbyReady(int element, bool ready)
        {
            if (State != ConnectionState.Lobby || IsMigrating) return;
            M5Protocol.WriteLobbyReady(writer, element, ready);
            LastLobbyError = null;
            Send();
        }

        public void SetLobbyColor(int colorId)
        {
            if (State != ConnectionState.Lobby || IsMigrating || !Lobby.HasValue ||
                Lobby.Value.Members[Lobby.Value.SelfIndex].Ready || !PlayerPalette.IsValid(colorId)) return;
            M5Protocol.WriteLobbyColor(writer, colorId);
            LastLobbyError = null;
            Send();
        }

        public void StartRoom()
        {
            if (State != ConnectionState.Lobby || IsMigrating) return;
            M5Protocol.WriteLobbyStart(writer);
            LastLobbyError = null;
            Send();
        }

        public void SetLobbyAiCount(int count)
        {
            if (State != ConnectionState.Lobby || IsMigrating || count < 0 || count >= rules.MaxPlayers) return;
            M5Protocol.WriteLobbyAiCount(writer, count);
            LastLobbyError = null;
            Send();
        }

        public void SetLobbyAiDifficulty(int aiIndex, AiDifficulty difficulty)
        {
            if (State != ConnectionState.Lobby || IsMigrating || !Lobby.HasValue ||
                aiIndex < 0 || aiIndex >= Lobby.Value.AiCount || difficulty > AiDifficulty.Hard) return;
            M5Protocol.WriteLobbyAiDifficulty(writer, aiIndex, difficulty);
            LastLobbyError = null;
            Send();
        }

        public void SendChat(bool global, string text)
        {
            if (State != ConnectionState.Connected || IsMigrating) return;
            M5Protocol.WriteChat(writer, global, text); Send(M5Protocol.ChatChannel);
        }

        public void ReportChat(ulong id, string reason)
        {
            if (State != ConnectionState.Connected || IsMigrating || !CanReportChat) return;
            M5Protocol.WriteChatReport(writer, id, reason); Send();
        }

        private void Send(byte channel = 0)
        {
            if (webSocket != null)
            {
                byte[] bytes = OnlineFrame.Wrap(HostGeneration, writer.CopyData());
                SentPayloadBytes += bytes.Length;
                webSocket.Send(bytes);
            }
            else { SentPayloadBytes += writer.Length; peer?.Send(writer, channel, DeliveryMethod.ReliableOrdered); }
        }

        public void Disconnect()
        {
            webSocket?.Dispose();
            webSocket = null;
            peer = null;
            if (manager.IsRunning) manager.Stop();
            ResetSession(string.Empty);
        }

        private void ConfigureWorld(GameRules worldRules)
        {
            gameplay?.Dispose();
            displayedGameplay?.Dispose(); displayedGameplay = null; displayedFog = null;
            WorldRules = worldRules;
            units = new UnitSnapshotAssembler(worldRules);
            visibilityFrames = new VisibilityFrameBuffer(worldRules);
            fog = new FogStateReplica(worldRules);
            gameplay = new GameplayReplica(worldRules);
        }

        private void ResetSession(string error)
        {
            State = ConnectionState.Disconnected;
            PlayerId = -1;
            peer = null;
            Error = error;
            Lobby = null;
            LastLobbyError = null;
            latestPose = -1;
            HostGeneration = 0; IsHostServer = false; MigrationStatus = MigrationPhase.None;
            advertisedHosting = null; advertisedGeneration = 0;
            migrationBackupAgeMs = -1;
            AwaitingHostLeave = false; terminalLeaveUntil = 0;
            stagingMigration = stagedUnits = stagedGameplay = false; stagedPose = null; stagedPoseCount = 0;
            migrationForgotten.Clear();
            displayedGameplay?.Dispose(); displayedGameplay = null; displayedFog = null;
            deferred.Clear();
            deferredBytes = 0;
            units.Reset();
            fog.Reset(); visibilityFrames.Reset(); gameplay.Reset(); clock.Reset();
        }

        private void AdvertiseHosting()
        {
            if (webSocket == null || HostGeneration == 0 || State != ConnectionState.Connected && State != ConnectionState.Lobby) return;
            if (advertisedHosting == hostingCapable && advertisedGeneration == HostGeneration) return;
            HostMigrationProtocol.WriteCapability(writer, hostingCapable); Send();
            advertisedHosting = hostingCapable; advertisedGeneration = HostGeneration;
        }

        public void RequestHostLeave()
        {
            if (webSocket == null || !IsHostServer || State == ConnectionState.Disconnected) return;
            HostMigrationProtocol.WriteLeave(writer); Send();
        }

        private void AcceptMigration(HostMigrationNotice notice)
        {
            if (notice.Generation < HostGeneration) return;
            migrationBackupAgeMs = notice.BackupAgeMs;
            bool commit = notice.Phase == MigrationPhase.None || notice.Phase == MigrationPhase.Resyncing;
            bool changed = notice.Generation > HostGeneration;
            if (commit && changed)
            {
                HostGeneration = notice.Generation;
                if (State == ConnectionState.Connected && !AwaitingHostLeave && notice.Reason != "left") BeginMigrationBaseline();
                else if (State == ConnectionState.Lobby) MigrationStatus = MigrationPhase.Resyncing;
            }
            IsHostServer = notice.IsHost;
            if (stagingMigration && commit) MigrationStatus = MigrationPhase.Resyncing;
            else if (!(State == ConnectionState.Lobby && changed && commit)) MigrationStatus = notice.Phase;
            MigrationChanged?.Invoke(new HostMigrationNotice(MigrationStatus, notice.Generation, notice.Tick, notice.IsHost, notice.Reason, notice.BackupAgeMs));
            if (notice.Phase == MigrationPhase.Failed) Error = "서버를 복구하지 못했습니다. " + notice.Reason;
            if (AwaitingHostLeave && (notice.Reason == "left" || notice.Phase == MigrationPhase.Failed)) Disconnect();
        }

        private void BeginMigrationBaseline()
        {
            if (displayedGameplay == null) { displayedGameplay = gameplay; displayedFog = fog; }
            else gameplay.Dispose();
            gameplay = new GameplayReplica(WorldRules); gameplay.BindPlayer(PlayerId);
            fog = new FogStateReplica(WorldRules); fog.BindPlayer(PlayerId);
            units = new UnitSnapshotAssembler(WorldRules); migrationUnits = new UnitSnapshotAssembler(WorldRules);
            visibilityFrames = new VisibilityFrameBuffer(WorldRules);
            latestPose = -1; stagedPose = null; stagedPoseCount = 0; stagedUnits = stagedGameplay = false; stagingMigration = true;
            migrationForgotten.Clear();
            deferred.Clear(); deferredBytes = 0;
        }

        private void PublishUnits(UnitSnapshotAssembler snapshot)
        {
            if (stagingMigration)
            {
                migrationUnits.CopyCompletedFrom(snapshot); stagedUnits = true; TryCompleteMigration();
            }
            else UnitsReceived?.Invoke(snapshot);
        }

        private void TryCompleteMigration()
        {
            if (!stagingMigration || !stagedPose.HasValue || !stagedUnits || !stagedGameplay || !fog.Ready ||
                !gameplay.State.SpatialReady || migrationUnits.Revision != fog.Revision) return;
            stagingMigration = false; MigrationStatus = MigrationPhase.None;
            displayedGameplay?.Dispose(); displayedGameplay = null; displayedFog = null;
            MigrationChanged?.Invoke(new HostMigrationNotice(MigrationPhase.None, HostGeneration, stagedPose.Value.Tick, IsHostServer, "ready", migrationBackupAgeMs));
            // 이전 표시를 초기화한 뒤 제거 기록을 적용해야 롤백된 새 유닛 프레임에서 되살아나지 않는다.
            foreach (var item in migrationForgotten)
            {
                if (item.Key.Kind == ObservedEntityKind.Unit)
                { forgotten[0] = item.Key.Id; UnitsForgotten?.Invoke(item.Value, forgotten, 1); }
                entitiesForgotten[0] = item.Key; EntitiesForgotten?.Invoke(item.Value, entitiesForgotten, 1);
            }
            migrationForgotten.Clear();
            for(int index=0;index<stagedPoseCount;index++)PoseReceived?.Invoke(stagedPoses[index]);
            UnitsReceived?.Invoke(migrationUnits); FogChanged?.Invoke(fog);
            GameplayReceived?.Invoke(gameplay.State); stagedPose = null;
        }

        private void CompleteLobbyMigration()
        {
            MigrationStatus = MigrationPhase.None;
            MigrationChanged?.Invoke(new HostMigrationNotice(MigrationPhase.None, HostGeneration, 0, IsHostServer, "ready", migrationBackupAgeMs));
        }

        public void Dispose()
        {
            Disconnect();
            if (IssuedAccountKey != null) Array.Clear(IssuedAccountKey, 0, IssuedAccountKey.Length);
            IssuedAccountKey = null;
        }

        private void OnReceive(NetPeer source, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (source != peer) return;
            ReceivedPayloadBytes += reader.AvailableBytes;
            ReceivePacket(reader, channel, method);
        }

        internal void ReceivePacket(NetDataReader reader, byte channel, DeliveryMethod method, bool draining = false)
        {
            if (reader.AvailableBytes < 1 || reader.AvailableBytes > WebSocketClientTransport.MaxPacketBytes) return;
            PacketKind kind = (PacketKind)reader.PeekByte();
            if (AwaitingHostLeave && kind != PacketKind.HostMigration) return;
            // UDP 채널 순서와 씬 로딩에 관계없이 Welcome의 크기를 먼저 적용한다.
            if (!draining && (DeferGameplay || State != ConnectionState.Connected || deferred.Count > 0) &&
                (channel == 1 || channel == 2 || kind == PacketKind.Pose || kind == PacketKind.Units || kind == PacketKind.ChatMessage))
            {
                if (deferred.Count == 0) deferredSince = clock.Elapsed.TotalSeconds;
                deferredBytes += reader.AvailableBytes;
                if (deferredBytes > WebSocketClientTransport.MaxPendingBytes || deferred.Count >= 4096)
                { Disconnect(); Error = "전투 씬 전환 중 동기화 대기 한도를 초과했습니다."; return; }
                deferred.Enqueue((reader.GetRemainingBytes(), channel, method));
                return;
            }
            if (channel == 2)
            {
                if (method == DeliveryMethod.ReliableOrdered && kind == PacketKind.Gameplay)
                { gameplay.TryAccept(reader, clock.Elapsed.TotalSeconds); DrainGameplay(); }
                return;
            }
            if (channel == 1)
            {
                if (method != DeliveryMethod.ReliableOrdered) return;
                if (kind == PacketKind.FogBaseline || kind == PacketKind.FogDelta)
                {
                    if (fog.TryAccept(reader))
                    {
                        if (!stagingMigration) FogChanged?.Invoke(fog);
                        if (State == ConnectionState.Connected && visibilityFrames.Release(fog, out var ready)) PublishUnits(ready);
                        DrainGameplay();
                    }
                }
                else if (kind == PacketKind.Forget && FogProtocol.TryReadEntityForget(reader, entitiesForgotten, out long tick, out _, out int owner, out int count) && (PlayerId<0||owner==PlayerId) && gameplay.BindPlayer(owner))
                {
                    int unitCount = 0;
                    for (int index = 0; index < count; index++)
                    {
                        ObservedEntityId entity = entitiesForgotten[index]; gameplay.Forget(entity, tick);
                        if (stagingMigration)
                        {
                            if (!migrationForgotten.TryGetValue(entity, out long previous) || tick > previous)
                            {
                                if (migrationForgotten.Count >= MaxMigrationForgotten && !migrationForgotten.ContainsKey(entity))
                                { Disconnect(); Error = "승계 중 관측 제거 대기 한도를 초과했습니다."; return; }
                                migrationForgotten[entity] = tick;
                            }
                        }
                        if (entity.Kind == ObservedEntityKind.Unit) forgotten[unitCount++] = entity.Id;
                    }
                    if (!stagingMigration)
                    {
                        if (unitCount > 0) UnitsForgotten?.Invoke(tick, forgotten, unitCount);
                        EntitiesForgotten?.Invoke(tick, entitiesForgotten, count);
                    }
                }
                return;
            }
            if (channel == M5Protocol.ChatChannel)
            {
                if (kind == PacketKind.ChatMessage && method == DeliveryMethod.ReliableOrdered && State == ConnectionState.Connected &&
                    M5Protocol.TryReadChatMessage(reader, settings, out ChatMessage chat)) ChatReceived?.Invoke(chat);
                return;
            }
            if (channel != 0) return;
            if (kind == PacketKind.HostStandbyOffer && HostMigrationProtocol.TryReadStandby(reader, out HostStandbyOffer offer))
            { if (hostingCapable && offer.Generation > HostGeneration) HostStandbyRequested?.Invoke(offer); return; }
            if (kind == PacketKind.HostMigration && HostMigrationProtocol.TryReadMigration(reader, out HostMigrationNotice notice))
            { AcceptMigration(notice); return; }
            if (kind == PacketKind.Welcome && method == DeliveryMethod.ReliableOrdered && (State != ConnectionState.Connected || stagingMigration) &&
                Protocol.TryReadWelcome(reader, out int player, out bool resumed, out byte[] accountKey, out int roomPlayers, out float mapSize))
            {
                if (roomPlayers > rules.MaxPlayers || mapSize > rules.MapSize) return;
                bool changed = roomPlayers != WorldRules.RoomPlayers || mapSize != WorldRules.MapSize;
                if (stagingMigration && changed) { Disconnect(); Error = "승계 중 전장 크기가 변경됐습니다."; return; }
                if (changed) ConfigureWorld(rules.ForRoom(roomPlayers, mapSize));
                if (player <= 0 || !fog.BindPlayer(player) || !gameplay.BindPlayer(player)) return;
                if (stagingMigration && PlayerId != player) { Disconnect(); Error = "승계 중 플레이어 ID가 변경됐습니다."; return; }
                PlayerId = player;
                Resumed = resumed;
                IssuedAccountKey = accountKey;
                Lobby = null;
                State = ConnectionState.Connected;
                lastPoseAt = clock.Elapsed.TotalSeconds;
                if (MigrationStatus == MigrationPhase.Resyncing && !stagingMigration) CompleteLobbyMigration();
                AdvertiseHosting();
                if (changed) WorldConfigured?.Invoke(WorldRules);
                DrainGameplay();
            }
            else if (kind == PacketKind.LobbyState && method == DeliveryMethod.ReliableOrdered && State != ConnectionState.Connected &&
                M5Protocol.TryReadLobbyState(reader, rules, out LobbyState lobby))
            {
                Lobby = lobby; State = ConnectionState.Lobby; AdvertiseHosting();
                if (MigrationStatus == MigrationPhase.Resyncing) CompleteLobbyMigration();
            }
            else if (kind == PacketKind.LobbyError && method == DeliveryMethod.ReliableOrdered &&
                M5Protocol.TryReadLobbyError(reader, out LobbyError lobbyError)) LastLobbyError = lobbyError;
            else if (kind == PacketKind.SessionEnded && method == DeliveryMethod.ReliableOrdered && State == ConnectionState.Connected &&
                Protocol.TryReadSessionEnded(reader,PlayerId,out var reason,out var journey))
            {
                FinalJourney=journey;
                Defeated = reason == SessionEndReason.BaseDestroyed;
                Victorious = reason == SessionEndReason.Victory;
                if (Defeated && IsHostServer)
                {
                    // 패배는 개인 세션 종료다. 방장 프로세스는 남은 참가자에게 최종 상태를 넘긴 뒤 정리한다.
                    AwaitingHostLeave = true;
                    terminalLeaveUntil = clock.Elapsed.TotalSeconds + settings.MigrationDeadlineSeconds;
                    deferred.Clear(); deferredBytes = 0;
                    RequestHostLeave();
                }
                else if (Defeated || Victorious) Disconnect();
            }
            else if (State == ConnectionState.Connected && kind == PacketKind.Pose && method == DeliveryMethod.Unreliable && Protocol.TryReadPoses(reader, WorldRules, poseBuffer, out int poseCount))
            {
                if (poseBuffer[0].PlayerId != PlayerId || poseBuffer[0].Tick <= latestPose) return;
                latestPose = poseBuffer[0].Tick; lastPoseAt = clock.Elapsed.TotalSeconds;
                if (stagingMigration)
                {
                    Array.Copy(poseBuffer,stagedPoses,poseCount);stagedPoseCount=poseCount;
                    stagedPose=poseBuffer[0];TryCompleteMigration();
                }
                else for(int index=0;index<poseCount;index++)PoseReceived?.Invoke(poseBuffer[index]);
            }
            else if (State == ConnectionState.Connected && kind == PacketKind.Units && method == DeliveryMethod.Unreliable)
            {
                if (units.TryAccept(reader) && units.PlayerId == PlayerId && visibilityFrames.Accept(units, fog, out var visible)) PublishUnits(visible);
            }
            else if (kind == PacketKind.Rejected && method == DeliveryMethod.ReliableOrdered && reader.AvailableBytes == 10)
            {
                reader.GetByte(); ulong sequence = reader.GetULong(); byte code = reader.GetByte();
                if (code <= (byte)CommandAdmission.QueueFull) { LastCommandFailure = (CommandAdmission)code; CommandResponse?.Invoke(sequence, LastCommandFailure); }
            }
            else if (kind == PacketKind.Accepted && method == DeliveryMethod.ReliableOrdered && reader.AvailableBytes == 9)
            { reader.GetByte(); ulong sequence = reader.GetULong(); LastCommandFailure = CommandAdmission.Accepted; CommandResponse?.Invoke(sequence, LastCommandFailure); }
        }

        private void DrainGameplay()
        {
            if (State != ConnectionState.Connected) return;
            while (gameplay.TryPublish(fog, out GameplayFrame frame))
            {
                if (stagingMigration) { stagedGameplay = true; TryCompleteMigration(); }
                else GameplayReceived?.Invoke(frame);
            }
        }
    }
}
