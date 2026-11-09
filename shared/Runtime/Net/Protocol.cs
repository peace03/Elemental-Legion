using System;
using System.Numerics;
using System.Text;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>길이·범위 검증이 끝난 바이너리 게임 메시지만 호출자에게 반환한다.</summary>
    public static class Protocol
    {
        public const ushort Version = 20;
        public const int AccountKeyBytes = 32;
        public const int MaxUnits = 4096;
        public const int UnitsPerChunk = (MaxDatagramBytes-UnitHeaderBytes)/UnitBytes;
        public const int MaxUnitChunks = (MaxUnits+UnitsPerChunk-1)/UnitsPerChunk;
        public const int MaxDatagramBytes = 1024;
        private const int UnitBytes = 29;
        private const int UnitHeaderBytes = 29;
        private const int CommandHeaderBytes = 9;
        private const int BuildBodyBytes = 10;
        private const int HelloPrefixBytes = 36;
        private const int MaxNicknameBytes = PlayerNickname.MaxLength * 3;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        internal const byte NoTowerElement = byte.MaxValue;

        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Finite(Vector2 value) => Finite(value.X) && Finite(value.Y);
        public static int ChunkCount(int count) => Math.Max(1, (count + UnitsPerChunk - 1) / UnitsPerChunk);

        public static void WriteHello(NetDataWriter writer, string hash, int element, string nickname = PlayerNickname.Default,
            byte[] resumeKey = null, byte[] hostProof = null, bool requestAccount = false)
        {
            if (hash == null || hash.Length != 64 || element < 0 || element > byte.MaxValue) throw new ArgumentException("접속 자료가 잘못됐습니다.");
            if (!PlayerNickname.IsValid(nickname)) throw new ArgumentException("닉네임이 잘못됐습니다.", nameof(nickname));
            writer.Reset(); writer.Put((byte)PacketKind.Hello); writer.Put(Version);
            for (int index = 0; index < 32; index++) writer.Put(Convert.ToByte(hash.Substring(index * 2, 2), 16));
            writer.Put((byte)element);
            writer.Put(nickname);
            if (resumeKey != null && resumeKey.Length != AccountKeyBytes || hostProof != null && hostProof.Length != AccountKeyBytes)
                throw new ArgumentException("접속 키 길이가 잘못됐습니다.");
            byte flags = (byte)((resumeKey != null ? 1 : 0) | (hostProof != null ? 2 : 0) | (requestAccount ? 4 : 0));
            if (flags == 0) return;
            writer.Put(flags);
            if (resumeKey != null) writer.Put(resumeKey);
            if (hostProof != null) writer.Put(hostProof);
        }

        public static JoinFailure ReadHello(NetDataReader reader, GameRules rules, out int element) =>
            ReadHello(reader, rules, out element, out _);

        public static JoinFailure ReadHello(NetDataReader reader, GameRules rules, out int element, out string nickname)
            => ReadHello(reader, rules, out element, out nickname, out _, out _, out _);

        public static JoinFailure ReadHello(NetDataReader reader, GameRules rules, out int element, out string nickname,
            out byte[] resumeKey, out byte[] hostProof, out bool requestAccount)
        {
            element = -1;
            nickname = string.Empty;
            resumeKey = hostProof = null;
            requestAccount = false;
            if (reader.AvailableBytes < 3 || reader.GetByte() != (byte)PacketKind.Hello) return JoinFailure.Malformed;
            if (reader.GetUShort() != Version) return JoinFailure.VersionMismatch;
            if (reader.AvailableBytes < HelloPrefixBytes - 3 || reader.AvailableBytes > HelloPrefixBytes - 3 + 3 + MaxNicknameBytes + 2 * AccountKeyBytes)
                return JoinFailure.Malformed;
            bool same = true;
            for (int index = 0; index < 32; index++) same &= reader.GetByte() == Convert.ToByte(rules.DataHash.Substring(index * 2, 2), 16);
            if (!same) return JoinFailure.DataMismatch;
            element = reader.GetByte();
            if (!rules.StartingElements.Contains(element)) return JoinFailure.InvalidElement;
            if (!TryReadNickname(reader, out nickname)) return JoinFailure.InvalidNickname;
            if (reader.AvailableBytes == 0) return JoinFailure.None;
            byte flags = reader.GetByte();
            if (flags == 0 || flags > 7 || reader.AvailableBytes != ((flags & 1) + ((flags >> 1) & 1)) * AccountKeyBytes)
                return JoinFailure.Malformed;
            if ((flags & 1) != 0) resumeKey = ReadAccountKey(reader);
            if ((flags & 2) != 0) hostProof = ReadAccountKey(reader);
            requestAccount = (flags & 4) != 0;
            return JoinFailure.None;
        }

        public static void WriteWelcome(NetDataWriter writer, int owner, bool resumed = false, byte[] key = null, int roomPlayers = 0)
        {
            writer.Reset(); writer.Put((byte)PacketKind.Welcome); writer.Put(owner);
            writer.Put(checked((byte)roomPlayers));
            if (key == null && !resumed) return;
            if (key != null && key.Length != AccountKeyBytes) throw new ArgumentException("복귀 키 길이가 잘못됐습니다.");
            writer.Put((byte)(resumed ? 1 : 0));
            writer.Put((byte)(key?.Length ?? 0));
            if (key != null) writer.Put(key);
        }

        public static bool TryReadWelcome(NetDataReader reader, out int owner, out bool resumed, out byte[] key)
            => TryReadWelcome(reader, out owner, out resumed, out key, out _);

        public static bool TryReadWelcome(NetDataReader reader, out int owner, out bool resumed, out byte[] key, out int roomPlayers)
        {
            owner = 0; resumed = false; key = null; roomPlayers = 0;
            if (reader.AvailableBytes < 6 || reader.GetByte() != (byte)PacketKind.Welcome) return false;
            owner = reader.GetInt();
            roomPlayers = reader.GetByte();
            if (owner <= 0 || roomPlayers == 1 || roomPlayers > 63) return false;
            if (reader.AvailableBytes == 0) return true;
            if (reader.AvailableBytes < 2) return false;
            byte mode = reader.GetByte(), count = reader.GetByte();
            if (mode > 1 || count != 0 && count != AccountKeyBytes || reader.AvailableBytes != count) return false;
            resumed = mode == 1;
            if (count != 0) key = ReadAccountKey(reader);
            return true;
        }

        private static byte[] ReadAccountKey(NetDataReader reader)
        {
            var key = new byte[AccountKeyBytes];
            Buffer.BlockCopy(reader.RawData, reader.Position, key, 0, key.Length);
            reader.SkipBytes(key.Length);
            return key;
        }

        internal static bool TryReadNickname(NetDataReader reader, out string nickname)
        {
            nickname = string.Empty;
            if (reader.AvailableBytes < sizeof(ushort)) return false;
            // LiteNetLib의 문자열 길이에는 비어 있는 문자열과 구분하는 1이 더해진다.
            int bytes = reader.GetUShort() - 1;
            if (bytes < 1 || bytes > MaxNicknameBytes || bytes > reader.AvailableBytes) return false;
            try { nickname = StrictUtf8.GetString(reader.RawData, reader.Position, bytes); }
            catch (DecoderFallbackException) { return false; }
            reader.SkipBytes(bytes);
            return PlayerNickname.IsValid(nickname);
        }

        public static void WriteMove(NetDataWriter writer, ulong sequence, Vector2 direction) => WriteMove(writer, sequence, 1, direction);
        public static void WriteMove(NetDataWriter writer, ulong sequence, byte squadMask, Vector2 direction)
        { writer.Reset(); writer.Put((byte)PacketKind.Move); writer.Put(sequence); writer.Put(squadMask); PutVector(writer, direction); }
        public static void WriteMoveTo(NetDataWriter writer, ulong sequence, byte squadMask, Vector2 destination)
        { writer.Reset(); writer.Put((byte)PacketKind.MoveTo); writer.Put(sequence); writer.Put(squadMask); PutVector(writer, destination); }
        public static void WriteTransfer(NetDataWriter writer, ulong sequence, byte fromSquad, byte toSquad, int count)
        { writer.Reset(); writer.Put((byte)PacketKind.Transfer); writer.Put(sequence); writer.Put(fromSquad); writer.Put(toSquad); writer.Put(count); }
        public static void WriteFocus(NetDataWriter writer, ulong sequence, byte anchorSquad)
        { writer.Reset(); writer.Put((byte)PacketKind.Focus); writer.Put(sequence); writer.Put(anchorSquad); }
        public static void WriteRecall(NetDataWriter writer,ulong sequence,byte squad)
        {writer.Reset();writer.Put((byte)PacketKind.Recall);writer.Put(sequence);writer.Put(squad);}

        public static bool TryReadMove(NetDataReader reader, out ulong sequence, out Vector2 direction)
            => TryReadMove(reader, out sequence, out _, out direction);
        public static bool TryReadMove(NetDataReader reader, out ulong sequence, out byte squadMask, out Vector2 direction)
        {
            sequence = 0; squadMask = 0; direction = default;
            if (reader.AvailableBytes != 18 || reader.GetByte() != (byte)PacketKind.Move) return false;
            sequence = reader.GetULong(); squadMask = reader.GetByte(); direction = GetVector(reader);
            return sequence != 0 && ValidSquadMask(squadMask) && Finite(direction);
        }

        public static void WriteTrain(NetDataWriter writer,ulong sequence,int element)
            => WriteTrain(writer,sequence,element,1,4);
        public static void WriteTrain(NetDataWriter writer,ulong sequence,int element,int count,byte targetSquad)
        {
            if(element<0||element>byte.MaxValue)throw new ArgumentOutOfRangeException(nameof(element));
            if(count<1||count>byte.MaxValue)throw new ArgumentOutOfRangeException(nameof(count));
            writer.Reset();writer.Put((byte)PacketKind.Train);writer.Put(sequence);writer.Put((byte)element);writer.Put((byte)count);writer.Put(targetSquad);
        }
        public static void WriteCancelTraining(NetDataWriter writer, ulong sequence, ulong orderId)
        {
            writer.Reset();
            writer.Put((byte)PacketKind.CancelTraining);
            writer.Put(sequence);
            writer.Put(orderId);
        }
        public static void WriteRally(NetDataWriter writer,ulong sequence) => WriteRally(writer,sequence,1,0);
        public static void WriteRally(NetDataWriter writer,ulong sequence,byte targetSquad,int count)
        {writer.Reset();writer.Put((byte)PacketKind.Rally);writer.Put(sequence);writer.Put(targetSquad);writer.Put(count);}
        public static void WriteBuild(NetDataWriter writer, ulong sequence, BuildingKind kind, int cellX, int cellZ, int towerElement = -1)
        {
            if (kind < BuildingKind.NatureForge || kind > BuildingKind.Dome) throw new ArgumentOutOfRangeException(nameof(kind));
            if (towerElement < -1 || towerElement >= NoTowerElement) throw new ArgumentOutOfRangeException(nameof(towerElement));
            writer.Reset();
            writer.Put((byte)PacketKind.Build);
            writer.Put(sequence);
            writer.Put((byte)kind);
            writer.Put(cellX);
            writer.Put(cellZ);
            writer.Put(towerElement < 0 ? NoTowerElement : (byte)towerElement);
        }
        public static void WriteUpgradeBase(NetDataWriter writer, ulong sequence)
        {
            writer.Reset();
            writer.Put((byte)PacketKind.UpgradeBase);
            writer.Put(sequence);
        }
        public static bool TryReadCommand(NetDataReader reader,GameRules rules,out SimCommand command)
        {
            command=default;if(reader.AvailableBytes<1)return false;
            if(reader.PeekByte()==(byte)PacketKind.Move)
            {if(!TryReadMove(reader,out ulong move,out byte mask,out Vector2 direction))return false;command=SimCommand.Move(move,mask,direction);return true;}
            if(reader.AvailableBytes<CommandHeaderBytes)return false;
            PacketKind kind=(PacketKind)reader.GetByte();ulong sequence=reader.GetULong();if(sequence==0)return false;
            if(kind==PacketKind.Rally&&reader.AvailableBytes==5)
            {
                byte target=reader.GetByte();int count=reader.GetInt();
                if(target<1||target>3||count<0||count>rules.MaxPopulation)return false;
                command=SimCommand.Rally(sequence,target,count);return true;
            }
            if(kind==PacketKind.MoveTo&&reader.AvailableBytes==9)
            {
                byte mask=reader.GetByte();Vector2 destination=GetVector(reader);
                if(!ValidSquadMask(mask)||!InWorld(destination,rules.MapSize))return false;
                command=SimCommand.MoveTo(sequence,mask,destination);return true;
            }
            if(kind==PacketKind.Transfer&&reader.AvailableBytes==6)
            {
                byte from=reader.GetByte(),to=reader.GetByte();int count=reader.GetInt();
                if(from<1||from>4||to<1||to>4||from==to||count<1||count>rules.MaxPopulation)return false;
                command=SimCommand.Transfer(sequence,from,to,count);return true;
            }
            if(kind==PacketKind.Focus&&reader.AvailableBytes==1)
            {
                byte anchor=reader.GetByte();if(anchor<1||anchor>4)return false;
                command=SimCommand.Focus(sequence,anchor);return true;
            }
            if(kind==PacketKind.Recall&&reader.AvailableBytes==1)
            {
                byte squad=reader.GetByte();if(squad<1||squad>3)return false;
                command=SimCommand.Recall(sequence,squad);return true;
            }
            if(kind==PacketKind.UpgradeBase&&reader.AvailableBytes==0){command=SimCommand.UpgradeBase(sequence);return true;}
            if (kind == PacketKind.CancelTraining && reader.AvailableBytes == sizeof(ulong))
            {
                ulong orderId = reader.GetULong();
                if (orderId == 0) return false;
                command = SimCommand.CancelTraining(sequence, orderId);
                return true;
            }
            if (kind == PacketKind.Build && reader.AvailableBytes == BuildBodyBytes)
            {
                BuildingKind building = (BuildingKind)reader.GetByte();
                int x = reader.GetInt(), z = reader.GetInt();
                int towerElement = reader.GetByte();
                if (building < BuildingKind.NatureForge || building > BuildingKind.Dome ||
                    x < 0 || z < 0 || x * (double)rules.BuildGridSize > rules.MapSize || z * (double)rules.BuildGridSize > rules.MapSize ||
                    building == BuildingKind.Dome && (x != 0 || z != 0)) return false;
                if (towerElement == NoTowerElement) towerElement = -1;
                if (!ConstructionSystem.ValidTowerElement(rules, building, towerElement)) return false;
                command = SimCommand.Build(sequence, building, x, z, towerElement);
                return true;
            }
            if(kind!=PacketKind.Train||reader.AvailableBytes!=3)return false;
            int element=reader.GetByte();if(element>=rules.Elements.Count)return false;
            int trainCount=reader.GetByte();byte trainingTarget=reader.GetByte();
            if(trainCount<1||trainCount>rules.MaxTrainingBatchCount||trainingTarget<1||trainingTarget>4)return false;
            command=SimCommand.Train(sequence,rules.Elements[element].Id,trainCount,trainingTarget);return true;
        }

        public static void WritePose(NetDataWriter writer, WorldView view)
        {
            writer.Reset(); writer.Put((byte)PacketKind.Pose); writer.Put(view.Tick); writer.Put(view.PlayerId);
            writer.Put((byte)view.Squads.Count);
            foreach(SquadView squad in view.Squads)
                PutPose(writer,new LegionPose(view.Tick,view.PlayerId,squad.AppliedSequence,squad.Position,squad.Direction,squad.Id,squad.MovementRevision));
        }

        public static void WritePose(NetDataWriter writer, LegionPose pose)
        {
            writer.Reset();writer.Put((byte)PacketKind.Pose);writer.Put(pose.Tick);writer.Put(pose.PlayerId);writer.Put((byte)1);PutPose(writer,pose);
        }

        private static void PutPose(NetDataWriter writer, LegionPose pose)
        {writer.Put(pose.SquadId);writer.Put(pose.AppliedSequence);PutVector(writer,pose.Position);PutVector(writer,pose.Direction);writer.Put(pose.MovementRevision);}

        public static bool TryReadPose(NetDataReader reader, GameRules rules, out LegionPose pose)
        {
            pose = default;
            if(reader.AvailableBytes<43||reader.AvailableBytes>130||reader.GetByte()!=(byte)PacketKind.Pose)return false;
            long tick=reader.GetLong();int player=reader.GetInt(),count=reader.GetByte();
            if(count<1||count>4||reader.AvailableBytes!=count*29)return false;
            LegionPose first=default;int mask=0;
            for(int index=0;index<count;index++)
            {
                if(!ReadPose(reader,rules,tick,player,out LegionPose value)||(mask&(1<<value.SquadId))!=0)return false;
                mask|=1<<value.SquadId;if(index==0)first=value;
            }
            pose=first;return true;
        }

        public static bool TryReadPoses(NetDataReader reader, GameRules rules, LegionPose[] target, out int count)
        {
            count=0;
            if(target==null||target.Length<4||reader.AvailableBytes<14||reader.AvailableBytes>130||reader.GetByte()!=(byte)PacketKind.Pose)return false;
            long tick=reader.GetLong();int player=reader.GetInt(),records=reader.GetByte();
            if(records<1||records>4||reader.AvailableBytes!=records*29)return false;
            int start=reader.Position,mask=0;
            for(int pass=0;pass<2;pass++)
            {
                reader.SetPosition(start);mask=0;
                for(int index=0;index<records;index++)
                {
                    if(!ReadPose(reader,rules,tick,player,out LegionPose pose)||(mask&(1<<pose.SquadId))!=0)return false;
                    mask|=1<<pose.SquadId;if(pass==1)target[index]=pose;
                }
            }
            count=records;return true;
        }

        private static bool ReadPose(NetDataReader reader,GameRules rules,long tick,int player,out LegionPose pose)
        {
            pose=default;byte squad=reader.GetByte();ulong sequence=reader.GetULong();Vector2 position=GetVector(reader),direction=GetVector(reader);uint movement=reader.GetUInt();
            if(tick<0||player<=0||squad<1||squad>4||!InWorld(position,rules.MapSize)||!Finite(direction)||direction.LengthSquared()>1.001f)return false;
            pose=new LegionPose(tick,player,sequence,position,direction,squad,movement);return true;
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
                writer.Put(Quantize(relative.X, mapSize)); writer.Put(Quantize(relative.Y, mapSize)); writer.Put((byte)((unit.Garrison ? 1 : 0)|(unit.Converted?2:0)|(unit.Ghost?4:0)|(unit.IsReinforcing?8:0)));
                writer.Put(unit.Health);writer.Put(unit.Shield);writer.Put(unit.StatusMask);
                writer.Put(unit.SquadId);writer.Put(unit.MovementRevision);
            }
        }

        internal static bool ReadUnitChunk(NetDataReader reader, GameRules rules, NetworkUnit[] target, out long tick, out int player, out Vector2 anchor, out int total, out int part, out uint revision)
        {
            tick = -1; player = -1; anchor = default; total = 0; part = 0; revision = 0;
            if (reader.AvailableBytes < UnitHeaderBytes || reader.AvailableBytes > MaxDatagramBytes || reader.GetByte() != (byte)PacketKind.Units) return false;
            tick = reader.GetLong(); player = reader.GetInt(); anchor = GetVector(reader); total = reader.GetUShort(); part = reader.GetUShort();
            revision = reader.GetUInt();
            if (tick < 0 || player <= 0 || !InWorld(anchor, rules.MapSize) || total > MaxUnits || part >= ChunkCount(total)) return false;
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
                    byte squad=reader.GetByte();uint movement=reader.GetUInt();
                    float tolerance = rules.MapSize / short.MaxValue;
                    if (id <= 0 || owner < -1 || owner == 0 || owner == -1 && ((flags & 13) != 0 || squad != 0) ||
                        owner > 0 && (squad > 4 || (flags&8)==0 && (squad<1 || ((flags&1)!=0)!=(squad==4))) ||
                        element >= rules.Elements.Count || flags > 15 ||!Finite(health)||health<=0||health>rules.Elements[element].Hp||!Finite(shield)||shield<0||status>1023||point.X < -tolerance || point.Y < -tolerance || point.X > rules.MapSize + tolerance || point.Y > rules.MapSize + tolerance) return false;
                    if (pass == 1) target[start + offset] = new NetworkUnit(id, owner, element, Vector2.Clamp(point, Vector2.Zero, new Vector2(rules.MapSize)), (flags&1)!=0,health,shield,(flags&2)!=0,status,(flags&4)!=0,squad,(flags&8)!=0,movement);
                }
            }
            return true;
        }

        private static bool ValidSquadMask(byte mask) => mask>0&&mask<=15;
        private static bool InWorld(Vector2 value, float map) => Finite(value) && value.X >= 0 && value.Y >= 0 && value.X <= map && value.Y <= map;
        private static short Quantize(float relative, float mapSize) => (short)Math.Round(Math.Max(-short.MaxValue, Math.Min(short.MaxValue, relative / mapSize * short.MaxValue)));
        private static void PutVector(NetDataWriter writer, Vector2 value) { writer.Put(value.X); writer.Put(value.Y); }
        private static Vector2 GetVector(NetDataReader reader) => new Vector2(reader.GetFloat(), reader.GetFloat());
    }
}
