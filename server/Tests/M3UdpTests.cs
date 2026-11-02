using System.Diagnostics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>실제 UDP에서 v3 기준·양성·집결·플레이어 격리와 종료 초기화를 검사한다.</summary>
public sealed class M3UdpTests
{
    [Fact]
    public void TwoDemoClientsReceivePrivateStateAndTrainThroughTheSameServerAdmission()
    {
        var data=GameRulesTests.Data();var rules=GameRulesTests.Rules();var settings=new NetworkSettings(data);
        using var host=new UdpGameServer(rules,settings,"m3-demo");host.Start(0);
        using var first=new ClientConnection(rules,settings);using var second=new ClientConnection(rules,settings);
        first.Connect("127.0.0.1",host.Port,rules.StartingElements[0]);second.Connect("127.0.0.1",host.Port,rules.StartingElements[1]);
        Pump(()=>first.State==ConnectionState.Connected&&second.State==ConnectionState.Connected&&first.Gameplay.State.Tick>=0&&second.Gameplay.State.Tick>=0,host,first,second);
        Assert.Equal(first.PlayerId,first.Gameplay.State.PlayerId);Assert.Equal(second.PlayerId,second.Gameplay.State.PlayerId);
        Assert.All(first.Gameplay.State.Buildings.ToArray(),building=>Assert.Equal(first.PlayerId,building.OwnerId));
        first.SendTrain(1,rules.ElementIndex("life"));first.SendRally(2);
        Pump(()=>first.Gameplay.State.TrainingElement==rules.ElementIndex("life"),host,first,second);
        Assert.Equal(-1,second.Gameplay.State.TrainingElement);
        Pump(()=>first.Gameplay.State.TrainedCount==1,host,first,second);
        Assert.Equal(0,second.Gameplay.State.TrainedCount);Assert.Equal(1,host.GetGameplayReport(first.PlayerId).Trained);
        first.Disconnect();Assert.Equal(-1,first.Gameplay.State.Tick);Assert.False(first.Fog.Ready);
    }
    private static void Pump(Func<bool> done,UdpGameServer host,params ClientConnection[] clients)
    {
        var timer=Stopwatch.StartNew();while(!done()&&timer.Elapsed.TotalSeconds<10)
        {host.Poll();host.Step();foreach(var client in clients)client.Poll();Thread.Sleep(1);}
        Assert.True(done(),"v3 UDP 통합 검사가 제한 시간 안에 완료되지 않았습니다.");
    }
}
