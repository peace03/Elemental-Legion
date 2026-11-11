using System;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace ElementalLegion.Client.Legion
{
    /// <summary>서버가 관측시킨 시설에만 제공 모델의 부품을 그린다.</summary>
    public sealed partial class ElementalVisuals
    {
        [Header("제공 시설 모델")]
        [SerializeField, Tooltip("원시핵·응집로·점화로·포집기·수호 위성의 실제 메시와 재질입니다.")]
        private FacilityModelCatalog facilityModels;
        [SerializeField, Range(.2f, 2), Tooltip("시설 인셋에 적용할 소유 색상의 밝기입니다.")]
        private float facilityTeamBrightness = 1;

        [Header("시설 주변의 빛")]
        [SerializeField, ColorUsage(false, true), Tooltip("원시핵의 중심에서 금속으로 퍼지는 청록빛입니다.")]
        private Color nucleusLocalLight = new Color(.24f, 2.7f, 3.6f);
        [SerializeField, ColorUsage(false, true), Tooltip("포집기 흡입구에서 금속으로 퍼지는 대기 중 금빛입니다.")]
        private Color collectorLocalLight = new Color(1.75f, .82f, .24f);
        [SerializeField, ColorUsage(false, true), Tooltip("응집로의 유동선에서 프레임으로 퍼지는 푸른빛입니다.")]
        private Color cohesionLocalLight = new Color(.14f, 1.65f, 3.15f);
        [SerializeField, ColorUsage(false, true), Tooltip("점화로의 수정에서 고리와 받침으로 퍼지는 보랏빛입니다.")]
        private Color ignitionLocalLight = new Color(1.35f, .27f, 3.0f);
        [SerializeField, Range(0, 4), Tooltip("수호 위성의 배치 원소가 받침을 비추는 강도입니다.")]
        private float towerLocalLight = 2.4f;
        [SerializeField, Range(0, 2), Tooltip("원시핵과 점화로의 작은 수정이 스스로 내는 빛의 강도입니다.")]
        private float facilityInlayGlow = 1;
        [SerializeField, Range(1, 10), Tooltip("시설의 밝기가 서서히 오르내리는 주기(초)입니다.")]
        private float facilityLightPeriod = 4.2f;
        [SerializeField, Tooltip("시설 아래 지면을 비추는 인스턴싱 재질입니다.")]
        private Material facilityGroundGlow;
        [SerializeField, Range(0, .15f), Tooltip("시설의 빛이 바닥에 번지는 강도입니다.")]
        private float facilityGroundGlowStrength = .052f;
        [SerializeField, Range(1, 2.5f), Tooltip("시설 반경에 대한 바닥 빛의 범위입니다.")]
        private float facilityGroundGlowRange = 1.85f;

        private const int GroundGlowBatchSize = 256;
        private MaterialPropertyBlock[][] facilityProperties;
        private MaterialPropertyBlock facilityPreview;
        private MaterialPropertyBlock facilityGroundProperties;
        private readonly Matrix4x4[] facilityGroundMatrices = new Matrix4x4[GroundGlowBatchSize];
        private readonly Vector4[] facilityGroundColors = new Vector4[GroundGlowBatchSize];
        private int facilityGroundCount;
        private static readonly int TeamColor = Shader.PropertyToID("_TeamColor");
        private static readonly int FacilityLightSource = Shader.PropertyToID("_FacilityLightSource");
        private static readonly int FacilityLightColor = Shader.PropertyToID("_FacilityLightColor");
        private static readonly int FacilityInlayGlow = Shader.PropertyToID("_FacilityInlayGlow");
        private static readonly int FacilityCrystal = Shader.PropertyToID("_FacilityCrystal");
        private static readonly int GlowColor = Shader.PropertyToID("_GlowColor");

        private void InitializeFacilities()
        {
            if (facilityModels != null && (nucleusGem == null || nucleusGemMaterial == null ||
                cohesionCurrents == null || cohesionCurrentsMaterial == null || collectorIntakeMaterial == null))
                throw new InvalidOperationException("시설 발광과 포집 파티클의 프리팹 참조를 확인하세요.");
            if (facilityModels != null && (facilityGroundGlow == null || !facilityGroundGlow.enableInstancing))
                throw new InvalidOperationException("시설 바닥 발광 재질과 GPU 인스턴싱 설정을 확인하세요.");
            facilityGroundProperties = new MaterialPropertyBlock();
            facilityProperties = new MaterialPropertyBlock[PlayerPalette.Count + 1][];
            for (int slot = 0; slot < facilityProperties.Length; slot++)
            {
                var states = new MaterialPropertyBlock[3];
                facilityProperties[slot] = states;
                for (int state = 0; state < states.Length; state++)
                {
                    states[state] = new MaterialPropertyBlock();
                    states[state].SetColor(TeamColor, PlayerColors.Get(slot) * facilityTeamBrightness);
                    states[state].SetFloat(Opacity, state == 1 ? ghostOpacity : state == 2 ? unfinishedOpacity : 1);
                }
            }
            facilityPreview = new MaterialPropertyBlock();
            facilityPreview.SetFloat(Opacity, previewOpacity);
        }

        private bool DrawFacility(BuildingKind kind, Vector3 center, float radius, MaterialPropertyBlock properties,
            bool operational, int id = 0, float seconds = 0, int towerElement = -1)
        {
            if (facilityModels == null || !facilityModels.TryGet(kind, out var model)) return false;
            float scale = radius / model.HorizontalRadius;
            Matrix4x4 placement = Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * scale);
            var casting = operational ? ShadowCastingMode.On : ShadowCastingMode.Off;
            bool collecting = operational && kind == BuildingKind.Collector;
            bool armedTower = operational && IsTower(kind) && ValidElement(towerElement);
            float flash = collecting ? CollectorGlow(id, seconds) : 0;
            Color glow = Color.clear;
            Vector4 glowCenter = Vector4.zero;
            if (collecting)
            {
                glow = collectorArrivalGlow * (.07f + flash);
                glowCenter = new Vector4(center.x, center.y + collectorWellHeight * scale, center.z, collectorGlowRadius * scale);
            }
            else if (armedTower)
            {
                glow = profiles[towerElement].Tint * towerSocketGlow;
                glowCenter = new Vector4(center.x, center.y + model.Height * scale, center.z, radius * .48f);
            }
            properties.SetVector(FacilityEmissionCenter, glowCenter);

            Color light = Color.clear;
            Vector4 source = Vector4.zero;
            float pulse = 0;
            if (operational)
            {
                pulse = 1 + .075f * Mathf.Sin(seconds * (2 * Mathf.PI / Mathf.Max(1, facilityLightPeriod)) + id * .618f);
                float height = model.Height * scale;
                (Color radiance, float offset, float range) = kind switch
                {
                    BuildingKind.Base => (nucleusLocalLight, height * nucleusGemHeight, 2.15f),
                    BuildingKind.NatureForge => (cohesionLocalLight, height * .47f, 1.65f),
                    BuildingKind.EnergyForge => (ignitionLocalLight, height * .56f, 1.95f),
                    BuildingKind.Collector => (collectorLocalLight * (1 + .8f * flash), collectorWellHeight * scale, 1.6f),
                    _ when armedTower => (profiles[towerElement].Tint * towerLocalLight, height + towerElementGap, 1.65f),
                    _ => (Color.clear, 0f, 1f)
                };
                light = radiance * pulse;
                float reach = radius * range;
                source = new Vector4(center.x, center.y + offset, center.z, 1 / Mathf.Max(.001f, reach * reach));
                if (light.maxColorComponent > 0) AddFacilityGroundGlow(center, radius, light);
            }
            properties.SetVector(FacilityLightSource, source);
            properties.SetVector(FacilityLightColor, light);

            foreach (var part in model.Parts)
            {
                Matrix4x4 local = part.LocalMatrix;
                Color emission = glow;
                bool ignitionCrystal = operational && part.Motion == FacilityModelCatalog.PartMotion.IgnitionCrystal;
                if (ignitionCrystal)
                    emission = ignitionCrystalGlow * pulse;
                else if (operational && part.Motion >= FacilityModelCatalog.PartMotion.IgnitionRingLower)
                {
                    int ringIndex = (int)part.Motion - (int)FacilityModelCatalog.PartMotion.IgnitionRingLower;
                    float phase = seconds * (2 * Mathf.PI / Mathf.Max(.5f, ignitionRingPeriod)) + ringIndex * 1.65f + id * .31f;
                    local.m13 += ringIndex * ignitionRingSpacing + ignitionRingLift * Mathf.Sin(phase);
                }
                bool hasInlays = kind == BuildingKind.Base ||
                    (kind == BuildingKind.EnergyForge && part.Motion == FacilityModelCatalog.PartMotion.None);
                properties.SetFloat(FacilityInlayGlow, hasInlays ? facilityInlayGlow * pulse : 0);
                properties.SetFloat(FacilityCrystal, ignitionCrystal ? 1 : 0);
                // HDR 광량은 sRGB 변환으로 강도가 다시 커지지 않도록 선형 값으로 전달한다.
                properties.SetVector(FacilityEmission, emission);
                Matrix4x4 matrix = placement * local;
                Graphics.DrawMesh(part.Mesh, matrix, part.Material, gameObject.layer, targetCamera, part.Submesh, properties, casting, true);
#if UNITY_EDITOR
                foreach (var camera in sceneViewCameras)
                    Graphics.DrawMesh(part.Mesh, matrix, part.Material, gameObject.layer, camera, part.Submesh, properties, casting, true);
#endif
            }
            if (operational) DrawFacilityEnergy(model, placement, id, seconds);
            return true;
        }

        private void AddFacilityGroundGlow(Vector3 center, float radius, Color radiance)
        {
            if (facilityGroundGlowStrength <= 0) return;
            center.y = .012f;
            int index = facilityGroundCount++;
            facilityGroundMatrices[index] = Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * (radius * facilityGroundGlowRange));
            facilityGroundColors[index] = radiance * facilityGroundGlowStrength;
            if (facilityGroundCount == GroundGlowBatchSize) DrawFacilityGroundGlows();
        }

        private void DrawFacilityGroundGlows()
        {
            if (facilityGroundCount == 0) return;
            facilityGroundProperties.SetVectorArray(GlowColor, facilityGroundColors);
            int layer = gameObject.layer;
            Graphics.DrawMeshInstanced(constructionRangeFill, 0, facilityGroundGlow, facilityGroundMatrices, facilityGroundCount,
                facilityGroundProperties, ShadowCastingMode.Off, false, layer, targetCamera, LightProbeUsage.Off);
#if UNITY_EDITOR
            foreach (var camera in sceneViewCameras)
                Graphics.DrawMeshInstanced(constructionRangeFill, 0, facilityGroundGlow, facilityGroundMatrices, facilityGroundCount,
                    facilityGroundProperties, ShadowCastingMode.Off, false, layer, camera, LightProbeUsage.Off);
#endif
            facilityGroundCount = 0;
        }

        private float FacilityHeight(BuildingKind kind, float radius)
        {
            if (facilityModels != null && facilityModels.TryGet(kind, out var model))
                return model.Height * radius / model.HorizontalRadius;
            return IsTower(kind) ? towerHeight : BuildingHeight(kind);
        }

        private float DrawFacilityCore(BuildingView building, Vector3 center, bool ghost, float seconds)
        {
            if (!IsTower(building.Kind) || !ValidElement(building.TowerElement) || building.IsUnderConstruction || building.Health <= 0) return 0;
            var profile = profiles[building.TowerElement];
            Matrix4x4 pose = TowerElementPose(building, center, ghost ? 0 : seconds, out float footprint);
            if (ghost)
                DrawOne(profile.Mesh, profile.Body, pose, particleGhostProperties);
            else
                AddBodyPose(building.TowerElement, building.Id, pose, footprint);
            return pose.m13 - center.y + profile.Mesh.bounds.max.y * pose.m11;
        }

        private Matrix4x4 TowerElementPose(BuildingView building, Vector3 center, float seconds, out float footprint)
        {
            var profile = profiles[building.TowerElement];
            footprint = building.Radius * towerElementRadius;
            float scale = footprint / bodyRadii[building.TowerElement];
            float gap = Mathf.Max(.06f, towerElementGap + towerElementLift * Mathf.Sin(seconds * 2.6f + building.Id * .63f));
            center.y += FacilityHeight(building.Kind, building.Radius) + gap - profile.Mesh.bounds.min.y * scale;
            return Matrix4x4.TRS(center, Quaternion.Euler(0, building.Id * 137.508f + seconds * profile.SpinSpeed, 0), Vector3.one * scale);
        }

        private float BuildingAttackHeight(int id)
        {
            if (facilityModels != null && observation != null && observation.SpatialReady)
                foreach (var building in observation.Buildings)
                    if (building.Id == id && facilityModels.TryGet(building.Kind, out _))
                    {
                        if (IsTower(building.Kind) && ValidElement(building.TowerElement))
                            return TowerElementPose(building, Vector3.up * .08f, building.GhostEndTick == 0 ? Now : 0, out _).m13;
                        return FacilityHeight(building.Kind, building.Radius) + .35f;
                    }
            return TowerHeadHeight + .08f;
        }
    }
}
