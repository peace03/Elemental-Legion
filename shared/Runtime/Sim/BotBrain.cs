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
        private readonly bool playMatch;
        private readonly long decisionTicks;
        private bool retreating;
        public AiDifficulty Difficulty { get; }
        private ulong sequence;
        private long lastTick = -1;
        private Vector2 previousDirection;
        private bool hasDirection;
        private int patrol;

        public BotBrain(GameRules rules, int playerId, bool playMatch = false, AiDifficulty difficulty = AiDifficulty.Normal)
        {
            this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
            if (playerId < 0) throw new ArgumentOutOfRangeException(nameof(playerId));
            if (difficulty > AiDifficulty.Hard) throw new ArgumentOutOfRangeException(nameof(difficulty));
            this.playerId = playerId; patrol = playerId % 4;
            this.playMatch = playMatch;
            Difficulty = difficulty;
            decisionTicks = playMatch ? rules.AiDecisionTicks(difficulty) : 1;
        }

        /// <summary>판단 틱과 명령 순번을 포함한 독립된 승계 상태를 만든다.</summary>
        public BotBrainState CaptureState() => new BotBrainState
        {
            PlayerId = playerId, PlayMatch = playMatch, Sequence = sequence, LastTick = lastTick,
            PreviousDirection = previousDirection, HasDirection = hasDirection, Patrol = patrol,
            Difficulty = Difficulty, Retreating = retreating
        };

        public static BotBrain RestoreState(GameRules rules, BotBrainState snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            WorldCheckpoint.Check(snapshot.PlayerId >= 0 && snapshot.LastTick >= -1 && snapshot.Difficulty <= AiDifficulty.Hard &&
                WorldCheckpoint.Finite(snapshot.PreviousDirection.X) && WorldCheckpoint.Finite(snapshot.PreviousDirection.Y) &&
                snapshot.PreviousDirection.LengthSquared() <= 1.001f && snapshot.Patrol >= 0 && snapshot.Patrol < 4,
                "봇 상태");
            return new BotBrain(rules, snapshot.PlayerId, snapshot.PlayMatch, snapshot.Difficulty)
            {
                sequence = snapshot.Sequence, lastTick = snapshot.LastTick,
                previousDirection = snapshot.PreviousDirection, hasDirection = snapshot.HasDirection, patrol = snapshot.Patrol,
                retreating = snapshot.Retreating
            };
        }

        /// <summary>설정된 판단 주기마다 최대 한 명령을 생성한다. 같은 관측을 중복 호출하면 명령을 만들지 않는다.</summary>
        public SimCommand? Decide(WorldView view)
        {
            if (view == null || view.PlayerId != playerId) throw new ArgumentException("봇 소유자의 관측이 아닙니다.", nameof(view));
            if (view.Tick <= lastTick || lastTick >= 0 && view.Tick - lastTick < decisionTicks) return null;
            lastTick = view.Tick;
            if (playMatch && ShouldRetreat(view)) return Move(view, view.BasePosition);
            if (playMatch && Develop(view) is SimCommand order) return order;
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
            // 첫 군단을 증원한 뒤에는 시야에 들어온 적 본진을 우선 공략한다.
            if (playMatch && view.Population - view.GarrisonCount > rules.StartUnitCount)
                foreach (BuildingView building in view.Buildings)
                    if (building.OwnerId != playerId && building.Kind == BuildingKind.Base)
                        return Move(view, building.Position);
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
            if (playMatch)
                foreach (BuildingView building in view.Buildings)
                    if (building.OwnerId != playerId && building.Kind == BuildingKind.Base)
                        return Move(view, building.Position);
            // 경유점은 맵 사분면의 중심이며 새로운 이동속도나 게임 수치를 만들지 않는다.
            Vector2 point = new Vector2((patrol == 0 || patrol == 3 ? 0.25f : 0.75f) * rules.MapSize, (patrol < 2 ? 0.25f : 0.75f) * rules.MapSize);
            if (Vector2.DistanceSquared(point, view.LegionPosition) <= rules.CorePickupRadius * rules.CorePickupRadius) patrol = (patrol + 1) % 4;
            return Move(view, point);
        }

        private SimCommand? Develop(WorldView view)
        {
            if (view.GarrisonCount > view.RallyingCount) return SimCommand.Rally(++sequence);
            if (Build(view, BuildingKind.Collector) is SimCommand collector) return collector;
            if (Build(view, BuildingKind.NatureForge) is SimCommand forge) return forge;
            int element = TrainingElement(view);
            if (view.NatureForge && !view.IsTraining && view.Population + view.ReservedPopulation < view.PopulationCap &&
                view.CoreBalance >= rules.TrainingCost(element, view.MainElement))
                return SimCommand.Train(++sequence, rules.Elements[element].Id);
            if (view.Population == view.PopulationCap && view.BaseLevel < rules.MaxBaseLevel &&
                view.CoreBalance >= rules.UpgradeCost(view.BaseLevel + 1))
                return SimCommand.UpgradeBase(++sequence);
            return null;
        }

        private bool ShouldRetreat(WorldView view)
        {
            float threshold = rules.AiRetreatHealthRatio(Difficulty);
            float home = rules.BaseRadius + rules.CorePickupRadius;
            if (threshold <= 0 || Vector2.DistanceSquared(view.LegionPosition, view.BasePosition) <= home * home)
                return retreating = false;
            if (retreating) return true;
            double health = 0, maximum = 0;
            bool threat = false;
            foreach (UnitView unit in view.Units)
            {
                if (unit.OwnerId != playerId) { threat = true; continue; }
                if (unit.Garrison) continue;
                health += unit.Health; maximum += unit.MaxHealth;
            }
            return retreating = threat && maximum > 0 && health / maximum < threshold;
        }

        private int TrainingElement(WorldView view)
        {
            int best = view.MainElement;
            if (Difficulty != AiDifficulty.Hard) return best;
            double bestScore = CounterScore(view, best);
            for (int element = 0; element < rules.Elements.Count; element++)
            {
                ElementDefinition definition = rules.Elements[element];
                if (definition.Effect == EffectKind.Heal ||
                    (definition.Group == ElementGroup.Nature ? !view.NatureForge : !view.EnergyForge) ||
                    view.CoreBalance < rules.TrainingCost(element, view.MainElement)) continue;
                double score = CounterScore(view, element);
                if (score > bestScore) { best = element; bestScore = score; }
            }
            return best;
        }

        private double CounterScore(WorldView view, int element)
        {
            double score = 0;
            foreach (UnitView unit in view.Units)
                if (unit.OwnerId != playerId) score += rules.Matchup(element, unit.Element) * unit.Health / unit.MaxHealth;
            return score;
        }

        private SimCommand? Build(WorldView view, BuildingKind kind)
        {
            if (view.CoreBalance < rules.BuildingCost(kind) || !rules.IsBuildingUnlocked(view.BaseLevel, kind)) return null;
            foreach (BuildingView building in view.Buildings)
                if (building.OwnerId == playerId && building.Kind == kind) return null;

            float radius = rules.BuildingRadius(kind);
            float distance = (rules.BaseRadius + rules.BuildRadius) * 0.5f;
            float grid = rules.BuildGridSize;
            for (int slot = 0; slot < 8; slot++)
            {
                double angle = slot * Math.PI / 4;
                Vector2 candidate = view.BasePosition + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * distance;
                int x = (int)Math.Round(candidate.X / grid), z = (int)Math.Round(candidate.Y / grid);
                Vector2 point = new Vector2(x, z) * grid;
                float reach = rules.BuildRadius - radius;
                if (reach < 0 || point.X < radius || point.Y < radius || point.X > rules.MapSize - radius ||
                    point.Y > rules.MapSize - radius || Vector2.DistanceSquared(point, view.BasePosition) > reach * reach) continue;
                if (!CanBuildAt(view, point, radius)) continue;
                return SimCommand.Build(++sequence, kind, x, z);
            }
            return null;
        }

        private static bool CanBuildAt(WorldView view, Vector2 point, float radius)
        {
            foreach (BuildingView building in view.Buildings)
            {
                float clearance = building.Radius + radius;
                if (building.BlocksMovement && Vector2.DistanceSquared(building.Position, point) < clearance * clearance) return false;
            }
            float squared = radius * radius;
            if (Vector2.DistanceSquared(view.LegionPosition, point) < squared) return false;
            foreach (UnitView unit in view.Units)
                if (Vector2.DistanceSquared(unit.Position, point) < squared) return false;
            return true;
        }

        private SimCommand? Move(WorldView view, Vector2 target)
        {
            Vector2 delta = target - view.LegionPosition;
            Vector2 direction = delta.LengthSquared() <= rules.CorePickupRadius * rules.CorePickupRadius ? Vector2.Zero : Vector2.Normalize(delta);
            if (playMatch && direction != Vector2.Zero)
                foreach (BuildingView building in view.Buildings)
                {
                    if (!building.BlocksMovement || building.Position == target ||
                        CircleCollision.Limit(view.LegionPosition, direction * rules.SlotSpacing, building.Position, building.Radius) == 1) continue;
                    Vector2 outward = Vector2.Normalize(view.LegionPosition - building.Position);
                    Vector2 tangent = new Vector2(-outward.Y, outward.X);
                    direction = Vector2.Dot(tangent, direction) < 0 ? -tangent : tangent;
                    break;
                }
            if (hasDirection && direction == previousDirection) return null;
            hasDirection = true; previousDirection = direction;
            return SimCommand.Move(++sequence, direction);
        }
    }
}
