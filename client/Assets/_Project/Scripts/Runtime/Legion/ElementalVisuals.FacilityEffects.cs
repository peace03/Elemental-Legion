using System;
using System.Collections.Generic;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace ElementalLegion.Client.Legion
{
    public sealed partial class ElementalVisuals
    {
        [Header("시설 발광과 움직임")]
        [SerializeField, Tooltip("원시핵 중앙의 반경 1인 면각구입니다.")] private Mesh nucleusGem;
        [SerializeField, Tooltip("원시핵 결정의 HDR 발광 재질입니다.")] private Material nucleusGemMaterial;
        [SerializeField, Tooltip("응집로 모델 내부에 맞춘 푸른 유동선 메시입니다.")] private Mesh cohesionCurrents;
        [SerializeField, Tooltip("응집로 유동선의 흐름과 발광 재질입니다.")] private Material cohesionCurrentsMaterial;
        [SerializeField, Range(.15f, .55f), Tooltip("원시핵의 시설 반경에 대한 결정 반경 비율입니다.")] private float nucleusGemRadius = .36f;
        [SerializeField, Range(.3f, .75f), Tooltip("원시핵의 시설 높이에 대한 결정 중심 높이 비율입니다.")] private float nucleusGemHeight = .60f;
        [SerializeField, Range(0, 45), Tooltip("원시핵 결정이 초당 회전하는 각도입니다.")] private float nucleusGemSpin = 13f;
        [SerializeField, ColorUsage(false, true), Tooltip("점화로 수정 표면에 더하는 보라색 HDR 발광입니다.")] private Color ignitionCrystalGlow = new Color(.64f, .13f, 2.15f);
        [SerializeField, Range(0, .24f), Tooltip("점화로 금속 링의 모델 공간 상하 진폭입니다.")] private float ignitionRingLift = .14f;
        [SerializeField, Range(0, .6f), Tooltip("점화로 링 사이에 더하는 모델 공간 높이 차입니다.")] private float ignitionRingSpacing = .32f;
        [SerializeField, Range(.5f, 5f), Tooltip("점화로 금속 링의 부유 주기(초)입니다.")] private float ignitionRingPeriod = 3.1f;
        [SerializeField, Range(.15f, .65f), Tooltip("수호 위성 반경에 대한 배치 원소 반경 비율입니다.")] private float towerElementRadius = .60f;
        [SerializeField, Range(.08f, .7f), Tooltip("수호 위성 위에 원소를 띄울 간격(m)입니다.")] private float towerElementGap = .42f;
        [SerializeField, Range(0, .25f), Tooltip("수호 위성 원소의 상하 진폭(m)입니다.")] private float towerElementLift = .13f;
        [SerializeField, Range(0, 3), Tooltip("배치 원소의 색으로 비추는 수호 위성 중앙 받침의 밝기입니다.")] private float towerSocketGlow = 1.35f;

        [Header("포집기 흡입")]
        [SerializeField, Tooltip("포집 경로에 쓰는 절차적 금빛 입자 재질입니다.")] private Material collectorIntakeMaterial;
        [SerializeField, Range(8, MaxIntakeSparks), Tooltip("핵 하나가 분해될 때 흡입되는 입자 수입니다.")] private int collectorIntakeSparks = 38;
        [SerializeField, Range(.04f, .24f), Tooltip("금빛 입자의 기본 크기(m)입니다.")] private float collectorSparkSize = .13f;
        [SerializeField, Range(.3f, 1.5f), Tooltip("가까운 원소핵의 흡입 시간(초)입니다.")] private float collectorIntakeSeconds = .77f;
        [SerializeField, Range(.15f, 1.2f), Tooltip("모델 공간에서 포집기 중앙 흡입구의 높이입니다.")] private float collectorWellHeight = .47f;
        [SerializeField, Range(.3f, 1.5f), Tooltip("포집기 중앙에서 금빛이 퍼지는 모델 공간 반경입니다.")] private float collectorGlowRadius = .88f;
        [SerializeField, ColorUsage(false, true), Tooltip("핵이 닿았을 때 포집기에 더하는 금빛 HDR 발광입니다.")] private Color collectorArrivalGlow = new Color(3.0f, 1.35f, .23f);

        private const int MaxIntakes = 32;
        private const int MaxIntakeSparks = 48;
        private const float IntakeEnd = 1.24f;
        private static readonly int FacilityEmission = Shader.PropertyToID("_FacilityEmission");
        private static readonly int FacilityEmissionCenter = Shader.PropertyToID("_FacilityEmissionCenter");
        private readonly Dictionary<int, BuildingView> observedCollectors = new Dictionary<int, BuildingView>();
        private readonly CollectorIntake[] collectorIntakes = new CollectorIntake[MaxIntakes];
        private int collectorIntakeCount;
        private int collectorParticleCount;
        private ParticleSystem collectorParticles;
        private ParticleSystemRenderer collectorParticleRenderer;
        private ParticleSystem.Particle[] collectorParticleBuffer;

        private struct CollectorIntake
        {
            internal int BuildingId;
            internal int CoreId;
            internal Vector3 Origin;
            internal Vector3 Destination;
            internal Vector3 Across;
            internal float ArcHeight;
            internal float Started;
            internal float Duration;
        }

        private void InitializeFacilityEffects()
        {
            if (facilityModels == null) return;

            var instance = new GameObject("Collector Intake Particles") { layer = gameObject.layer };
            instance.transform.SetParent(poolRoot.transform, false);
            collectorParticles = instance.AddComponent<ParticleSystem>();
            collectorParticles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = collectorParticles.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.useUnscaledTime = true;
            main.maxParticles = MaxIntakes * MaxIntakeSparks;
            var emission = collectorParticles.emission;
            emission.enabled = false;
            collectorParticleRenderer = instance.GetComponent<ParticleSystemRenderer>();
            collectorParticleRenderer.sharedMaterial = collectorIntakeMaterial;
            collectorParticleRenderer.shadowCastingMode = ShadowCastingMode.Off;
            collectorParticleRenderer.receiveShadows = false;
            collectorParticleRenderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream>
            {
                ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV
            });
            collectorParticleBuffer ??= new ParticleSystem.Particle[MaxIntakes * MaxIntakeSparks];
        }

        private void ObserveFacilityEffects(GameplayFrame frame)
        {
            if (collectorParticles == null) return;
            if (frame == null || !frame.SpatialReady)
            {
                ClearFacilityEffects();
                return;
            }

            observedCollectors.Clear();
            foreach (var building in frame.Buildings)
                if (building.Kind == BuildingKind.Collector && building.Health > 0 &&
                    building.GhostEndTick == 0 && !building.IsUnderConstruction)
                    observedCollectors.Add(building.Id, building);

            bool removed = false;
            for (int index = collectorIntakeCount - 1; index >= 0; index--)
                if (!observedCollectors.ContainsKey(collectorIntakes[index].BuildingId))
                {
                    RemoveCollectorIntake(index);
                    removed = true;
                }
            if (removed) ClearCollectorParticles();
        }

        private bool TryStartCollectorIntake(SimEvent entry, float seconds)
        {
            if (collectorParticles == null || entry.SourceKind != ObservedEntityKind.Building ||
                entry.EntityKind != ObservedEntityKind.Core || entry.EntityId <= 0 ||
                !observedCollectors.TryGetValue(entry.SourceId, out var building) || building.OwnerId != entry.OwnerId ||
                !facilityModels.TryGet(BuildingKind.Collector, out var model)) return false;

            Vector3 origin = new Vector3(entry.Position.X, .7f, entry.Position.Y);
            float scale = building.Radius / model.HorizontalRadius;
            Vector3 destination = new Vector3(building.Position.X, .08f + collectorWellHeight * scale, building.Position.Y);
            Vector3 delta = destination - origin;
            float distance = delta.magnitude;
            if (collectorIntakeCount == collectorIntakes.Length) RemoveCollectorIntake(0);
            collectorIntakes[collectorIntakeCount++] = new CollectorIntake
            {
                BuildingId = building.Id,
                CoreId = entry.EntityId,
                Origin = origin,
                Destination = destination,
                Across = Vector3.Cross(Vector3.up, delta).normalized,
                ArcHeight = .36f + Mathf.Min(1.65f, distance * .12f),
                Started = seconds,
                Duration = Mathf.Max(.3f, collectorIntakeSeconds) + Mathf.Min(.48f, distance * .017f)
            };
            return true;
        }

        private void RenderCollectorIntakes(float seconds)
        {
            if (collectorParticles == null) return;
            int amount = Mathf.Clamp(collectorIntakeSparks, 8, MaxIntakeSparks);
            int limit = Mathf.Clamp(maxParticles - auxiliaryParticles, 0, collectorParticleBuffer.Length);
            int written = 0;
            for (int index = collectorIntakeCount - 1; index >= 0; index--)
            {
                ref CollectorIntake intake = ref collectorIntakes[index];
                float progress = (seconds - intake.Started) / intake.Duration;
                if (progress >= IntakeEnd)
                {
                    RemoveCollectorIntake(index);
                    continue;
                }
                if (progress < .37f)
                {
                    // 남은 결정이 작아지며 입자가 같은 위치에서 출발한다.
                    float remainder = 1 - Mathf.SmoothStep(0, 1, progress / .37f);
                    var corePose = Matrix4x4.TRS(intake.Origin, Quaternion.Euler(0, seconds * 85, 15),
                        Vector3.one * (.42f * remainder));
                    DrawOne(crystal, profiles[lightIndex].Body, corePose);
                }
                for (int spark = 0; spark < amount && written < limit; spark++)
                {
                    float fraction = spark / (float)(amount - 1);
                    float delay = fraction * .54f;
                    float travel = (progress - delay) / .64f;
                    if (travel < 0 || travel >= 1) continue;
                    float along = travel * travel * (1.4f - .4f * travel);
                    float spiral = intake.CoreId * .739f + spark * 2.399963f + travel * 6.2f;
                    float spread = (.12f + fraction * .30f) * (1 - travel);
                    float arc = Mathf.Sin(along * Mathf.PI);
                    Vector3 offset = intake.Across * (Mathf.Cos(spiral) * spread + arc * .32f) +
                        Vector3.up * (Mathf.Sin(spiral) * spread + arc * intake.ArcHeight);
                    float fade = Mathf.SmoothStep(.35f, 1, travel / .12f) *
                        (1 - Mathf.SmoothStep(0, 1, (travel - .91f) / .09f));
                    collectorParticleBuffer[written++] = new ParticleSystem.Particle
                    {
                        position = Vector3.LerpUnclamped(intake.Origin, intake.Destination, along) + offset,
                        startSize = collectorSparkSize * (.68f + (spark % 7) * .13f),
                        startColor = new Color(1, .78f + fraction * .20f, .42f + fraction * .48f, fade),
                        startLifetime = 1,
                        remainingLifetime = 1,
                        randomSeed = (uint)(spark + 1)
                    };
                }
            }
            if (written > 0 && !collectorParticles.isPlaying) collectorParticles.Play(false);
            if (written > 0 || collectorParticles.particleCount > 0)
                collectorParticles.SetParticles(collectorParticleBuffer, written);
            collectorParticleCount = written;
        }

        private float CollectorGlow(int id, float seconds)
        {
            float result = 0;
            for (int index = 0; index < collectorIntakeCount; index++)
            {
                ref CollectorIntake intake = ref collectorIntakes[index];
                if (intake.BuildingId != id) continue;
                float progress = (seconds - intake.Started) / intake.Duration;
                float rise = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.40f, .86f, progress));
                float fade = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.95f, IntakeEnd, progress));
                result = Mathf.Max(result, rise * fade);
            }
            return result;
        }

        private void ForgetFacilityEffects(ObservedEntityId entity)
        {
            if (entity.Kind != ObservedEntityKind.Building || !observedCollectors.Remove(entity.Id)) return;
            bool removed = false;
            for (int index = collectorIntakeCount - 1; index >= 0; index--)
                if (collectorIntakes[index].BuildingId == entity.Id)
                {
                    RemoveCollectorIntake(index);
                    removed = true;
                }
            if (removed) ClearCollectorParticles();
        }

        private void RemoveCollectorIntake(int index)
        {
            int following = --collectorIntakeCount - index;
            if (following > 0) Array.Copy(collectorIntakes, index + 1, collectorIntakes, index, following);
        }

        private void ClearCollectorParticles()
        {
            collectorParticleCount = 0;
            if (collectorParticles != null) collectorParticles.Clear(false);
        }

        private void ClearFacilityEffects()
        {
            collectorIntakeCount = 0;
            collectorParticleCount = 0;
            observedCollectors.Clear();
            if (collectorParticles != null)
                collectorParticles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void DrawFacilityEnergy(FacilityModelCatalog.Model model, Matrix4x4 placement, int id, float seconds)
        {
            if (model.Kind == BuildingKind.Base && nucleusGem != null)
            {
                Vector3 point = Vector3.up * (model.Height * nucleusGemHeight);
                Quaternion rotation = Quaternion.Euler(13, seconds * nucleusGemSpin + id * 23, 9);
                Matrix4x4 gem = Matrix4x4.TRS(point, rotation, Vector3.one * (model.HorizontalRadius * nucleusGemRadius));
                DrawOne(nucleusGem, nucleusGemMaterial, placement * gem);
            }
            else if (model.Kind == BuildingKind.NatureForge && cohesionCurrents != null)
            {
                DrawOne(cohesionCurrents, cohesionCurrentsMaterial, placement);
            }
        }
    }
}
