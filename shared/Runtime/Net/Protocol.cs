using System;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>버전 3 바이너리 계약이다. 길이·범위 검증이 끝난 데이터만 호출자에게 반환한다.</summary>
    public static class Protocol
    {
        public const ushort Version = 3;
        public const int MaxUnits = 4096;
        public const int UnitsPerChunk = (MaxDatagramBytes-UnitHeaderBytes)/UnitBytes;
        public const int MaxUnitChunks = (MaxUnits+UnitsPerChunk-1)/UnitsPerChunk;
        public const int MaxDatagramBytes = 1024;
        private const int UnitBytes = 24;
        private const int UnitHeaderBytes = 29;

        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Finite(Vector2 value) => Finite(value.X) && Finite(value.Y);
        public static int ChunkCount(int count) => Math.Max(1, (count + UnitsPerChunk - 1) / UnitsPerChunk);

        public static void WriteHello(NetDataWriter writer, string hash, int element)
        {
            if (hash == null || hash.Length != 64 || element < 0 || element > byte.MaxValue) throw new ArgumentException("접속 자료가 잘못됐습니다.");
            writer.Reset(); writer.Put((byte)PacketKind.Hello); writer.Put(Version);
            for (int index = 0; index < 32; index++) writer.Put(Convert.ToByte(hash.Substring(index * 2, 2), 16));
            writer.Put((byte)element);
        }

        public static JoinFailure ReadHello(NetDataReader reader, GameRules rules, out int element)
        {
            element = -1;
            if (reader.AvailableBytes != 36 || reader.GetByte() != (byte)PacketKind.Hello) return JoinFailure.Malformed;
            if (reader.GetUShort() != Version) return JoinFailure.VersionMismatch;
            bool same = true;
            for (int index = 0; index < 32; index++) same &= reader.GetByte() == Convert.ToByte(rules.DataHash.Substring(index * 2, 2), 16);
            if (!same) return JoinFailure.DataMismatch;
            element = reader.GetByte();
            return rules.StartingElements.Contains(element) ? JoinFailure.None : JoinFailure.InvalidElement;
        }

        public static void WriteMove(NetDataWriter writer, ulong sequence, Vector2 direction)
        { writer.Reset(); writer.Put((byte)PacketKind.Move); writer.Put(sequence); PutVector(writer, direction); }

        public static bool TryReadMove(NetDataReader reader, out ulong sequence, out Vector2 direction)
        {
            sequence = 0; direction = default;
            if (reader.AvailableBytes != 17 || reader.GetByte() != (byte)PacketKind.Move) return false;
            sequence = reader.GetULong(); direction = GetVector(reader);
            return sequence != 0 && Finite(direction);
        }

        public static void WriteTrain(NetDataWriter writer,ulong sequence,int element)
        {
            if(element<0||element>byte.MaxValue)throw new ArgumentOutOfRangeException(nameof(element));
            writer.Reset();writer.Put((byte)PacketKind.Train);writer.Put(sequence);writer.Put((byte)element);
        }
        public static void WriteRally(NetDataWriter writer,ulong sequence)
        {writer.Reset();writer.Put((byte)PacketKind.Rally);writer.Put(sequence);}
        public static bool TryReadCommand(NetDataReader reader,GameRules rules,out SimCommand command)
        {
            command=default;if(reader.AvailableBytes<1)return false;
            if(reader.PeekByte()==(byte)PacketKind.Move)
            {if(!TryReadMove(reader,out ulong move,out Vector2 direction))return false;command=SimCommand.Move(move,direction);return true;}
            if(reader.AvailableBytes!=9&&reader.AvailableBytes!=10)return false;
            PacketKind kind=(PacketKind)reader.GetByte();ulong sequence=reader.GetULong();if(sequence==0)return false;
            if(kind==PacketKind.Rally&&reader.AvailableBytes==0){command=SimCommand.Rally(sequence);return true;}
            if(kind!=PacketKind.Train||reader.AvailableBytes!=1)return false;
            int element=reader.GetByte();if(element>=rules.Elements.Count)return false;
            command=SimCommand.Train(sequence,rules.Elements[element].Id);return true;
        }

        public static void WritePose(NetDataWriter writer, WorldView view)
        {
            writer.Reset(); writer.Put((byte)PacketKind.Pose); writer.Put(view.Tick); writer.Put(view.PlayerId);
            writer.Put(view.AppliedSequence); PutVector(writer, view.LegionPosition); PutVector(writer, view.Direction);
        }

        public static bool TryReadPose(NetDataReader reader, GameRules rules, out LegionPose pose)
        {
            pose = default;
            if (reader.AvailableBytes != 37 || reader.GetByte() != (byte)PacketKind.Pose) return false;
            long tick = reader.GetLong(); int player = reader.GetInt(); ulong sequence = reader.GetULong();
            Vector2 position = GetVector(reader), direction = GetVector(reader);
            if (tick < 0 || player < 0 || !InWorld(position, rules.MapSize) || !Finite(direction) || direction.LengthSquared() > 1.001f) return false;
            pose = new LegionPose(tick, player, sequence, position, direction); return true;
        }

        public static void WriteUnits(NetDataWriter writer, WorldView view, float mapSize, int part, uint revision = 0)
        {
            int total = view.Units.Count;
            if (total > MaxUnits || part < 0 || part >= ChunkCount(total)) throw new ArgumentOutOfRangeException(nameof(part));
            writer.Reset(); writer.Put((byte)PacketKind.Units); writer.Put(view.Tick); writer.Put(view.PlayerId);
            PutVector(writer, view.LegionPosition); writer.Put((ushort)total); writer.Put((ushort)part);
            writer.Put(revision);
            int end = Math.Min(total, (part + 1) * UnitsPerChunk);
            for (int index = part * UnitsPerChunk; index < end; index++)
            {
                UnitView unit = view.Units[index]; Vector2 relative = unit.Position - view.LegionPosition;
                writer.Put(unit.Id); writer.Put(unit.OwnerId); writer.Put((byte)unit.Element);
                writer.Put(Quantize(relative.X, mapSize)); writer.Put(Quantize(relative.Y, mapSize)); writer.Put((byte)((unit.Garrison ? 1 : 0)|(unit.Converted?2:0)));
                writer.Put(unit.Health);writer.Put(unit.Shield);writer.Put(unit.StatusMask);
            }
        }

        internal static bool ReadUnitChunk(NetDataReader reader, GameRules rules, NetworkUnit[] target, out long tick, out int player, out Vector2 anchor, out int total, out int part, out uint revision)
        {
            tick = -1; player = -1; anchor = default; total = 0; part = 0; revision = 0;
            if (reader.AvailableBytes < UnitHeaderBytes || reader.AvailableBytes > MaxDatagramBytes || reader.GetByte() != (byte)PacketKind.Units) return false;
            tick = reader.GetLong(); player = reader.GetInt(); anchor = GetVector(reader); total = reader.GetUShort(); part = reader.GetUShort();
            revision = reader.GetUInt();
            if (tick < 0 || player < 0 || !InWorld(anchor, rules.MapSize) || total > MaxUnits || part >= ChunkCount(total)) return false;
            int count = Math.Min(UnitsPerChunk, total - part * UnitsPerChunk);
            if (reader.AvailableBytes != count * UnitBytes) return false;
            int start = part * UnitsPerChunk;
            // 실패한 청크가 진행 중 버퍼를 오염시키지 않도록 먼저 모든 레코드를 검사한다.
            int position = reader.Position;
            for (int pass = 0; pass < 2; pass++)
            {
                reader.SetPosition(position);
                for (int offset = 0; offset < count; offset++)
                {
                    int id = reader.GetInt(), owner = reader.GetInt(), element = reader.GetByte();
                    Vector2 point = anchor + new Vector2(reader.GetShort(), reader.GetShort()) * (rules.MapSize / short.MaxValue);
                    byte flags = reader.GetByte();
                    float health=reader.GetFloat(),shield=reader.GetFloat();ushort status=reader.GetUShort();
                    float tolerance = rules.MapSize / short.MaxValue;
                    if (id <= 0 || owner < -1 || owner>rules.MaxPlayers || element >= rules.Elements.Count || flags > 3 ||!Finite(health)||health<=0||health>rules.Elements[element].Hp||!Finite(shield)||shield<0||status>1023||point.X < -tolerance || point.Y < -tolerance || point.X > rules.MapSize + tolerance || point.Y > rules.MapSize + tolerance) return false;
                    if (pass == 1) target[start + offset] = new NetworkUnit(id, owner, element, Vector2.Clamp(point, Vector2.Zero, new Vector2(rules.MapSize)), (flags&1)!=0,health,shield,(flags&2)!=0,status);
                }
            }
            return true;
        }

        private static bool InWorld(Vector2 value, float map) => Finite(value) && value.X >= 0 && value.Y >= 0 && value.X <= map && value.Y <= map;
        private static short Quantize(float relative, float mapSize) => (short)Math.Round(Math.Max(-short.MaxValue, Math.Min(short.MaxValue, relative / mapSize * short.MaxValue)));
        private static void PutVector(NetDataWriter writer, Vector2 value) { writer.Put(value.X); writer.Put(value.Y); }
        private static Vector2 GetVector(NetDataReader reader) => new Vector2(reader.GetFloat(), reader.GetFloat());
    }
}
