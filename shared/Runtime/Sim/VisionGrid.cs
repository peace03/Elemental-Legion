using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    public enum VisionCellState : byte { Unexplored, Explored, Visible }

    /// <summary>플레이어별 지형의 현재 시야와 누적 탐색이다. 외부에는 읽기만 허용하며 유닛 판정에 사용하지 않는다.</summary>
    public sealed class VisionGrid
    {
        private readonly byte[] current, explored;
        private readonly float mapSize;
        public int Width { get; }
        public int CellCount { get; }
        public float CellSize { get; }
        public ReadOnlySpan<byte> Current => current;
        public ReadOnlySpan<byte> Explored => explored;

        internal VisionGrid(float mapSize, float cellSize)
        {
            this.mapSize = mapSize; CellSize = cellSize;
            Width = checked((int)Math.Ceiling(mapSize / cellSize));
            CellCount = checked(Width * Width);
            current = new byte[(CellCount + 7) / 8]; explored = new byte[current.Length];
        }

        public VisionCellState GetState(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Width) throw new ArgumentOutOfRangeException(nameof(x));
            int index = x + y * Width, mask = 1 << (index & 7);
            return (current[index >> 3] & mask) != 0 ? VisionCellState.Visible : (explored[index >> 3] & mask) != 0 ? VisionCellState.Explored : VisionCellState.Unexplored;
        }

        internal void Begin() => Array.Clear(current, 0, current.Length);

        internal bool TryRestoreExplored(byte[] bits)
        {
            if (bits == null || bits.Length != explored.Length) return false;
            int tail = CellCount & 7;
            if (tail != 0 && (bits[bits.Length - 1] >> tail) != 0) return false;
            bits.CopyTo(explored, 0);
            return true;
        }

        internal void Reveal(Vector2 center, float radius)
        {
            int minX = FirstCell(center.X - radius), maxX = Cell(center.X + radius);
            int minY = FirstCell(center.Y - radius), maxY = Cell(center.Y + radius);
            float squared = radius * radius;
            for (int y = minY; y <= maxY; y++)
            {
                float dy = Math.Max(0, Math.Max(y * CellSize - center.Y, center.Y - Math.Min(mapSize, (y + 1) * CellSize)));
                float remaining = squared - dy * dy;
                for (int x = minX; x <= maxX; x++)
                {
                    int index = x + y * Width, mask = 1 << (index & 7);
                    // 복수 원천이 이미 공개한 칸은 같은 원/AABB 연산을 반복하지 않는다.
                    if ((current[index >> 3] & mask) != 0) continue;
                    float dx = Math.Max(0, Math.Max(x * CellSize - center.X, center.X - Math.Min(mapSize, (x + 1) * CellSize)));
                    if (dx * dx > remaining) continue;
                    current[index >> 3] |= (byte)mask;
                }
            }
        }

        internal void Reveal(CameraSight sight)
        {
            int minX = FirstCell(sight.Min.X), maxX = Cell(sight.Max.X);
            int minY = FirstCell(sight.Min.Y), maxY = Cell(sight.Max.Y);
            for (int y = minY; y <= maxY; y++)
                for (int index = y * Width + minX, end = y * Width + maxX; index <= end; index++)
                    current[index >> 3] |= (byte)(1 << (index & 7));
        }

        internal void Commit()
        { for (int index = 0; index < current.Length; index++) explored[index] |= current[index]; }

        private int Cell(float value) => Math.Max(0, Math.Min(Width - 1, (int)Math.Floor(value / CellSize)));
        private int FirstCell(float value) => Math.Max(0, Math.Min(Width - 1, (int)Math.Ceiling(value / CellSize) - 1));
    }
}
