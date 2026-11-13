using System;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    public enum PacketKind : byte
    {
        Hello = 1, Welcome, Move, Pose, Units, Rejected, FogBaseline, FogDelta, Forget, Train, Rally, Gameplay, Accepted,
        Build, UpgradeBase, CancelTraining, ChatSend, ChatMessage, ChatReport, LobbyReady, LobbyStart, LobbyState,
        RoomDiscovery, RoomListing, SessionEnded, LobbyError, JoinError, LobbyAiCount,
        HostCapability, HostStandbyOffer, HostLeave, HostMigration, LobbyAiDifficulty, LobbyColor,
        MoveTo, Transfer, Focus, Recall, AcknowledgeJourney, QuickTransfer = 40, UpgradeFacility = 41
    }
    public enum JoinFailure : byte { None, Malformed, VersionMismatch, DataMismatch, InvalidElement, WorldFull, InvalidNickname, InvalidResumeKey, AlreadyConnected, RoomStarted, StorageUnavailable }
    public enum SessionEndReason : byte { BaseDestroyed = 1, Victory = 2 }

    /// <summary>자기 군단의 서버 확정 중심·적용 방향·입력 확인 번호다.</summary>
    public readonly struct LegionPose
    {
        public long Tick { get; }
        public int PlayerId { get; }
        public ulong AppliedSequence { get; }
        public Vector2 Position { get; }
        public Vector2 Direction { get; }
        public byte SquadId { get; }
        public uint MovementRevision { get; }
        public LegionPose(long tick, int playerId, ulong sequence, Vector2 position, Vector2 direction,
            byte squadId = 1, uint movementRevision = 0)
        { Tick = tick; PlayerId = playerId; AppliedSequence = sequence; Position = position; Direction = direction; SquadId = squadId; MovementRevision = movementRevision; }
    }

    /// <summary>현재 시야에서 받은 유닛 표시 값이다. 판정 상태나 가변 월드를 포함하지 않는다.</summary>
    public readonly struct NetworkUnit
    {
        public int Id { get; }
        public int OwnerId { get; }
        public int Element { get; }
        public Vector2 Position { get; }
        public bool Garrison { get; }
        public float Health { get; }
        public float Shield { get; }
        public bool Converted { get; }
        public bool Ghost { get; }
        public ushort StatusMask { get; }
        public byte SquadId { get; }
        public bool IsReinforcing { get; }
        public bool IsSquadTransfer { get; }
        public uint MovementRevision { get; }
        public NetworkUnit(int id, int ownerId, int element, Vector2 position, bool garrison)
            :this(id,ownerId,element,position,garrison,0,0,false,0) { }
        public NetworkUnit(int id,int ownerId,int element,Vector2 position,bool garrison,float health,float shield,bool converted,ushort statusMask,bool ghost=false,
            byte? squadId=null,bool isReinforcing=false,uint movementRevision=0,bool isSquadTransfer=false)
        {Id=id;OwnerId=ownerId;Element=element;Position=position;Garrison=garrison;Health=health;Shield=shield;Converted=converted;StatusMask=statusMask;Ghost=ghost;
            SquadId=squadId??(byte)(ownerId<0?0:garrison?4:1);IsReinforcing=isReinforcing;MovementRevision=movementRevision;IsSquadTransfer=isSquadTransfer;}
    }

    /// <summary>비신뢰 MTU 청크를 같은 틱 단위로 조립한다. 완성 전에는 이전 관측값을 유지한다.</summary>
    public sealed class UnitSnapshotAssembler
    {
        private readonly GameRules rules;
        private readonly NetworkUnit[] scratch = new NetworkUnit[Protocol.MaxUnits];
        private readonly NetworkUnit[] incoming = new NetworkUnit[Protocol.MaxUnits];
        private long assemblingTick = -1;
        private int expectedCount, owner;
        private Vector2 anchor;
        private readonly bool[] received = new bool[Protocol.MaxUnitChunks];
        private int receivedCount;
        private uint expectedRevision;
        public NetworkUnit[] Units { get; } = new NetworkUnit[Protocol.MaxUnits];
        public int Count { get; private set; }
        public int PlayerId { get; private set; } = -1;
        public long Tick { get; private set; } = -1;
        public Vector2 Anchor { get; private set; }
        public uint Revision { get; private set; }
        public UnitSnapshotAssembler(GameRules rules) { this.rules = rules ?? throw new ArgumentNullException(nameof(rules)); }

        public void Reset() { assemblingTick = -1; Tick = -1; Count = 0; PlayerId = -1; receivedCount=0;Array.Clear(received,0,received.Length); Revision = 0; }

        /// <summary>현재 완성 프레임만 보관한다. 미래 시야를 기다리는 연결 내부 버퍼에서 사용한다.</summary>
        internal void CopyCompletedFrom(UnitSnapshotAssembler source)
        {
            Array.Copy(source.Units, Units, source.Count);
            Tick = source.Tick; Count = source.Count; PlayerId = source.PlayerId; Anchor = source.Anchor; Revision = source.Revision;
        }

        public bool TryAccept(NetDataReader reader)
        {
            if (!Protocol.ReadUnitChunk(reader, rules, incoming, out long tick, out int player, out Vector2 origin, out int total, out int part, out uint revision)) return false;
            if (tick <= Tick || tick < assemblingTick) return false;
            if (tick > assemblingTick)
            { assemblingTick = tick; expectedCount = total; owner = player; anchor = origin; Array.Clear(received,0,received.Length);receivedCount=0; expectedRevision = revision; }
            if (total != expectedCount || owner != player || anchor != origin || expectedRevision != revision) return false;
            int offset = part * Protocol.UnitsPerChunk;
            int length=Math.Min(Protocol.UnitsPerChunk,total-offset);
            if(received[part])
            {for(int index=offset;index<offset+length;index++)if(!incoming[index].Equals(scratch[index]))return false;}
            else {Array.Copy(incoming,offset,scratch,offset,length);received[part]=true;receivedCount++;}
            int parts = Protocol.ChunkCount(total);
            if (receivedCount != parts) return false;
            Array.Copy(scratch, Units, total);
            Tick = tick; Count = total; PlayerId = owner; Anchor = anchor; Revision = revision;
            return true;
        }
    }
}
