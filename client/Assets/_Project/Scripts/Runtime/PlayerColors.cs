using System.Collections.Generic;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using UnityEngine;

namespace ElementalLegion.Client
{
    /// <summary>공유 소유 팔레트를 Unity 표시 색상으로 변환합니다.</summary>
    internal static class PlayerColors
    {
        internal static readonly Color Neutral = new Color32(164, 174, 183, 255);

        internal static Color Get(int colorId)
        {
            if (!PlayerPalette.IsValid(colorId)) return Neutral;
            uint rgb = PlayerPalette.GetRgb(colorId);
            return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        }
    }

    /// <summary>이미 허용된 소유 색만 기억하며 공간 정보는 보관하지 않습니다.</summary>
    internal sealed class PlayerColorCache
    {
        private readonly Dictionary<int, int> colors = new Dictionary<int, int>();

        internal void Observe(GameplayFrame frame)
        {
            for (int index = 0; index < frame.OwnerColors.Count; index++)
            {
                var entry = frame.OwnerColors[index];
                if (entry.PlayerId > 0 && PlayerPalette.IsValid(entry.ColorId)) colors[entry.PlayerId] = entry.ColorId;
            }
        }

        internal int Slot(int owner) => owner > 0 && colors.TryGetValue(owner, out int color) ? color : PlayerPalette.Count;
        internal Color Get(int owner) => PlayerColors.Get(Slot(owner));
        internal void Clear() => colors.Clear();
    }
}
