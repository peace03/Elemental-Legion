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

    internal ChatMigrationState CaptureMigration()
    {
        long now = Environment.TickCount64;
        return new ChatMigrationState
        {
            NextId = nextId,
            Cooldowns = lastSent.Select(item => new ChatCooldown(item.Key.Owner, item.Key.Global,
                    Math.Max(0, 1000L * (item.Key.Global ? settings.GlobalChatCooldown : settings.ProximityChatCooldown) - (now - item.Value))))
                .Where(item => item.RemainingMilliseconds > 0).ToArray(),
            History = history.Where(item => now - item.At < 300_000).Select(item => new ChatDelivery(item.Id,
                item.Owner, item.Text, item.Recipients.ToArray(), Math.Max(0, now - item.At))).ToArray()
        };
    }

    internal void RestoreMigration(ChatMigrationState snapshot)
    {
        lastSent.Clear();
        history.Clear();
        nextId = snapshot.NextId;
        long now = Environment.TickCount64;
        foreach (ChatCooldown cooldown in snapshot.Cooldowns)
        {
            long duration = 1000L * (cooldown.Global ? settings.GlobalChatCooldown : settings.ProximityChatCooldown);
            if (cooldown.RemainingMilliseconds > duration) throw new InvalidDataException("채팅 제한의 남은 시간이 잘못됐습니다.");
            lastSent.Add((cooldown.Owner, cooldown.Global), now - duration + cooldown.RemainingMilliseconds);
        }
        foreach (ChatDelivery item in snapshot.History)
            history.Enqueue(new Delivery(item.Id, item.Owner, item.Text, new HashSet<int>(item.Recipients), now - item.AgeMilliseconds));
    }

    internal static void ValidateMigration(ChatMigrationState snapshot, int nextPlayerId)
    {
        if (snapshot.Cooldowns == null || snapshot.Cooldowns.Length > 30 || snapshot.History == null ||
            snapshot.History.Length > HistoryLimit || snapshot.NextId == ulong.MaxValue)
            throw new InvalidDataException("승계 채팅 상태가 잘못됐습니다.");
        var keys = new HashSet<(int, bool)>();
        foreach (ChatCooldown item in snapshot.Cooldowns)
            if (item == null || item.Owner < 1 || item.Owner >= nextPlayerId || item.RemainingMilliseconds <= 0 ||
                item.RemainingMilliseconds > 3_600_000 || !keys.Add((item.Owner, item.Global)))
                throw new InvalidDataException("승계 채팅 제한이 잘못됐습니다.");
        ulong previous = 0;
        foreach (ChatDelivery item in snapshot.History)
        {
            if (item == null || item.Id <= previous || item.Id > snapshot.NextId || item.Owner < 1 || item.Owner >= nextPlayerId ||
                !M5Protocol.IsSafeText(item.Text, 200) || item.Recipients == null || item.Recipients.Length > 15 ||
                item.Recipients.Distinct().Count() != item.Recipients.Length || item.Recipients.Any(owner => owner <= 0 || owner >= nextPlayerId) ||
                item.AgeMilliseconds < 0 || item.AgeMilliseconds >= 300_000)
                throw new InvalidDataException("승계 채팅 기록이 잘못됐습니다.");
            previous = item.Id;
        }
    }

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
