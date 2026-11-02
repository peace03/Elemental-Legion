using System;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>시야 안의 중립·핵을 찾고 양성한 뒤 적에게 접근하는 공용 봇이다. 월드 수정 권한은 갖지 않는다.</summary>
    public sealed class BotBrain
    {
        private readonly GameRules rules;
        private readonly int playerId;
        private ulong sequence;
        private long lastTick = -1;
        private Vector2 previousDirection;
        private bool hasDirection;
        private int patrol;

        public BotBrain(GameRules rules, int playerId)
        {
            this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
            if (playerId < 0) throw new ArgumentOutOfRangeException(nameof(playerId));
            this.playerId = playerId; patrol = playerId % 4;
        }

        /// <summary>관측 틱마다 최대 한 명령을 생성한다. 같은 관측을 중복 호출하면 명령을 만들지 않는다.</summary>
        public SimCommand? Decide(WorldView view)
        {
            if (view == null || view.PlayerId != playerId) throw new ArgumentException("봇 소유자의 관측이 아닙니다.", nameof(view));
            if (view.Tick == lastTick) return null;
            lastTick = view.Tick;
            UnitView? neutral = null, enemy = null;
            float neutralDistance = float.PositiveInfinity, enemyDistance = float.PositiveInfinity;
            foreach (UnitView unit in view.Units)
            {
                if (unit.OwnerId == playerId) continue;
                float distance = Vector2.DistanceSquared(unit.Position, view.LegionPosition);
                if (unit.OwnerId < 0 && (distance < neutralDistance || distance == neutralDistance && (!neutral.HasValue || unit.Id < neutral.Value.Id)))
                { neutral = unit; neutralDistance = distance; }
                else if (unit.OwnerId >= 0 && (distance < enemyDistance || distance == enemyDistance && (!enemy.HasValue || unit.Id < enemy.Value.Id)))
                { enemy = unit; enemyDistance = distance; }
            }
            if (neutral.HasValue) return Move(view, neutral.Value.Position);
            CoreView? core = null;
            float coreDistance = float.PositiveInfinity;
            foreach (CoreView candidate in view.Cores)
            {
                float distance = Vector2.DistanceSquared(candidate.Position, view.LegionPosition);
                if (distance < coreDistance || distance == coreDistance && (!core.HasValue || candidate.Id < core.Value.Id)) { core = candidate; coreDistance = distance; }
            }
            if (core.HasValue) return Move(view, core.Value.Position);
            if (!view.IsTraining && view.NatureForge && view.Population + view.ReservedPopulation < view.PopulationCap && view.CoreBalance >= rules.TrainingCost(view.MainElement, view.MainElement))
                return SimCommand.Train(++sequence, rules.Elements[view.MainElement].Id);
            if (enemy.HasValue) return Move(view, enemy.Value.Position);
            // 경유점은 맵 사분면의 중심이며 새로운 이동속도나 게임 수치를 만들지 않는다.
            Vector2 point = new Vector2((patrol == 0 || patrol == 3 ? 0.25f : 0.75f) * rules.MapSize, (patrol < 2 ? 0.25f : 0.75f) * rules.MapSize);
            if (Vector2.DistanceSquared(point, view.LegionPosition) <= rules.CorePickupRadius * rules.CorePickupRadius) patrol = (patrol + 1) % 4;
            return Move(view, point);
        }

        private SimCommand? Move(WorldView view, Vector2 target)
        {
            Vector2 delta = target - view.LegionPosition;
            Vector2 direction = delta.LengthSquared() <= rules.CorePickupRadius * rules.CorePickupRadius ? Vector2.Zero : Vector2.Normalize(delta);
            if (hasDirection && direction == previousDirection) return null;
            hasDirection = true; previousDirection = direction;
            return SimCommand.Move(++sequence, direction);
        }
    }
}
