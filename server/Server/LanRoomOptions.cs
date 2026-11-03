using ElementalLegion.Shared.Net;

namespace ElementalLegion.Server;

/// <summary>LAN 대기실의 표시와 시작 권한을 제한한다.</summary>
public sealed class LanRoomOptions
{
    public string Title { get; }
    public int Minimum { get; }
    public int Capacity { get; }
    internal byte[] HostProof { get; }

    public LanRoomOptions(string title, int minimum, int capacity, int maxPlayers, byte[] hostProof)
    {
        if (!M5Protocol.IsSafeText(title, M5Protocol.TitleLimit) || minimum < 2 || capacity < minimum ||
            capacity > maxPlayers || hostProof.Length != Protocol.AccountKeyBytes)
            throw new ArgumentException("LAN 대기실 설정이 잘못됐습니다.");
        Title = title.Trim(); Minimum = minimum; Capacity = capacity; HostProof = hostProof.ToArray();
    }
}
