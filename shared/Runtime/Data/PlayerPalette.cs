using System;

namespace ElementalLegion.Shared.Data
{
    /// <summary>소유자 구별에 사용하는 고정 15색 팔레트입니다. 원소 자체의 색과 분리합니다.</summary>
    public static class PlayerPalette
    {
        public const int Count = 15;
        // Colorcet glasbey_light 앞 15색의 어두운 색만 밝게 보정했습니다. 고지는 ColorcetNotice.txt에 보존합니다.
        private static readonly uint[] Colors =
        {
            0xDF3636u, 0x088B06u, 0xBC16FFu, 0x06ACC6u, 0x98FF00u,
            0xFFA530u, 0xFF8FC8u, 0x8F6F79u, 0x00FECFu, 0xB0A5FFu,
            0x94AD84u, 0x9E6F0Au, 0x548079u, 0xD9259Du, 0xFEF590u
        };

        public static bool IsValid(int colorId) => colorId >= 0 && colorId < Count;

        public static uint GetRgb(int colorId)
        {
            if (!IsValid(colorId)) throw new ArgumentOutOfRangeException(nameof(colorId));
            return Colors[colorId];
        }
    }
}
