using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>고정 원형 건물의 바깥 후보점을 탐색해 지점 이동의 다음 경유점을 구한다.</summary>
    internal static class BuildingNavigation
    {
        private const int Sides = 16, MaxObstacles = 16, MaxNodes = Sides * MaxObstacles + 2, EdgeBudget = 8192;
        private const float Clearance = 0.001f;

        internal static Vector2 Next(WorldState world, Vector2 start, Vector2 goal, Vector2 guardOrigin, float guardRadius, PlayerState observer = null)
        {
            // 건물 내부를 지정한 명령은 기존 충돌 처리로 표면까지 접근한 뒤 멈춘다.
            if (!Legal(world, goal, guardOrigin, guardRadius, observer)) return goal;
            if (Blocking(world, start, goal, observer) == null) return goal;
            Span<Vector2> nodes = stackalloc Vector2[MaxNodes];
            Span<float> costs = stackalloc float[MaxNodes];
            Span<int> previous = stackalloc int[MaxNodes];
            Span<byte> closed = stackalloc byte[MaxNodes];
            Span<int> obstacles = stackalloc int[MaxObstacles];
            costs.Fill(float.PositiveInfinity); previous.Fill(-1); closed.Clear();
            nodes[0] = start; nodes[1] = goal; costs[0] = 0;
            int count = 2, obstacleCount = 0, budget = EdgeBudget;
            // shortcut: 한 경로의 건물 16개와 간선 검사 수를 제한한다. 복잡한 미로가 필요해지면 전용 경로 그래프로 바꾼다.
            while (budget > 0)
            {
                int current = -1; float best = float.PositiveInfinity;
                for (int i = 0; i < count; i++)
                {
                    float score = costs[i] + Vector2.Distance(nodes[i], goal);
                    if (closed[i] == 0 && score < best) { best = score; current = i; }
                }
                if (current < 0) break;
                if (current == 1)
                {
                    int next = 1;
                    while (previous[next] > 0) next = previous[next];
                    return nodes[next];
                }
                closed[current] = 1;
                for (int next = 1; next < count && budget > 0; next++)
                {
                    if (closed[next] != 0) continue;
                    budget--;
                    BuildingState blocking = Blocking(world, nodes[current], nodes[next], observer);
                    if (blocking != null)
                    {
                        bool known = false;
                        for (int i = 0; i < obstacleCount; i++) if (obstacles[i] == blocking.Id) { known = true; break; }
                        if (known || obstacleCount == MaxObstacles) continue;
                        obstacles[obstacleCount++] = blocking.Id;
                        // 외접 다각형의 변도 원 밖에 두어 좌표 반올림으로 건물 안에 들어가지 않게 한다.
                        float radius = blocking.Radius / MathF.Cos(MathF.PI / Sides) + Clearance;
                        for (int i = 0; i < Sides; i++)
                        {
                            float angle = i * (2 * MathF.PI / Sides);
                            Vector2 point = blocking.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                            if (Legal(world, point, guardOrigin, guardRadius, observer)) nodes[count++] = point;
                        }
                        continue;
                    }
                    float cost = costs[current] + Vector2.Distance(nodes[current], nodes[next]);
                    if (cost < costs[next]) { costs[next] = cost; previous[next] = current; }
                }
            }
            return start;
        }

        /// <summary>빈 중심은 직전 완성된 관측과 자기 건물만 사용하며 원격 충돌로 정보를 얻지 않는다.</summary>
        internal static bool BlocksFor(BuildingState building, PlayerState observer)
            => building.BlocksMovement && (observer == null || building.OwnerId == observer.Id || observer.View.CanSeePoint(building.Position));

        internal static Vector2 ResolveCenter(WorldState world, Vector2 center, PlayerState owner, float guardRadius, bool observedOnly = true)
        {
            PlayerState observer = observedOnly ? owner : null;
            if (Legal(world, center, owner.BasePosition, guardRadius, observer)) return center;
            Vector2 closest = center; float best = float.PositiveInfinity;
            // 건물 안의 중심을 가까운 합법 경계로 옮긴다. 목적지와 분대 소속은 바꾸지 않는다.
            foreach (BuildingState building in world.Buildings)
            {
                if (!BlocksFor(building, observer)) continue;
                Vector2 radial = center - building.Position;
                if (radial == Vector2.Zero) radial = owner.BasePosition - building.Position;
                if (radial == Vector2.Zero) radial = Vector2.UnitY;
                radial = Vector2.Normalize(radial);
                for (int i = -1; i < Sides; i++)
                {
                    float angle = i * (2 * MathF.PI / Sides);
                    Vector2 direction = i < 0 ? radial : new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    Vector2 point = Vector2.Clamp(building.Position + direction * (building.Radius + Clearance), Vector2.Zero, new Vector2(world.Rules.MapSize));
                    if (guardRadius > 0) point = SquadSystem.ClampGuardPosition(world, owner, point);
                    float distance = Vector2.DistanceSquared(center, point);
                    if (distance < best && Legal(world, point, owner.BasePosition, guardRadius, observer))
                    { closest = point; best = distance; }
                }
            }
            return closest;
        }

        private static bool Legal(WorldState world, Vector2 point, Vector2 guardOrigin, float guardRadius, PlayerState observer)
        {
            if (point.X < 0 || point.Y < 0 || point.X > world.Rules.MapSize || point.Y > world.Rules.MapSize ||
                guardRadius > 0 && Vector2.DistanceSquared(point, guardOrigin) > guardRadius * guardRadius + Clearance) return false;
            foreach (BuildingState building in world.Buildings)
                if (BlocksFor(building, observer) && Vector2.DistanceSquared(point, building.Position) < building.Radius * building.Radius) return false;
            return true;
        }

        private static BuildingState Blocking(WorldState world, Vector2 start, Vector2 end, PlayerState observer)
        {
            float fraction = 1; BuildingState obstacle = null;
            foreach (BuildingState building in world.Buildings)
            {
                if (!BlocksFor(building, observer)) continue;
                float limit = CircleCollision.Limit(start, end - start, building.Position, building.Radius, fraction);
                if (limit < fraction) { fraction = limit; obstacle = building; }
            }
            return obstacle;
        }
    }
}
