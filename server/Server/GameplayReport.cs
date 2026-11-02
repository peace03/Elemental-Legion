using System.Globalization;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

/// <summary>시야 이벤트 대신 도메인 누적값을 사용한 플레이어별 신규 아군 통계다.</summary>
public sealed class GameplayReport
{
    public int PlayerId {get;}
    public long Trained {get;}
    public long Converted {get;}
    public double Target {get;}
    public double? ConvertedShare=>Trained+Converted==0?null:(double)Converted/(Trained+Converted);
    public GameplayReport(WorldView view,double target){PlayerId=view.PlayerId;Trained=view.TrainedCount;Converted=view.ConvertedCount;Target=target;}
    public void Write(TextWriter output)
    {
        string share=ConvertedShare.HasValue?ConvertedShare.Value.ToString("P2",CultureInfo.InvariantCulture):"N/A";
        output.WriteLine(FormattableString.Invariant($"gameplay player={PlayerId}; trained={Trained}; converted={Converted}; convertedShare={share}; target={Target:P2}; initial units excluded"));
    }
}
