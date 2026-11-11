using System;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>한 연결의 시야 청크를 원자 적용한다. 이전 기준·부분 조각은 현재 표시 자료를 변경하지 않는다.</summary>
    public sealed class FogStateReplica
    {
        private readonly byte[] current, explored, scratch;
        private readonly bool[] received;
        private readonly int[] clearedBuildingMemories = new int[FogProtocol.MaxRememberedBuildings];
        private uint assembling;
        private int assemblingOwner, totalBytes, receivedCount;
        private long assemblingTick;
        private PacketKind assemblingKind;
        private int expectedOwner = -1;
        private int clearedBuildingMemoryCount;
        private readonly NetDataReader payloadReader = new NetDataReader();
        public int Width { get; }
        public int CellCount { get; }
        public float CellSize { get; }
        public ReadOnlySpan<byte> Current => current;
        public ReadOnlySpan<byte> Explored => explored;
        /// <summary>현재 revision에서 제거할 이전 적 시설 표시 ID다.</summary>
        public ReadOnlySpan<int> ClearedBuildingMemories => clearedBuildingMemories.AsSpan(0, clearedBuildingMemoryCount);
        public uint Revision { get; private set; }
        public int PlayerId { get; private set; } = -1;
        public long Tick { get; private set; } = -1;
        public bool Ready => Revision > 0;

        public FogStateReplica(GameRules rules)
        {
            CellSize = rules.CellSize; Width = checked((int)Math.Ceiling(rules.MapSize / rules.CellSize)); CellCount = checked(Width * Width);
            if (CellCount > FogProtocol.MaxCells) throw new ArgumentException("시야 격자가 기술 상한을 넘었습니다.", nameof(rules));
            current = new byte[(CellCount + 7) / 8]; explored = new byte[current.Length];
            scratch = new byte[checked(CellCount * FogProtocol.DeltaCellBytes + FogProtocol.DeltaFixedBytes + clearedBuildingMemories.Length * sizeof(int))];
            received = new bool[(scratch.Length + FogProtocol.PayloadBytes - 1) / FogProtocol.PayloadBytes];
        }

        public bool BindPlayer(int playerId)
        {
            if (playerId <= 0 || Ready && PlayerId != playerId || expectedOwner > 0 && expectedOwner != playerId) return false;
            expectedOwner = playerId; return true;
        }

        public VisionCellState GetState(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Width) throw new ArgumentOutOfRangeException(nameof(x));
            int cell = x + y * Width, mask = 1 << (cell & 7);
            return (current[cell >> 3] & mask) != 0 ? VisionCellState.Visible : (explored[cell >> 3] & mask) != 0 ? VisionCellState.Explored : VisionCellState.Unexplored;
        }

        public void Reset()
        {
            Array.Clear(current, 0, current.Length); Array.Clear(explored, 0, explored.Length);
            Array.Clear(received, 0, received.Length); assembling = 0; Revision = 0; PlayerId = -1; expectedOwner = -1; Tick = -1; receivedCount = 0;
            clearedBuildingMemoryCount = 0;
        }

        public bool TryAccept(NetDataReader reader)
        {
            if (!FogProtocol.ReadHeader(reader, scratch.Length, out PacketKind kind, out long tick, out int owner, out uint revision, out uint baseline, out int total, out int offset)) return false;
            if (expectedOwner > 0 && owner != expectedOwner || Ready && owner != PlayerId || tick < Tick || revision <= Revision || revision < assembling) return false;
            if (kind == PacketKind.FogBaseline ? Ready || baseline != 0 || total != current.Length * 2 : !Ready || baseline != Revision || revision != Revision + 1 || total < FogProtocol.DeltaFixedBytes) return false;
            if (assembling != revision)
            {
                assembling = revision; assemblingOwner = owner; assemblingTick = tick; assemblingKind = kind; totalBytes = total;
                Array.Clear(received, 0, received.Length); receivedCount = 0;
            }
            if (owner != assemblingOwner || tick != assemblingTick || kind != assemblingKind || total != totalBytes) return false;
            int part = offset / FogProtocol.PayloadBytes, length = reader.AvailableBytes;
            for (int index = 0; index < length; index++)
            {
                byte value = reader.GetByte();
                if (received[part]) { if (scratch[offset + index] != value) return false; }
                else scratch[offset + index] = value;
            }
            if (!received[part]) { received[part] = true; receivedCount++; }
            if (receivedCount != (total + FogProtocol.PayloadBytes - 1) / FogProtocol.PayloadBytes) return false;
            if (!ValidateAndApply(kind, total)) { assembling = 0; return false; }
            Revision = revision; PlayerId = owner; Tick = tick;
            return true;
        }

        private bool ValidateAndApply(PacketKind kind, int total)
        {
            if (kind == PacketKind.FogBaseline)
            {
                for (int index = 0; index < current.Length; index++) if ((scratch[index] & ~scratch[index + current.Length]) != 0) return false;
                int unused = current.Length * 8 - CellCount;
                if (unused > 0 && ((scratch[current.Length - 1] | scratch[current.Length * 2 - 1]) & (255 << (8 - unused))) != 0) return false;
                Array.Copy(scratch, current, current.Length); Array.Copy(scratch, current.Length, explored, 0, explored.Length);
                return true;
            }
            NetDataReader reader = payloadReader; reader.SetSource(scratch, 0, total);
            int count = reader.GetInt();
            if (count < 0 || count > CellCount || count > (total - FogProtocol.DeltaFixedBytes) / FogProtocol.DeltaCellBytes) return false;
            int previous = -1;
            for (int index = 0; index < count; index++)
            {
                int cell = reader.GetInt(); byte flags = reader.GetByte();
                if (cell <= previous || cell >= CellCount || flags > 3 || flags == 1 || (explored[cell >> 3] & (1 << (cell & 7))) != 0 && (flags & 2) == 0) return false;
                previous = cell;
            }
            int clearCount = reader.GetUShort();
            if (clearCount > clearedBuildingMemories.Length || reader.AvailableBytes != clearCount * sizeof(int)) return false;
            previous = 0;
            for (int index = 0; index < clearCount; index++)
            {
                int id = reader.GetInt();
                if (id <= previous) return false;
                previous = id;
            }

            reader.SetPosition(sizeof(int));
            for (int index = 0; index < count; index++)
            {
                int cell = reader.GetInt(), flags = reader.GetByte(), mask = 1 << (cell & 7);
                current[cell >> 3] = (byte)((current[cell >> 3] & ~mask) | ((flags & 1) != 0 ? mask : 0));
                explored[cell >> 3] |= (byte)((flags & 2) != 0 ? mask : 0);
            }
            reader.GetUShort();
            for (int index = 0; index < clearCount; index++) clearedBuildingMemories[index] = reader.GetInt();
            clearedBuildingMemoryCount = clearCount;
            return true;
        }
    }
}
