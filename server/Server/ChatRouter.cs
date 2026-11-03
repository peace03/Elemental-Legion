using System.Numerics;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

public sealed record ChatReport(ulong Id, int From, int Reporter, string Text);

/// <summary>현재 방의 접속과 시야를 기준으로 채팅을 전달하고 최근 신고 근거만 보관한다.</summary>
public sealed class ChatRouter(NetworkSettings settings)
{
    private const int HistoryLimit = 100;
    private readonly Dictionary<(int Owner, bool Global), long> lastSent = new();
    private readonly List<(int Owner, bool Global)> expired = new();
    private readonly Queue<Delivery> history = new();
    private ulong nextId;

    private sealed record Delivery(ulong Id, int Owner, string Text, HashSet<int> Recipients, long At);

    public bool Send(NetworkWorld world, int owner, ChatRequest request, Action<int, ChatMessage> deliver)
    {
        if (!world.ConnectedOwners.Contains(owner) || !world.World.TryGetView(owner, out var sender) ||
            !M5Protocol.IsSafeText(request.Text, settings.ChatMaxLength)) return false;
        long now = Environment.TickCount64;
        long cooldown = 1000L * (request.Global ? settings.GlobalChatCooldown : settings.ProximityChatCooldown);
        var key = (owner, request.Global);
        if (lastSent.TryGetValue(key, out long previous) && now - previous < cooldown) return false;
        expired.Clear();
        foreach (var item in lastSent)
            if (now - item.Value >= 1000L * (item.Key.Global ? settings.GlobalChatCooldown : settings.ProximityChatCooldown))
                expired.Add(item.Key);
        foreach (var item in expired) lastSent.Remove(item);
        lastSent[key] = now;

        string nickname = PlayerNickname.Default;
        foreach (PlayerStanding standing in sender.Standings)
            if (standing.PlayerId == owner) { nickname = standing.Nickname; break; }
        ulong id = checked(++nextId);
        var recipients = new HashSet<int>();
        foreach (int recipient in world.ConnectedOwners)
        {
            if (!world.World.TryGetView(recipient, out var view)) continue;
            if (!request.Global && !WithinRadius(sender.LegionPosition, view.LegionPosition, settings.ChatRadius)) continue;
            int anchor = 0;
            if (!request.Global)
                foreach (UnitView unit in view.Units)
                    if (unit.OwnerId == owner) { anchor = unit.Id; break; }
            deliver(recipient, new ChatMessage(id, owner, nickname, request.Global, anchor, request.Text));
            if (recipient != owner) recipients.Add(recipient);
        }
        while (history.Count > 0 && (history.Count >= HistoryLimit || now - history.Peek().At >= 300_000)) history.Dequeue();
        history.Enqueue(new Delivery(id, owner, request.Text, recipients, now));
        return true;
    }

    public ChatReport? Report(int reporterId, ulong messageId)
    {
        foreach (Delivery delivery in history)
            if (delivery.Id == messageId && Environment.TickCount64 - delivery.At < 300_000 && delivery.Recipients.Remove(reporterId))
                return new ChatReport(messageId, delivery.Owner, reporterId, delivery.Text);
        return null;
    }

    public static bool WithinRadius(Vector2 from, Vector2 to, float radius) =>
        Vector2.DistanceSquared(from, to) <= radius * radius;
}
