using System;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Client.Legion
{
    /// <summary>내 군단 중심만 예측한다. 서버 확정 위치에 미확인 입력을 재적용하며 게임 판정은 하지 않는다.</summary>
    public sealed class MovementPrediction
    {
        private readonly GameRules rules;
        private readonly PendingInput[] pending = new PendingInput[256];
        private int head, count;
        private long lastTick = -1;
        private ulong lastSequence;
        private byte squadId = 1;
        private uint movementRevision;
        private Vector2 displayOffset;
        private Vector2? lastDisplayed;
        private bool followsObservation;
        private readonly BuildingView[] buildings = new BuildingView[GameplayCodec.MaxBuildings];
        private int buildingCount;
        private Vector2 basePosition;
        private bool hasBasePosition;
        public Vector2 Position { get; private set; }
        public Vector2 ConfirmedPosition { get; private set; }
        public int PendingCount => count;
        public bool Ready => lastTick >= 0;
        internal bool FollowsObservation => followsObservation;

        public MovementPrediction(GameRules rules) { this.rules = rules ?? throw new ArgumentNullException(nameof(rules)); }

        /// <summary>서버가 보낸 본진을 수비대 예측 범위의 기준으로 사용한다.</summary>
        public void SetBasePosition(Vector2 position)
        {
            if(!Protocol.Finite(position)||position.X<0||position.Y<0||position.X>rules.MapSize||position.Y>rules.MapSize)
                throw new ArgumentOutOfRangeException(nameof(position));
            basePosition=position;hasBasePosition=true;
        }

        public void SetBuildings(ReadOnlySpan<BuildingView> observed)
        {
            if(observed.Length>buildings.Length)throw new ArgumentOutOfRangeException(nameof(observed));
            buildingCount=0;
            foreach (BuildingView building in observed)
                if (building.BlocksMovement) buildings[buildingCount++]=building;
        }

        public bool Push(ulong sequence, Vector2 direction)
        {
            if (!Protocol.Finite(direction)) throw new ArgumentException("방향은 유한해야 합니다.", nameof(direction));
            if (!Ready || sequence == 0 || sequence <= lastSequence || count == pending.Length) return false;
            direction = Normalize(direction);
            pending[(head + count) % pending.Length] = new PendingInput(sequence, direction); count++; lastSequence = sequence;
            Position = Advance(Position, direction, rules.DeltaTime);
            return true;
        }

        /// <summary>분대 선택·승계·불연속 이동 때 이전 입력과 표시 보정을 함께 버린다.</summary>
        public void Reset(LegionPose pose)
        {
            head = count = 0;
            lastTick = -1;
            lastSequence = pose.AppliedSequence;
            displayOffset = Vector2.Zero;
            lastDisplayed = null;
            followsObservation = false;
            squadId = pose.SquadId;
            movementRevision = pose.MovementRevision;
            Reconcile(pose);
        }

        public void Reconcile(LegionPose pose)
        {
            if (pose.SquadId != squadId) return;
            if (pose.Tick <= lastTick) return;
            if (Ready && pose.MovementRevision != movementRevision) { Reset(pose); return; }
            movementRevision = pose.MovementRevision;
            bool hadPose = Ready;
            Vector2 previous = Position;
            lastTick = pose.Tick;
            while (count > 0 && pending[head].Sequence <= pose.AppliedSequence) { head = (head + 1) % pending.Length; count--; }
            ConfirmedPosition = Position = pose.Position;
            for (int index = 0; index < count; index++) Position = Advance(Position, pending[(head + index) % pending.Length].Direction, rules.DeltaTime);
            if (!hadPose || followsObservation) return;
            displayOffset += previous - Position;
            LimitDisplayOffset();
        }

        /// <summary>입력 틱 사이의 표시 보간이다. 대기 시간이 길어도 한 틱을 넘겨 외삽하지 않는다.</summary>
        public Vector2 Sample(Vector2 direction, double remainder) => Advance(Position, Normalize(direction), Math.Max(0, Math.Min(rules.DeltaTime, remainder)));

        /// <summary>직접 입력이 끝나면 원소와 같은 시점의 서버 위치를 따라간다.</summary>
        public Vector2 SampleDisplay(Vector2 direction, double remainder, double frameSeconds, Vector2? observedCenter = null)
        {
            bool useObservation = observedCenter.HasValue && direction == Vector2.Zero && count == 0;
            Vector2 sample = useObservation ? observedCenter.GetValueOrDefault() : Sample(direction, remainder);
            if (useObservation != followsObservation)
            {
                // 입력 예측과 관측 사이에서 기준이 바뀌는 프레임에도 화면 위치를 이어 간다.
                displayOffset = lastDisplayed.HasValue ? lastDisplayed.Value - sample : Vector2.Zero;
                LimitDisplayOffset();
                followsObservation = useObservation;
            }
            float distance = displayOffset.Length();
            if (distance > 0)
            {
                float correction = rules.LegionSpeed * 0.5f * (float)Math.Max(0, frameSeconds);
                displayOffset = distance <= correction ? Vector2.Zero : displayOffset * (1 - correction / distance);
                Vector2 corrected = MovePoint(sample, sample + displayOffset);
                displayOffset = corrected - sample;
                sample = corrected;
            }
            lastDisplayed = sample;
            return sample;
        }

        private void LimitDisplayOffset()
        {
            float limit = rules.CameraVisionPadding * 0.5f;
            if (displayOffset.LengthSquared() > limit * limit) displayOffset = Vector2.Normalize(displayOffset) * limit;
        }

        private Vector2 Advance(Vector2 origin, Vector2 direction, double seconds) =>
            MovePoint(origin, origin + direction * (rules.LegionSpeed * (float)seconds));

        private Vector2 MovePoint(Vector2 origin, Vector2 destination)
        {
            destination=Vector2.Clamp(destination,Vector2.Zero,new Vector2(rules.MapSize));
            if(squadId==4&&hasBasePosition)
            {
                Vector2 offset=destination-basePosition;
                if(offset.LengthSquared()>rules.GarrisonRadius*rules.GarrisonRadius)
                    destination=basePosition+Vector2.Normalize(offset)*rules.GarrisonRadius;
            }
            Vector2 delta=destination-origin;float fraction=1;
            for(int index=0;index<buildingCount;index++)fraction=CircleCollision.Limit(origin,delta,buildings[index].Position,buildings[index].Radius,fraction);
            return origin+delta*fraction;
        }
        private static Vector2 Normalize(Vector2 direction)
        {
            if (!Protocol.Finite(direction)) return Vector2.Zero;
            float scale = Math.Max(Math.Abs(direction.X), Math.Abs(direction.Y));
            return scale > 0 ? Vector2.Normalize(direction / scale) : Vector2.Zero;
        }

        private readonly struct PendingInput
        {
            public ulong Sequence { get; }
            public Vector2 Direction { get; }
            public PendingInput(ulong sequence, Vector2 direction) { Sequence = sequence; Direction = direction; }
        }
    }
}
