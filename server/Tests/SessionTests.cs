using System.Numerics;
using ElementalLegion.Server;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>서버가 정한 소유자·출발점과 연결 종료의 경계를 검사한다.</summary>
public sealed class SessionTests
{
    [Fact]
    public void SessionsUseServerIdsEnforceCapacityAndDoNotReuseDisconnectedOwners()
    {
        var rules = GameRulesTests.Rules();
        var document = new GameDataDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
        var network = new NetworkWorld(rules, new NetworkSettings(document));
        Assert.Equal(JoinFailure.InvalidElement, network.TryJoin(rules.ElementIndex("light"), out _));
        for (int index = 0; index < rules.MaxPlayers; index++)
        {
            Assert.Equal(JoinFailure.None, network.TryJoin(rules.StartingElements[0], out int owner));
            Assert.Equal(index + 1, owner);
        }
        Assert.Equal(JoinFailure.WorldFull, network.TryJoin(rules.StartingElements[0], out _));
        network.Leave(1);
        Assert.Equal(CommandAdmission.UnknownPlayer, network.Move(1, 99, Vector2.UnitX));
        Assert.Equal(JoinFailure.WorldFull, network.TryJoin(rules.StartingElements[0], out _));
        Assert.Equal(rules.StartUnitCount, network.World.GetView(1).Population);
    }

    [Fact]
    public void TwoLegionsMoveIntoMutualVisionAndAcknowledgeAppliedInputs()
    {
        var rules = GameRulesTests.Rules();
        var document = new GameDataDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
        var network = new NetworkWorld(rules, new NetworkSettings(document));
        network.TryJoin(rules.StartingElements[0], out int first);
        network.TryJoin(rules.StartingElements[1], out int second);
        Vector2 start = network.World.GetView(first).LegionPosition;
        Vector2 other = network.World.GetView(second).LegionPosition;
        Assert.DoesNotContain(network.World.GetView(first).Units, unit => unit.OwnerId == second);
        Vector2 direction = Vector2.Normalize(other - start);
        Assert.Equal(CommandAdmission.Accepted, network.Move(first, 1, direction));
        Assert.Equal(CommandAdmission.StaleSequence, network.Move(first, 1, direction));
        Assert.Equal(CommandAdmission.Accepted, network.Move(second, 1, -direction));
        int ticks = (int)((Vector2.Distance(start, other) - rules.VisionRadius * 0.5f) / (rules.LegionSpeed * 2 * rules.DeltaTime));
        for (int index = 0; index < ticks; index++) network.Step();
        Assert.Contains(network.World.GetView(first).Units, unit => unit.OwnerId == second);
        Assert.Contains(network.World.GetView(second).Units, unit => unit.OwnerId == first);
        Assert.Equal(1UL, network.World.GetView(first).AppliedSequence);
    }
}
