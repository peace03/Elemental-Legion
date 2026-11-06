using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    public sealed partial class ElementalVisuals
    {
        [Header("전향·핵 드롭")]
        [SerializeField, Range(1, 64), Tooltip("전향 때 새 소유자 색으로 솟는 입자 수입니다.")]
        private int conversionParticles = 20;
        [SerializeField, Min(.1f), Tooltip("전향 효과 지속 시간(초)입니다.")]
        private float conversionDuration = .95f;
        [SerializeField, Min(.05f), Tooltip("전향 입자가 모이기 시작하는 반경(m)입니다.")]
        private float conversionRadius = .9f;
        [SerializeField, Min(.05f), Tooltip("전향 입자가 몸체 중심 위로 모이는 높이(m)입니다.")]
        private float conversionRise = 1.25f;
        [SerializeField, Min(.05f), Tooltip("전향 빛줄기 입자의 기본 폭(m)입니다.")]
        private float conversionStreakWidth = .24f;
        [SerializeField, Range(1, 64), Tooltip("핵이 떨어질 때 낮게 튀는 결정 파편 수입니다.")]
        private int coreDropParticles = 12;
        [SerializeField, Min(.1f), Tooltip("핵 드롭 효과 지속 시간(초)입니다.")]
        private float coreDropDuration = .68f;
        [SerializeField, Min(.05f), Tooltip("핵 드롭 파편의 최대 확산 반경(m)입니다.")]
        private float coreDropRadius = 1.15f;
        [SerializeField, Min(.05f), Tooltip("핵 드롭 파편이 시작 높이 위로 튀어 오르는 높이(m)입니다.")]
        private float coreDropArc = .26f;
        [SerializeField, Min(.05f), Tooltip("핵 드롭 파편의 최대 기본 크기(m)입니다.")]
        private float coreDropShardSize = .52f;
        [SerializeField, Tooltip("핵 드롭 파편의 기본 호박빛입니다.")]
        private Color coreDropTint = new Color(1f, .56f, .12f);

        private static bool IsOutcome(SimEventKind? cue) => cue == SimEventKind.Converted || cue == SimEventKind.CoreDropped;

        private void EmitOutcome(SimEventKind cue, Vector3 point, ObservedEntityId entity, ObservedEntityId source, Color ownerTint)
        {
            bool conversion = cue == SimEventKind.Converted;
            int amount = Mathf.Clamp(conversion ? conversionParticles : coreDropParticles, 1, 64);
            Effect effect = AcquireEffect(true, cue, ref amount, out _);
            if (effect == null) return;

            float lifetime = Mathf.Max(.1f, conversion ? conversionDuration : coreDropDuration);
            var profile = profiles[conversion ? lightIndex : crystalIndex];
            var renderer = effect.Renderer;
            renderer.renderMode = conversion ? ParticleSystemRenderMode.Billboard : ParticleSystemRenderMode.Mesh;
            renderer.alignment = conversion ? ParticleSystemRenderSpace.View : ParticleSystemRenderSpace.World;
            renderer.mesh = conversion ? null : profile.FragmentMesh != null ? profile.FragmentMesh : crystal;
            renderer.sharedMaterial = conversion ? profile.Particles : profile.Body;
            effectProperties.Clear();
            Color tint = conversion ? ownerTint : coreDropTint;
            effectProperties.SetColor(BaseColor, tint);
            effectProperties.SetColor(SecondColor, Color.Lerp(tint, Color.white, conversion ? .2f : .25f) * (conversion ? 1.45f : 1.7f));
            if (!conversion)
            {
                // 결정 파편의 바위 면에도 핵 드롭 색이 남게 한다.
                effectProperties.SetFloat("_OwnershipTint", 1);
                effectProperties.SetTexture("_SurfaceTex", Texture2D.whiteTexture);
                effectProperties.SetFloat("_Transmission", 0);
                effectProperties.SetFloat("_AmbientFill", .8f);
            }
            renderer.SetPropertyBlock(effectProperties);

            float radius = Mathf.Max(.05f, conversion ? conversionRadius : coreDropRadius);
            float rise = Mathf.Max(.05f, conversionRise);
            float shardSize = Mathf.Max(.05f, coreDropShardSize);
            float streakWidth = Mathf.Max(.05f, conversionStreakWidth);
            float gravity = conversion ? 0 : 8 * Mathf.Max(.05f, coreDropArc) / (lifetime * lifetime);
            var main = effect.System.main;
            main.maxParticles = 256;
            main.startRotation3D = !conversion;
            main.startSize3D = conversion;
            main.gravityModifier = gravity / Mathf.Max(.01f, -Physics.gravity.y);
            var tumble = effect.System.rotationOverLifetime;
            tumble.enabled = !conversion;
            tumble.separateAxes = true;
            tumble.x = 2.7f; tumble.y = 4.3f; tumble.z = 1.8f;
            ConfigureParticleSheet(effect.System, profile, false);

            effect.Entity = entity;
            effect.Source = source;
            effect.Cue = cue;
            effect.Critical = true;
            effect.Reaction = -1;
            effect.Projectile = false;
            effect.Started = Now;
            effect.Lifetime = lifetime;
            effect.Until = effect.Started + lifetime;
            effect.Position = point;
            effect.System.Play();

            var parameters = new ParticleSystem.EmitParams { startColor = Color.white };
            var ground = point - Vector3.up * bodyHeight;
            for (int index = 0; index < amount; index++)
            {
                float angle = index * 2.399963f;
                float spread = Mathf.Sqrt((index + .5f) / amount);
                float grain = (index * 17 % 31) / 30f;
                Vector3 radial = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                float duration = lifetime * Mathf.Lerp(.72f, 1, grain);
                parameters.startLifetime = duration;
                if (conversion)
                {
                    Vector3 offset = radial * (radius * Mathf.Lerp(.35f, 1, spread));
                    parameters.position = ground + Vector3.up * (bodyHeight * (.24f + grain * .37f)) + offset;
                    parameters.velocity = (point + Vector3.up * (rise * Mathf.Lerp(.65f, 1, spread)) - parameters.position) / duration;
                    float width = streakWidth * Mathf.Lerp(.62f, 1, grain);
                    parameters.startSize3D = new Vector3(width, width * 3.1f, width);
                }
                else
                {
                    float size = shardSize * Mathf.Lerp(.44f, 1, grain);
                    parameters.position = ground + Vector3.up * (size * .6f) + radial * (radius * .14f * spread);
                    parameters.velocity = radial * (radius * .86f * spread / duration) + Vector3.up * (gravity * duration * .5f);
                    parameters.startSize = size;
                    parameters.rotation3D = new Vector3(index * 1.17f, index * 2.39f, index * .71f);
                }
                effect.System.Emit(parameters, 1);
            }
            auxiliaryParticles += amount;
        }

        private Effect AcquireEffect(bool critical, SimEventKind? cue, ref int amount, out bool append)
        {
            append = false;
            if (pool == null) return null;
            bool outcome = IsOutcome(cue);
            int limit = critical ? pool.Length : Mathf.FloorToInt(pool.Length * (1 - criticalReserve));
            float now = Now;
            Effect effect = null;
            for (int index = 0; index < limit; index++)
                if (pool[index].Until <= now) { effect = pool[index]; break; }

            if (effect == null && critical)
            {
                Effect otherCritical = null, oldestOutcome = null;
                foreach (Effect candidate in pool)
                {
                    if (!candidate.Critical) { effect = candidate; break; }
                    if (!IsOutcome(candidate.Cue)) otherCritical = candidate;
                    else if (oldestOutcome == null || candidate.Until < oldestOutcome.Until) oldestOutcome = candidate;
                }
                // 서로 다른 궤적과 소유색을 하나의 파티클 렌더러에 섞지 않는다.
                if (effect == null && outcome) effect = otherCritical ?? oldestOutcome;
                else if (effect == null && otherCritical != null)
                {
                    effect = otherCritical;
                    amount = 1;
                    append = ActiveParticles < maxParticles;
                }
            }
            if (effect == null || !critical && ActiveParticles + amount > maxParticles)
            {
                DecorationDrops++;
                return null;
            }
            if (!append) ClearEffect(effect);
            if (critical && ActiveParticles + amount > maxParticles)
            {
                foreach (Effect candidate in pool)
                {
                    if (!candidate.Critical && candidate.System.particleCount > 0) ClearEffect(candidate);
                    if (ActiveParticles + amount <= maxParticles) break;
                }
                foreach (var layer in bodyLayers)
                {
                    if (ActiveParticles + amount <= maxParticles) break;
                    bodyParticleCount -= layer.particleCount;
                    layer.Clear();
                }
                if (outcome && ActiveParticles + amount > maxParticles)
                    foreach (Effect candidate in pool)
                    {
                        if (candidate.Critical && !IsOutcome(candidate.Cue) && candidate.System.particleCount > 0)
                            ClearEffect(candidate);
                        if (ActiveParticles + amount <= maxParticles) break;
                    }
                amount = Mathf.Min(amount, maxParticles - ActiveParticles);
            }
            if (amount <= 0) return null;
            effect.Aggregated = append;
            return effect;
        }

        private void ClearEffect(Effect effect)
        {
            auxiliaryParticles = Mathf.Max(0, auxiliaryParticles - effect.System.particleCount);
            effect.System.Clear();
            effect.Until = 0;
            effect.Aggregated = false;
        }
    }
}
