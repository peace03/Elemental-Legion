using System;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>시야 배치 청크와 이전 관측 ID의 표시 해제 계약이다. 위치·전투 상태를 담지 않는다.</summary>
    public static class FogProtocol
    {
        public const int HeaderBytes = 29;
        public const int PayloadBytes = Protocol.MaxDatagramBytes - HeaderBytes;
        public const int ForgetHeaderBytes = 19;
        public const int ForgetPerChunk = (Protocol.MaxDatagramBytes - ForgetHeaderBytes) / 5;
        public const int MaxCells = 1024 * 1024;
        public const int MaxRememberedBuildings = GameplayCodec.MaxBuildings;
        internal const int DeltaFixedBytes = sizeof(int) + sizeof(ushort);
        internal const int DeltaCellBytes = sizeof(int) + sizeof(byte);

        internal static void WriteChunk(NetDataWriter writer, PacketKind kind, long tick, int owner, uint revision, uint baseline, NetDataWriter body, int part)
        {
            int offset = checked(part * PayloadBytes), length = Math.Min(PayloadBytes, body.Length - offset);
            if (part < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(part));
            writer.Reset(); writer.Put((byte)kind); writer.Put(tick); writer.Put(owner); writer.Put(revision); writer.Put(baseline);
            writer.Put(body.Length); writer.Put(offset); writer.Put(body.Data, offset, length);
        }

        internal static bool ReadHeader(NetDataReader reader, int maxBytes, out PacketKind kind, out long tick, out int owner, out uint revision, out uint baseline, out int total, out int offset)
        {
            kind = 0; tick = -1; owner = -1; revision = 0; baseline = 0; total = 0; offset = 0;
            if (reader.AvailableBytes < HeaderBytes || reader.AvailableBytes > Protocol.MaxDatagramBytes) return false;
            kind = (PacketKind)reader.GetByte(); tick = reader.GetLong(); owner = reader.GetInt(); revision = reader.GetUInt(); baseline = reader.GetUInt(); total = reader.GetInt(); offset = reader.GetInt();
            return (kind == PacketKind.FogBaseline || kind == PacketKind.FogDelta) && tick >= 0 && owner > 0 && revision > 0 && total > 0 && total <= maxBytes && offset >= 0 && offset < total && offset % PayloadBytes == 0 && reader.AvailableBytes == Math.Min(PayloadBytes, total - offset);
        }

        public static bool TryReadForget(NetDataReader reader, int[] ids, out long tick, out uint revision, out int owner, out int count)
        {
            tick = -1; revision = 0; owner = -1; count = 0;
            if (reader.AvailableBytes < ForgetHeaderBytes || reader.AvailableBytes > Protocol.MaxDatagramBytes || reader.GetByte() != (byte)PacketKind.Forget) return false;
            tick = reader.GetLong(); owner = reader.GetInt(); revision = reader.GetUInt(); count = reader.GetUShort();
            if (tick < 0 || owner <= 0 || revision == 0 || count == 0 || count > ForgetPerChunk || count > ids.Length || reader.AvailableBytes != count * 5) return false;
            int start = reader.Position;
            for (int index = 0; index < count; index++) if (reader.GetByte()!=(byte)ObservedEntityKind.Unit||reader.GetInt() <= 0) return false;
            reader.SetPosition(start);
            for (int index = 0; index < count; index++) {reader.GetByte();ids[index] = reader.GetInt();}
            return true;
        }

        public static bool TryReadEntityForget(NetDataReader reader,ObservedEntityId[] ids,out long tick,out uint revision,out int owner,out int count)
        {
            tick=-1;revision=0;owner=-1;count=0;
            if(reader.AvailableBytes<ForgetHeaderBytes||reader.AvailableBytes>Protocol.MaxDatagramBytes||reader.GetByte()!=(byte)PacketKind.Forget)return false;
            tick=reader.GetLong();owner=reader.GetInt();revision=reader.GetUInt();count=reader.GetUShort();
            if(tick<0||owner<=0||revision==0||count<=0||count>ForgetPerChunk||count>ids.Length||reader.AvailableBytes!=count*5)return false;
            int start=reader.Position;
            for(int index=0;index<count;index++){int kind=reader.GetByte(),id=reader.GetInt();if(kind>(int)ObservedEntityKind.Candidate||id<=0)return false;}
            reader.SetPosition(start);
            for(int index=0;index<count;index++)ids[index]=new ObservedEntityId((ObservedEntityKind)reader.GetByte(),reader.GetInt());
            return true;
        }
    }
}
