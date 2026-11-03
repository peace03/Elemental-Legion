using System.Diagnostics;
using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>실제 루프백 UDP와 Unity가 사용하는 동일 연결 코드로 두 세션을 검사한다.</summary>
public sealed class UdpIntegrationTests
{
    [Fact]
    public void TwoRealClientsConnectMoveObserveAndRejectDifferentGameData()
    {
        var data = GameRulesTests.Data(); var rules = new GameRules(data); var settings = new NetworkSettings(data);
        using var host = new UdpGameServer(rules, settings, "m3-demo"); host.Start(0);
        using var first = new ClientConnection(rules, settings);
        using var second = new ClientConnection(rules, settings);
        long firstTick = -1; Vector2 firstPosition = default;
        bool firstSawOther = false, secondSawOther = false;
        var firstIds = new HashSet<int>(); var secondIds = new HashSet<int>();
        first.PoseReceived += pose => { firstTick = pose.Tick; firstPosition = pose.Position; };
        first.UnitsReceived += frame => { foreach(var unit in frame.Units.Take(frame.Count)) firstIds.Add(unit.Id); firstSawOther |= second.PlayerId > 0 && frame.Units.Take(frame.Count).Any(unit => unit.OwnerId == second.PlayerId); };
        second.UnitsReceived += frame => { foreach(var unit in frame.Units.Take(frame.Count)) secondIds.Add(unit.Id); secondSawOther |= first.PlayerId > 0 && frame.Units.Take(frame.Count).Any(unit => unit.OwnerId == first.PlayerId); };
        first.Connect("127.0.0.1", host.Port, rules.StartingElements[0]);
        second.Connect("127.0.0.1", host.Port, rules.StartingElements[1]);
        Pump(() => first.State == ConnectionState.Connected && second.State == ConnectionState.Connected && first.Fog.Ready && second.Fog.Ready && firstTick > 0 && firstIds.Count>0 && secondIds.Count>0, host, first, second);
        Assert.NotEqual(first.PlayerId, second.PlayerId);
        Assert.Equal(first.PlayerId, first.Fog.PlayerId);
        Assert.False(firstSawOther); Assert.False(secondSawOther);
        Assert.Equal(host.Simulation.World.GetView(first.PlayerId).Units.Select(unit=>unit.Id).Order(),firstIds.Order());
        Assert.Equal(host.Simulation.World.GetView(second.PlayerId).Units.Select(unit=>unit.Id).Order(),secondIds.Order());
        var otherBase = host.Simulation.World.GetView(second.PlayerId).BasePosition;
        Assert.Equal(ElementalLegion.Shared.Sim.VisionCellState.Unexplored, first.Fog.GetState((int)(otherBase.X / rules.CellSize), (int)(otherBase.Y / rules.CellSize)));
        Vector2 start = firstPosition;
        Vector2 direction = Vector2.Normalize(host.Simulation.World.GetView(second.PlayerId).LegionPosition - host.Simulation.World.GetView(first.PlayerId).LegionPosition);
        first.SendMove(1, direction); second.SendMove(1, -direction);
        Pump(() => firstSawOther && secondSawOther, host, first, second);
        Assert.True(Vector2.Distance(start, firstPosition) > rules.VisionRadius);
        Assert.True(host.SentPayloadBytes > 0);
        var traffic = host.GetTraffic(first.PlayerId);
        Assert.True(traffic.AppSentBytes > 0 && traffic.TransportSentBytes > 0);
        Assert.True(traffic.InitialSyncSentBytes > 0);
        Assert.True(traffic.SentPayload(PacketKind.Units) > 0);
        var different = new GameDataDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")) + "\n");
        using var rejected = new ClientConnection(new GameRules(different), settings);
        rejected.Connect("127.0.0.1", host.Port, rules.StartingElements[0]);
        Pump(() => rejected.State == ConnectionState.Disconnected, host, first, second, rejected);
        Assert.Equal(JoinFailure.DataMismatch, rejected.JoinFailure);
        int disconnectedOwner = first.PlayerId; first.Disconnect();
        Pump(() => host.Simulation.ConnectedCount == 1, host, second);
        Vector2 stopped = host.Simulation.World.GetView(disconnectedOwner).LegionPosition;
        host.Step(); Assert.Equal(stopped, host.Simulation.World.GetView(disconnectedOwner).LegionPosition);
        Assert.True(string.IsNullOrEmpty(host.LastSocketError), host.LastSocketError);
        Assert.False(first.Fog.Ready);
        Vector2 oldBase=host.Simulation.World.GetView(disconnectedOwner).BasePosition;
        first.Connect("127.0.0.1",host.Port,rules.StartingElements[0]);
        Assert.All(first.Fog.Explored.ToArray(),value=>Assert.Equal(0,value));
        Pump(()=>first.State==ConnectionState.Connected&&first.Fog.Ready,host,first,second);
        Assert.NotEqual(disconnectedOwner,first.PlayerId);
        Assert.Equal(ElementalLegion.Shared.Sim.VisionCellState.Unexplored,first.Fog.GetState((int)(oldBase.X/rules.CellSize),(int)(oldBase.Y/rules.CellSize)));
    }

    private static void Pump(Func<bool> complete, UdpGameServer host, params ClientConnection[] clients)
    {
        var timeout = Stopwatch.StartNew();
        while (!complete() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            host.Poll(); host.Step(); foreach (var client in clients) client.Poll(); Thread.Sleep(1);
        }
        Assert.True(complete(), "루프백 UDP가 제한 시간 안에 기대 상태가 되지 않았습니다.");
    }
}
