using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib;
using LiteNetLib.Utils;

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
        private UnitSnapshotAssembler units;
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
        private readonly Stopwatch clock = new Stopwatch();
        public GameRules WorldRules { get; private set; }
        public GameplayReplica Gameplay { get; private set; }
        public FogStateReplica Fog { get; private set; }
        public ConnectionState State { get; private set; }
        public int PlayerId { get; private set; } = -1;
        public JoinFailure JoinFailure { get; private set; }
        public bool Defeated { get; private set; }
        public bool Victorious { get; private set; }
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
                if (info.AdditionalData != null && info.AdditionalData.AvailableBytes == 1)
                {
                    byte code = info.AdditionalData.GetByte();
                    if (info.Reason == DisconnectReason.ConnectionRejected && code <= (byte)JoinFailure.StorageUnavailable)
                        JoinFailure = (JoinFailure)code;
                    if (info.Reason == DisconnectReason.RemoteConnectionClose && code == (byte)SessionEndReason.BaseDestroyed)
                        Defeated = true;
                    if (info.Reason == DisconnectReason.RemoteConnectionClose && code == (byte)SessionEndReason.Victory)
                        Victorious = true;
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
                    OnPacket(packetReader, packet.Channel, packet.Method, draining: true);
                }
                if (deferred.Count > 0) return;
            }
            if (manager.IsRunning) manager.PollEvents(256);
            if (webSocket != null)
            {
                for (int i = 0; i < 256 && webSocket != null && webSocket.TryReceive(out byte[] packet); i++)
                {
                    ReceivedPayloadBytes += packet.Length;
                    packetReader.SetSource(packet);
                    PacketKind kind = (PacketKind)packet[0];
                    byte channel = kind == PacketKind.ChatMessage ? M5Protocol.ChatChannel : kind == PacketKind.Gameplay ? (byte)2 :
                        kind == PacketKind.FogBaseline || kind == PacketKind.FogDelta || kind == PacketKind.Forget ? (byte)1 : (byte)0;
                    DeliveryMethod method = kind == PacketKind.Pose || kind == PacketKind.Units
                        ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
                    OnPacket(packetReader, channel, method);
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
            if (Gameplay.HasExpired(clock.Elapsed.TotalSeconds))
            { string reason=Gameplay.FailureReason; Disconnect(); Error = reason.Length>0?reason:"신뢰 동기화 대기가 2초 또는 1MiB를 초과했습니다."; }
        }

        public void SendTrain(ulong sequence, int element)
        {
            if (State != ConnectionState.Connected) return;
            Protocol.WriteTrain(writer, sequence, element); Send();
        }

        public void SendCancelTraining(ulong sequence, ulong orderId)
        {
            if (State != ConnectionState.Connected) return;
            Protocol.WriteCancelTraining(writer, sequence, orderId);
            Send();
        }

        public void SendRally(ulong sequence)
        {
            if (State != ConnectionState.Connected) return;
            Protocol.WriteRally(writer, sequence); Send();
        }

        public void SendBuild(ulong sequence, BuildingKind kind, int cellX, int cellZ, int towerElement = -1)
        {
            if (State != ConnectionState.Connected) return;
            Protocol.WriteBuild(writer, sequence, kind, cellX, cellZ, towerElement);
            Send();
        }

        public void SendUpgradeBase(ulong sequence)
        {
            if (State != ConnectionState.Connected) return;
            Protocol.WriteUpgradeBase(writer, sequence);
            Send();
        }

        public void SendMove(ulong sequence, Vector2 direction)
        {
            if (State != ConnectionState.Connected) return;
            Protocol.WriteMove(writer, sequence, direction);
            Send();
        }

        public void SetLobbyReady(int element, bool ready)
        {
            if (State != ConnectionState.Lobby) return;
            M5Protocol.WriteLobbyReady(writer, element, ready);
            LastLobbyError = null;
            Send();
        }

        public void StartRoom()
        {
            if (State != ConnectionState.Lobby) return;
            M5Protocol.WriteLobbyStart(writer);
            LastLobbyError = null;
            Send();
        }

        public void SetLobbyAiCount(int count)
        {
            if (State != ConnectionState.Lobby || count < 0 || count >= rules.MaxPlayers) return;
            M5Protocol.WriteLobbyAiCount(writer, count);
            LastLobbyError = null;
            Send();
        }

        public void SendChat(bool global, string text)
        {
            if (State != ConnectionState.Connected) return;
            M5Protocol.WriteChat(writer, global, text); Send(M5Protocol.ChatChannel);
        }

        public void ReportChat(ulong id, string reason)
        {
            if (State != ConnectionState.Connected || !CanReportChat) return;
            M5Protocol.WriteChatReport(writer, id, reason); Send();
        }

        private void Send(byte channel = 0)
        {
            SentPayloadBytes += writer.Length;
            if (webSocket != null) webSocket.Send(writer.CopyData());
            else peer?.Send(writer, channel, DeliveryMethod.ReliableOrdered);
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
            Gameplay?.Dispose();
            WorldRules = worldRules;
            units = new UnitSnapshotAssembler(worldRules);
            visibilityFrames = new VisibilityFrameBuffer(worldRules);
            Fog = new FogStateReplica(worldRules);
            Gameplay = new GameplayReplica(worldRules);
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
            deferred.Clear();
            deferredBytes = 0;
            units.Reset();
            Fog.Reset(); visibilityFrames.Reset(); Gameplay.Reset(); clock.Reset();
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
            OnPacket(reader, channel, method);
        }

        private void OnPacket(NetDataReader reader, byte channel, DeliveryMethod method, bool draining = false)
        {
            if (reader.AvailableBytes < 1 || reader.AvailableBytes > WebSocketClientTransport.MaxPacketBytes) return;
            PacketKind kind = (PacketKind)reader.PeekByte();
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
                { Gameplay.TryAccept(reader, clock.Elapsed.TotalSeconds); DrainGameplay(); }
                return;
            }
            if (channel == 1)
            {
                if (method != DeliveryMethod.ReliableOrdered) return;
                if (kind == PacketKind.FogBaseline || kind == PacketKind.FogDelta)
                {
                    if (Fog.TryAccept(reader))
                    {
                        FogChanged?.Invoke(Fog);
                        if (State == ConnectionState.Connected && visibilityFrames.Release(Fog, out var ready)) UnitsReceived?.Invoke(ready);
                        DrainGameplay();
                    }
                }
                else if (kind == PacketKind.Forget && FogProtocol.TryReadEntityForget(reader, entitiesForgotten, out long tick, out _, out int owner, out int count) && (PlayerId<0||owner==PlayerId) && Gameplay.BindPlayer(owner))
                {
                    int unitCount = 0;
                    for (int index = 0; index < count; index++)
                    {
                        ObservedEntityId entity = entitiesForgotten[index]; Gameplay.Forget(entity, tick);
                        if (entity.Kind == ObservedEntityKind.Unit) forgotten[unitCount++] = entity.Id;
                    }
                    if (unitCount > 0) UnitsForgotten?.Invoke(tick, forgotten, unitCount);
                    EntitiesForgotten?.Invoke(tick, entitiesForgotten, count);
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
            if (kind == PacketKind.Welcome && method == DeliveryMethod.ReliableOrdered && State != ConnectionState.Connected &&
                Protocol.TryReadWelcome(reader, out int player, out bool resumed, out byte[] accountKey, out int roomPlayers))
            {
                if (roomPlayers > rules.MaxPlayers) return;
                bool changed = roomPlayers != WorldRules.RoomPlayers;
                if (changed) ConfigureWorld(rules.ForRoom(roomPlayers));
                if (player <= 0 || !Fog.BindPlayer(player) || !Gameplay.BindPlayer(player)) return;
                PlayerId = player;
                Resumed = resumed;
                IssuedAccountKey = accountKey;
                Lobby = null;
                State = ConnectionState.Connected;
                if (changed) WorldConfigured?.Invoke(WorldRules);
                DrainGameplay();
            }
            else if (kind == PacketKind.LobbyState && method == DeliveryMethod.ReliableOrdered && State != ConnectionState.Connected &&
                M5Protocol.TryReadLobbyState(reader, rules, out LobbyState lobby))
            { Lobby = lobby; State = ConnectionState.Lobby; }
            else if (kind == PacketKind.LobbyError && method == DeliveryMethod.ReliableOrdered &&
                M5Protocol.TryReadLobbyError(reader, out LobbyError lobbyError)) LastLobbyError = lobbyError;
            else if (kind == PacketKind.SessionEnded && method == DeliveryMethod.ReliableOrdered && State == ConnectionState.Connected &&
                reader.AvailableBytes == 2)
            {
                reader.GetByte();
                byte reason = reader.GetByte();
                Defeated = reason == (byte)SessionEndReason.BaseDestroyed;
                Victorious = reason == (byte)SessionEndReason.Victory;
                if (Defeated || Victorious) Disconnect();
            }
            else if (State == ConnectionState.Connected && kind == PacketKind.Pose && method == DeliveryMethod.Unreliable && Protocol.TryReadPose(reader, WorldRules, out LegionPose pose))
            {
                if (pose.PlayerId != PlayerId || pose.Tick <= latestPose) return;
                latestPose = pose.Tick; PoseReceived?.Invoke(pose);
            }
            else if (State == ConnectionState.Connected && kind == PacketKind.Units && method == DeliveryMethod.Unreliable)
            {
                if (units.TryAccept(reader) && units.PlayerId == PlayerId && visibilityFrames.Accept(units, Fog, out var visible)) UnitsReceived?.Invoke(visible);
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
            while (Gameplay.TryPublish(Fog, out GameplayFrame frame)) GameplayReceived?.Invoke(frame);
        }
    }
}
