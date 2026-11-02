using System;
using System.Numerics;
using System.Diagnostics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    public enum ConnectionState { Disconnected, Connecting, Connected }

    /// <summary>Unity와 콘솔 검증에서 함께 사용하는 클라이언트 UDP 수명이다. Unity API를 호출하지 않는다.</summary>
    public sealed class ClientConnection : IDisposable
    {
        private readonly GameRules rules;
        private readonly NetworkSettings settings;
        private readonly NetManager manager;
        private readonly NetDataWriter writer = new NetDataWriter();
        private readonly UnitSnapshotAssembler units;
        private NetPeer peer;
        private long latestPose = -1;
        private readonly VisibilityFrameBuffer visibilityFrames;
        private readonly int[] forgotten = new int[Protocol.MaxUnits];
        private readonly ObservedEntityId[] entitiesForgotten = new ObservedEntityId[FogProtocol.ForgetPerChunk];
        private readonly Stopwatch clock = new Stopwatch();
        public GameplayReplica Gameplay { get; }
        public FogStateReplica Fog { get; }
        public ConnectionState State { get; private set; }
        public int PlayerId { get; private set; } = -1;
        public JoinFailure JoinFailure { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public CommandAdmission LastCommandFailure { get; private set; } = CommandAdmission.Accepted;
        public event Action<LegionPose> PoseReceived;
        public event Action<UnitSnapshotAssembler> UnitsReceived;
        public event Action<FogStateReplica> FogChanged;
        public event Action<GameplayFrame> GameplayReceived;
        public event Action<ulong, CommandAdmission> CommandResponse;
        public event Action<long, ObservedEntityId[], int> EntitiesForgotten;
        /// <summary>버퍼는 재사용되므로 구독자는 콜백 안에서 ID를 소비한다.</summary>
        public event Action<long, int[], int> UnitsForgotten;

        public ClientConnection(GameRules rules, NetworkSettings settings)
        {
            this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            units = new UnitSnapshotAssembler(rules);
            visibilityFrames = new VisibilityFrameBuffer(rules); Fog = new FogStateReplica(rules);
            Gameplay = new GameplayReplica(rules);
            var listener = new EventBasedNetListener();
            manager = new NetManager(listener) { AutoRecycle = true, ChannelsCount = 3 };
            listener.PeerDisconnectedEvent += (source, info) =>
            {
                if (source != peer) return;
                State = ConnectionState.Disconnected; PlayerId = -1; peer = null;
                if (info.AdditionalData != null && info.AdditionalData.AvailableBytes == 1)
                {
                    byte code = info.AdditionalData.GetByte();
                    if (code <= (byte)JoinFailure.WorldFull) JoinFailure = (JoinFailure)code;
                }
                Error = info.Reason.ToString();
                Fog.Reset(); visibilityFrames.Reset(); units.Reset(); Gameplay.Reset();
            };
            listener.NetworkErrorEvent += (_, error) => { Error = error.ToString(); };
            listener.NetworkReceiveEvent += OnReceive;
        }

        public void Connect(string host, int element)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("서버 주소가 필요합니다.", nameof(host));
            Disconnect();
            JoinFailure = JoinFailure.None; Error = string.Empty; latestPose = -1;
            Protocol.WriteHello(writer, rules.DataHash, element);
            if (!manager.Start()) throw new InvalidOperationException("클라이언트 UDP 소켓을 열지 못했습니다.");
            State = ConnectionState.Connecting; clock.Restart();
            try { peer = manager.Connect(host, settings.Port, writer); }
            catch { Disconnect(); throw; }
        }

        /// <summary>통합 검증처럼 임시 포트에 연결할 때 사용한다. 게임 실행은 JSON 포트를 사용한다.</summary>
        public void Connect(string host, int port, int element)
        {
            if (port <= 0 || port > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("서버 주소가 필요합니다.", nameof(host));
            Disconnect(); JoinFailure = JoinFailure.None; Error = string.Empty; latestPose = -1;
            Protocol.WriteHello(writer, rules.DataHash, element);
            if (!manager.Start()) throw new InvalidOperationException("클라이언트 UDP 소켓을 열지 못했습니다.");
            State = ConnectionState.Connecting; clock.Restart();
            try { peer = manager.Connect(host, port, writer); }
            catch { Disconnect(); throw; }
        }

        public void Poll()
        {
            if (manager.IsRunning) manager.PollEvents(256);
            if (Gameplay.HasExpired(clock.Elapsed.TotalSeconds))
            { string reason=Gameplay.FailureReason; Disconnect(); Error = reason.Length>0?reason:"신뢰 동기화 대기가 2초 또는 1MiB를 초과했습니다."; }
        }

        public void SendTrain(ulong sequence, int element)
        {
            if (State != ConnectionState.Connected || peer == null) return;
            Protocol.WriteTrain(writer, sequence, element); peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendRally(ulong sequence)
        {
            if (State != ConnectionState.Connected || peer == null) return;
            Protocol.WriteRally(writer, sequence); peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendMove(ulong sequence, Vector2 direction)
        {
            if (State != ConnectionState.Connected || peer == null) return;
            Protocol.WriteMove(writer, sequence, direction);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void Disconnect()
        {
            if (manager.IsRunning) manager.Stop();
            State = ConnectionState.Disconnected; PlayerId = -1; peer = null;
            units.Reset();
            Fog.Reset(); visibilityFrames.Reset(); Gameplay.Reset(); clock.Reset();
        }

        public void Dispose() => Disconnect();

        private void OnReceive(NetPeer source, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (source != peer || reader.AvailableBytes < 1) return;
            PacketKind kind = (PacketKind)reader.PeekByte();
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
            if (channel != 0) return;
            if (kind == PacketKind.Welcome && method == DeliveryMethod.ReliableOrdered && reader.AvailableBytes == 5)
            {
                reader.GetByte(); int player = reader.GetInt();
                if (player > 0 && Fog.BindPlayer(player) && Gameplay.BindPlayer(player)) { PlayerId = player; State = ConnectionState.Connected; DrainGameplay(); }
            }
            else if (State == ConnectionState.Connected && kind == PacketKind.Pose && method == DeliveryMethod.Unreliable && Protocol.TryReadPose(reader, rules, out LegionPose pose))
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
