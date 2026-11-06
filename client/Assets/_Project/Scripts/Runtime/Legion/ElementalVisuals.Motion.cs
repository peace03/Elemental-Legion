using System.Collections.Generic;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    public sealed partial class ElementalVisuals
    {
        private struct MotionState
        {
            internal Vector3 Position, Velocity;
            internal float LastTime, AttackAt, HitAt, ConversionAt, FacingYaw;
            internal int Seen;
            internal bool HasPosition;
        }

        private const float FacingMovementSpeed = .05f / .18f;
        [SerializeField, Min(1), Tooltip("전장 원소 몸체가 이동 방향을 따라 도는 최대 속도(도/초)입니다.")]
        private float facingTurnDegreesPerSecond = 360;
        private readonly Dictionary<int, MotionState> motions = new Dictionary<int, MotionState>(Protocol.MaxUnits * 2);
        private readonly List<int> unseenMotion = new List<int>(Protocol.MaxUnits);
        private int motionVersion;

        private void ConfigureParticleSheet(ParticleSystem system,ElementArtProfile profile,bool enabled)
        {
            var sheet=system.textureSheetAnimation;sheet.enabled=enabled;
            if(!enabled)return;
            sheet.numTilesX=Mathf.Clamp(profile.ParticleColumns,1,16);
            sheet.numTilesY=Mathf.Clamp(profile.ParticleRows,1,16);
            sheet.animation=ParticleSystemAnimationType.WholeSheet;
            sheet.cycleCount=1;
            sheet.frameOverTime=new ParticleSystem.MinMaxCurve(1,particleFrames);
        }

        private MotionState TrackMotion(int id, int element, Vector3 position, float seconds)
        {
            motions.TryGetValue(id, out MotionState state);
            float elapsed = seconds - state.LastTime;
            float spin = profiles[element].SpinSpeed;
            if (!state.HasPosition) state.FacingYaw = Mathf.Repeat(id * 137.508f + seconds * spin, 360);
            if (state.HasPosition && elapsed > .0001f)
            {
                Vector3 velocity = (position - state.Position) / elapsed;
                velocity.y = 0;
                state.Velocity = Vector3.Lerp(state.Velocity, Vector3.ClampMagnitude(velocity, 12), 1 - Mathf.Exp(-elapsed * 14));
                state.FacingYaw = AdvanceFacingYaw(state.FacingYaw, state.Velocity, spin, elapsed, facingTurnDegreesPerSecond);
            }
            state.Position = position;
            state.LastTime = seconds;
            state.Seen = motionVersion;
            state.HasPosition = true;
            motions[id] = state;
            return state;
        }

        internal static float AdvanceFacingYaw(float current, Vector3 velocity, float idleSpin, float elapsed, float turnRate)
        {
            if (velocity.sqrMagnitude <= FacingMovementSpeed * FacingMovementSpeed)
                return Mathf.Repeat(current + idleSpin * elapsed, 360);
            float target = Mathf.Atan2(velocity.x, velocity.z) * Mathf.Rad2Deg;
            return Mathf.Repeat(Mathf.MoveTowardsAngle(current, target, Mathf.Max(1, turnRate) * elapsed), 360);
        }

        private void CullMotion()
        {
            unseenMotion.Clear();
            foreach (var pair in motions) if (pair.Value.Seen != motionVersion) unseenMotion.Add(pair.Key);
            foreach (int id in unseenMotion) motions.Remove(id);
        }

        private void ReactBody(SimEvent data)
        {
            bool attack = data.Kind == SimEventKind.Attack && data.SourceKind == ObservedEntityKind.Unit;
            bool hit = (data.Kind == SimEventKind.Impact || data.Kind == SimEventKind.Converted) && data.EntityKind == ObservedEntityKind.Unit;
            if (data.Kind == SimEventKind.Death && data.EntityKind == ObservedEntityKind.Unit) motions.Remove(data.EntityId);
            if (!attack && !hit) return;
            int id = attack ? data.SourceId : data.EntityId;
            if (id <= 0) return;
            motions.TryGetValue(id, out MotionState state);
            if (attack) state.AttackAt = Now;
            else if (data.Kind == SimEventKind.Converted) state.ConversionAt = Now;
            else state.HitAt = Now;
            motions[id] = state;
        }

        private static float Pulse(float seconds, float at, float duration)
        {
            float phase = (seconds - at) / Mathf.Max(.05f, duration);
            return at > 0 && phase >= 0 && phase < 1 ? Mathf.Sin(phase * Mathf.PI) : 0;
        }

        private Matrix4x4 BodyPose(int element, int identity, Vector3 position, Vector3 velocity, float seconds,
            float footprint, float attack = 0, float hit = 0, float conversion = 0)
        {
            float movement = Mathf.Clamp01(velocity.magnitude * .18f);
            float yaw = identity * 137.508f + seconds * profiles[element].SpinSpeed;
            if (movement > .05f)
                yaw = Mathf.LerpAngle(yaw, Mathf.Atan2(velocity.x, velocity.z) * Mathf.Rad2Deg, Mathf.SmoothStep(0,1,movement*2));
            return BodyPoseFacing(element, identity, position, movement, seconds, yaw, footprint, attack, hit, conversion);
        }

        private Matrix4x4 RuntimeBodyPose(int element, int identity, Vector3 position, MotionState motion, float seconds,
            float footprint, float attack = 0, float hit = 0, float conversion = 0)
        {
            float movement = Mathf.Clamp01(motion.Velocity.magnitude * .18f);
            float yaw = motion.HasPosition ? motion.FacingYaw : identity * 137.508f + seconds * profiles[element].SpinSpeed;
            return BodyPoseFacing(element, identity, position, movement, seconds, yaw, footprint, attack, hit, conversion);
        }

        private Matrix4x4 BodyPoseFacing(int element, int identity, Vector3 position, float movement, float seconds, float yaw,
            float footprint, float attack, float hit, float conversion)
        {
            var profile = profiles[element];
            float phase = identity * 2.399963f + profile.Style * .37f;
            float breath = Mathf.Sin(seconds * profile.MotionFrequency + phase);
            float height = 1 + breath * profile.PulseAmplitude + attack * profile.AttackRecoil - hit * .22f + conversion * .24f;
            height += movement * (profile.Style == 1 || profile.Style == 2 ? .18f : .06f);
            float scale = bodySize * profile.Size;
            float stretch=1+movement*Mathf.Clamp(profile.MovementStretch,0,1.6f);
            float width = Mathf.Min(scale * (1 - attack * .18f - movement * .08f), footprint / (bodyRadii[element]*stretch));
            position.y += profile.HoverAmplitude * breath + attack * .08f - hit * .05f;
            return Matrix4x4.TRS(position, Quaternion.Euler(0, yaw, 0), new Vector3(width, scale * height, width*stretch));
        }

        private void BodyParticle(int style, int index, float seconds, float phase, float angle,
            out Vector3 offset, out float size)
        {
            float radius = .32f;
            size = .12f;
            float height = Mathf.Sin(angle * 1.7f) * .2f;
            switch (style)
            {
                case 0: radius = .38f+phase*.14f; size = .035f+.055f*(1-phase); height = Mathf.Sin(angle * 1.8f) * .35f; break;
                case 1: radius = .08f + phase * .14f; size = .62f * (1 - phase) + .24f; height = -.32f + phase * .95f; break;
                case 2: radius = .04f+phase*.2f; angle += phase*Mathf.PI*4; size=.3f+phase*.28f; height=-.26f+phase*.75f; break;
                case 3: radius = .1f+Mathf.Sin(phase*Mathf.PI)*.15f; size=.65f+Mathf.Sin(phase*Mathf.PI)*.1f; height=Mathf.Sin(angle*1.7f)*.22f+phase*.1f; break;
                case 4: radius = .42f; size = .1f + .06f * Mathf.Sin(phase * Mathf.PI); height = Mathf.Sin(angle * 2) * .3f; break;
                case 5: radius = .46f; size = .08f; height = -.4f + phase * .35f; break;
                case 6: radius = .24f + .18f * Mathf.Sin(Mathf.Floor(seconds * 13) + index); size = .08f + .08f * (1 - phase); height = Mathf.Sin(angle * 3 + seconds * 17) * .45f; break;
                case 7: radius = index<2?0:.08f+phase*.22f; size = index<2?.3f:.055f; height = index<2?0:Mathf.Sin(angle)*.2f; break;
                case 8: radius = .4f; size = .1f; height = Mathf.Sin(angle + seconds) * .45f; break;
                case 9: radius = .15f + phase * .35f; size = .1f * (1 - phase) + .04f; height = Mathf.Sin(angle * 2) * .35f; break;
                case 10: radius = .18f + phase * .32f; angle -= seconds * 3; size = .018f+.04f*(1-phase); height = Mathf.Sin(angle)*.08f; break;
                case 11: radius = index==0?0:.15f+(1-phase)*.3f; size=index==0?.14f:.012f+.025f*(1-phase); height=index==0?0:Mathf.Sin(angle*1.7f)*.24f; break;
                case 12:
                    offset = new Vector3(Mathf.Sin(angle) * .45f, Mathf.Sin(angle * 2) * .3f, Mathf.Cos(angle) * .2f);
                    return;
                case 13: radius = .2f + phase * .25f; size = .06f + .07f * Mathf.Sin(phase * Mathf.PI); height = -.25f + phase * .95f; break;
            }
            offset = new Vector3(Mathf.Cos(angle) * radius, height, Mathf.Sin(angle) * radius);
        }
    }
}
