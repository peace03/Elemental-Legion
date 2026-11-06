using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;

namespace ElementalLegion.Server;

/// <summary>사람의 선택을 유지하며 빈 색을 AI에 배정한다.</summary>
internal static class LobbyColors
{
    internal static int[] Restore(IReadOnlyList<int> requested)
    {
        int used = 0;
        var colors = new int[requested.Count];
        for (int index = 0; index < requested.Count; index++)
        {
            int color = requested[index];
            if (color == -1) continue;
            if (!PlayerPalette.IsValid(color) || (used & (1 << color)) != 0)
                throw new InvalidDataException("저장된 소유 색이 잘못됐습니다.");
            used |= 1 << color;
        }
        for (int index = 0; index < requested.Count; index++)
        {
            int color = requested[index];
            if (color == -1)
            {
                color = 0;
                while (color < PlayerPalette.Count && (used & (1 << color)) != 0) color++;
                if (!PlayerPalette.IsValid(color)) throw new InvalidDataException("저장된 소유 색의 수가 상한을 넘었습니다.");
                used |= 1 << color;
            }
            colors[index] = color;
        }
        return colors;
    }

    internal static int FirstAvailable(IEnumerable<int> humans, IEnumerable<int> ai)
    {
        int used = 0;
        foreach (int color in humans.Concat(ai))
            if (PlayerPalette.IsValid(color)) used |= 1 << color;
        for (int color = 0; color < PlayerPalette.Count; color++)
            if ((used & (1 << color)) == 0) return color;
        throw new InvalidOperationException("사용 가능한 소유 색이 없습니다.");
    }

    internal static LobbyError? Select(int requested, IEnumerable<int> otherHumans, List<int> ai)
    {
        if (!PlayerPalette.IsValid(requested)) return LobbyError.InvalidPacket;
        int[] humans = otherHumans.ToArray();
        if (humans.Contains(requested)) return LobbyError.ColorTaken;
        int displaced = ai.IndexOf(requested);
        if (displaced >= 0) ai[displaced] = FirstAvailable(humans.Append(requested), ai.Where((_, index) => index != displaced));
        return null;
    }
}
