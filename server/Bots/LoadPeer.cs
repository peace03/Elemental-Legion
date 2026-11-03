using System.Diagnostics;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Bots;

internal sealed class LoadPeer : IDisposable
{
    internal sealed class MoveSample(long sent)
    {
        internal long Sent { get; } = sent;
        internal long Applied { get; set; }
        internal CommandAdmission? Response { get; set; }
    }

    private readonly double startingAngle;
    private double angle;
    private ulong sequence;
    private ulong firstMeasuredSequence;
    private int nextPoseSample;

    internal int Element { get; }
    internal string Name { get; }
    internal ClientConnection Connection { get; }
    internal LegionPose Pose { get; private set; }
    internal bool HasPose { get; private set; }
    internal bool Ready => Connection.State == ConnectionState.Connected && Connection.Fog.Ready &&
        Connection.Gameplay.State.Tick >= 0 && HasPose;
    internal double NextMove { get; set; }
    internal List<MoveSample> Moves { get; } = new();
    internal byte[]? ResumeKey { get; private set; }

    internal LoadPeer(GameRules rules, NetworkSettings settings, int index, Random random)
    {
        Element = rules.StartingElements[index % rules.StartingElements.Count];
        Name = $"m6-{index + 1:00}";
        startingAngle = random.NextDouble() * Math.Tau;
        angle = startingAngle;
        Connection = new ClientConnection(rules, settings);
        Connection.PoseReceived += ObservePose;
        Connection.CommandResponse += ObserveResponse;
    }

    internal void Poll()
    {
        Connection.Poll();
        if (Connection.IssuedAccountKey is { } key && ResumeKey == null) ResumeKey = key.ToArray();
    }

    internal void Move(bool measure)
    {
        float map = Connection.WorldRules.MapSize;
        Vector2 center = new(map / 2);
        float radius = map * 0.035f;
        Vector2 target = center + radius * new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        Vector2 delta = target - Pose.Position;
        if (delta.LengthSquared() < 4)
        {
            angle += Math.PI / 2;
            target = center + radius * new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            delta = target - Pose.Position;
        }
        if (measure)
        {
            if (Moves.Count == 0) firstMeasuredSequence = sequence + 1;
            Moves.Add(new MoveSample(Stopwatch.GetTimestamp()));
        }
        Vector2 direction = delta.LengthSquared() < 0.01f ? Vector2.Zero : Vector2.Normalize(delta);
        foreach (BuildingView building in Connection.Gameplay.State.Buildings)
        {
            if (direction == Vector2.Zero || !building.BlocksMovement ||
                CircleCollision.Limit(Pose.Position, direction * Connection.WorldRules.SlotSpacing, building.Position, building.Radius) == 1) continue;
            Vector2 outward = Pose.Position - building.Position;
            if (outward.LengthSquared() < 0.0001f) break;
            Vector2 tangent = Vector2.Normalize(new Vector2(-outward.Y, outward.X));
            direction = Vector2.Dot(tangent, direction) >= 0 ? tangent : -tangent;
            break;
        }
        Connection.SendMove(++sequence, direction);
    }

    internal void RestartMovement()
    {
        HasPose = false;
        sequence = 0;
        angle = startingAngle;
    }

    internal void EndMeasurement() => firstMeasuredSequence = 0;

    private void ObservePose(LegionPose pose)
    {
        Pose = pose;
        HasPose = true;
        if (firstMeasuredSequence == 0) return;
        long now = Stopwatch.GetTimestamp();
        while (nextPoseSample < Moves.Count && firstMeasuredSequence + (ulong)nextPoseSample <= pose.AppliedSequence)
            Moves[nextPoseSample++].Applied = now;
    }

    private void ObserveResponse(ulong id, CommandAdmission response)
    {
        if (firstMeasuredSequence == 0 || id < firstMeasuredSequence || id - firstMeasuredSequence >= (ulong)Moves.Count) return;
        Moves[(int)(id - firstMeasuredSequence)].Response = response;
    }

    public void Dispose()
    {
        Connection.Dispose();
        if (ResumeKey != null) Array.Clear(ResumeKey);
    }
}
