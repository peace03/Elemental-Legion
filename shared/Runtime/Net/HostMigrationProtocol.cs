using System;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    public enum MigrationPhase : byte { None, Preparing, Recovering, Resyncing, Failed }

    /// <summary>선정된 참가자에게만 전달하는 예비 서버 실행 권한이다.</summary>
    public readonly struct HostStandbyOffer
    {
        public string RoomId { get; }
        public string Key { get; }
        public ulong Generation { get; }
        public HostStandbyOffer(string roomId, string key, ulong generation)
        { RoomId = roomId; Key = key; Generation = generation; }
    }

    /// <summary>중계가 확정한 서버 세대와 클라이언트 동기화 단계다.</summary>
    public readonly struct HostMigrationNotice
    {
        public MigrationPhase Phase { get; }
        public ulong Generation { get; }
        public long Tick { get; }
        public bool IsHost { get; }
        public string Reason { get; }
        /// <summary>중계가 확인한 백업의 나이(ms)다. 측정하지 못했으면 -1이다.</summary>
        public int BackupAgeMs { get; }
        public HostMigrationNotice(MigrationPhase phase, ulong generation, long tick, bool isHost, string reason, int backupAgeMs = -1)
        { Phase = phase; Generation = generation; Tick = tick; IsHost = isHost; Reason = reason; BackupAgeMs = backupAgeMs; }
    }

    /// <summary>월드 백업을 포함하지 않는 작은 승계 제어 메시지를 검증한다.</summary>
    public static class HostMigrationProtocol
    {
        public static void WriteCapability(NetDataWriter writer, bool capable)
        { writer.Reset(); writer.Put((byte)PacketKind.HostCapability); writer.Put((byte)(capable ? 1 : 0)); }

        public static bool TryReadCapability(NetDataReader reader, out bool capable)
        {
            capable = false;
            if (reader.AvailableBytes != 2 || reader.GetByte() != (byte)PacketKind.HostCapability) return false;
            byte value = reader.GetByte(); capable = value == 1; return value <= 1;
        }

        public static void WriteLeave(NetDataWriter writer)
        { writer.Reset(); writer.Put((byte)PacketKind.HostLeave); }

        public static bool TryReadLeave(NetDataReader reader)
            => reader.AvailableBytes == 1 && reader.GetByte() == (byte)PacketKind.HostLeave;

        public static void WriteStandby(NetDataWriter writer, string roomId, string key, ulong generation)
        {
            if (!Hex(roomId, 32) || !Hex(key, 64) || generation < 2)
                throw new ArgumentException("예비 서버 실행 권한이 잘못됐습니다.");
            writer.Reset(); writer.Put((byte)PacketKind.HostStandbyOffer);
            writer.Put(roomId); writer.Put(key); writer.Put(generation);
        }

        public static bool TryReadStandby(NetDataReader reader, out HostStandbyOffer offer)
        {
            offer = default;
            try
            {
                if (reader.AvailableBytes < 1 || reader.GetByte() != (byte)PacketKind.HostStandbyOffer) return false;
                string room = reader.GetString(32), key = reader.GetString(64);
                if (reader.AvailableBytes != 8 || !Hex(room, 32) || !Hex(key, 64)) return false;
                ulong generation = reader.GetULong();
                if (generation < 2) return false;
                offer = new HostStandbyOffer(room, key, generation); return true;
            }
            catch (Exception error) when (error is ArgumentException || error is IndexOutOfRangeException || error is InvalidOperationException)
            { return false; }
        }

        public static void WriteMigration(NetDataWriter writer, MigrationPhase phase, ulong generation, long tick, bool isHost, string reason = "", int backupAgeMs = -1)
        {
            if (phase < MigrationPhase.None || phase > MigrationPhase.Failed || tick < 0 || backupAgeMs < -1 || !ReasonValid(reason))
                throw new ArgumentException("서버 승계 상태가 잘못됐습니다.");
            writer.Reset(); writer.Put((byte)PacketKind.HostMigration); writer.Put((byte)phase);
            writer.Put(generation); writer.Put(tick); writer.Put((byte)(isHost ? 1 : 0)); writer.Put(backupAgeMs); writer.Put(reason);
        }

        public static bool TryReadMigration(NetDataReader reader, out HostMigrationNotice state)
        {
            state = default;
            try
            {
                if (reader.AvailableBytes < 25 || reader.GetByte() != (byte)PacketKind.HostMigration) return false;
                byte phase = reader.GetByte(); ulong generation = reader.GetULong(); long tick = reader.GetLong(); byte host = reader.GetByte();
                int backupAgeMs = reader.GetInt();
                string reason = reader.GetString(64);
                if (phase > (byte)MigrationPhase.Failed || host > 1 || tick < 0 || backupAgeMs < -1 || !ReasonValid(reason) || reader.AvailableBytes != 0) return false;
                state = new HostMigrationNotice((MigrationPhase)phase, generation, tick, host == 1, reason, backupAgeMs); return true;
            }
            catch (Exception error) when (error is ArgumentException || error is IndexOutOfRangeException || error is InvalidOperationException)
            { return false; }
        }

        private static bool ReasonValid(string value)
        {
            if (value == null || value.Length > 64) return false;
            foreach (char character in value) if (character < 32 || character > 126) return false;
            return true;
        }

        private static bool Hex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char character in value)
                if (!(character >= '0' && character <= '9' || character >= 'a' && character <= 'f' || character >= 'A' && character <= 'F')) return false;
            return true;
        }
    }
}
