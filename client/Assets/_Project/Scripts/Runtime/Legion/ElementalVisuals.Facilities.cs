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
        private MaterialPropertyBlock[][] facilityProperties;
        private MaterialPropertyBlock facilityPreview;
        private static readonly int TeamColor = Shader.PropertyToID("_TeamColor");

        private void InitializeFacilities()
        {
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

        private bool DrawFacility(BuildingKind kind, Vector3 center, float radius, MaterialPropertyBlock properties, bool shadows)
        {
            if (facilityModels == null || !facilityModels.TryGet(kind, out var model)) return false;
            Matrix4x4 placement = Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * (radius / model.HorizontalRadius));
            var casting = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            foreach (var part in model.Parts)
            {
                Matrix4x4 matrix = placement * part.LocalMatrix;
                Graphics.DrawMesh(part.Mesh, matrix, part.Material, gameObject.layer, targetCamera, part.Submesh, properties, casting, true);
#if UNITY_EDITOR
                foreach (var camera in sceneViewCameras)
                    Graphics.DrawMesh(part.Mesh, matrix, part.Material, gameObject.layer, camera, part.Submesh, properties, casting, true);
#endif
            }
            return true;
        }

        private float FacilityHeight(BuildingKind kind, float radius)
        {
            if (facilityModels != null && facilityModels.TryGet(kind, out var model))
                return model.Height * radius / model.HorizontalRadius;
            return IsTower(kind) ? towerHeight : BuildingHeight(kind);
        }

        private void DrawFacilityCore(BuildingView building, Vector3 center, bool ghost)
        {
            if (!IsTower(building.Kind) || !ValidElement(building.TowerElement) || building.IsUnderConstruction) return;
            var profile = profiles[building.TowerElement];
            Vector3 point = center + Vector3.up * (FacilityHeight(building.Kind, building.Radius) + .15f);
            Matrix4x4 pose = BodyPose(building.TowerElement, building.Id, point, Vector3.zero, Now, building.Radius * .25f);
            DrawOne(profile.Mesh, profile.Body, pose, ghost ? particleGhostProperties : particleFallbackProperties);
        }

        private float BuildingAttackHeight(int id)
        {
            if (facilityModels != null && observation != null && observation.SpatialReady)
                foreach (var building in observation.Buildings)
                    if (building.Id == id && facilityModels.TryGet(building.Kind, out _))
                        return FacilityHeight(building.Kind, building.Radius) + .35f;
            return TowerHeadHeight + .08f;
        }
    }
}
