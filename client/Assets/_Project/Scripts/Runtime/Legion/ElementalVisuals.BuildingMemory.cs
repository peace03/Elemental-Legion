using ElementalLegion.Shared.Sim;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    public sealed partial class ElementalVisuals
    {
        [Header("마지막 시설 관측")]
        [SerializeField, Range(.1f, 1), Tooltip("마지막으로 관측한 시설의 표시 밀도입니다. 현재 보이는 시설에는 적용되지 않습니다.")]
        private float rememberedBuildingOpacity = .65f;
        private readonly Plane[] rememberedFrustum = new Plane[6];
        private BuildingMemory buildingMemory;
        private MaterialPropertyBlock rememberedFacility, rememberedSilhouette;

        internal void SetBuildingMemory(BuildingMemory memory) => buildingMemory = memory;

        internal bool TryGetRememberedBuilding(int id, out RememberedBuilding record)
        {
            record = default;
            return buildingMemory != null && !buildingMemory.IsVisible(id) && buildingMemory.TryGet(id, out record);
        }

        internal bool TryHoverRememberedBuilding(Ray ray, out RememberedBuilding hovered)
        {
            hovered = default;
            if (buildingMemory == null || ray.direction.y >= 0 ||
                !new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float ground)) return false;
            var direction = new Vector2(ray.direction.x, ray.direction.z);
            float horizontalSquared = direction.sqrMagnitude;
            float nearest = float.PositiveInfinity;
            foreach (RememberedBuilding record in buildingMemory.Records)
            {
                BuildingView building = record.Building;
                if (!buildingMemory.IsVisible(building.Id) &&
                    TryPickBuilding(building, ray, ground, direction, horizontalSquared, hovered.Building.Kind, ref nearest))
                    hovered = record;
            }
            return hovered.Building.Id != 0;
        }

        private void DrawRememberedBuildings()
        {
            if (buildingMemory == null || buildingMemory.Records.Count == 0) return;
            GeometryUtility.CalculateFrustumPlanes(targetCamera, rememberedFrustum);
            if (rememberedFacility == null)
            {
                rememberedFacility = new MaterialPropertyBlock();
                rememberedFacility.SetFloat("_EmissionStrength", 0);
                rememberedFacility.SetFloat("_ReflectionStrength", 0);
                rememberedSilhouette = Spectral(rememberedBuildingOpacity);
                rememberedSilhouette.SetFloat("_EmissionStrength", 0);
                rememberedSilhouette.SetFloat("_ReflectionStrength", 0);
                rememberedSilhouette.SetFloat("_Flow", 0);
                rememberedSilhouette.SetFloat("_OwnershipTint", 1);
            }
            foreach (RememberedBuilding record in buildingMemory.Records)
            {
                BuildingView building = record.Building;
                if (buildingMemory.IsVisible(building.Id)) continue;
                Vector3 center = new Vector3(building.Position.X, .08f, building.Position.Y);
                float height = VisualBuildingHeight(building) + building.Radius;
                var bounds = new Bounds(center + Vector3.up * (height * .5f),
                    new Vector3(building.Radius * 2, height, building.Radius * 2));
                if (!GeometryUtility.TestPlanesAABB(rememberedFrustum, bounds)) continue;
                Color tint = Color.Lerp(new Color(.52f, .57f, .64f), record.Color, .32f);
                float opacity = Mathf.Clamp01(rememberedBuildingOpacity);
                if (building.GhostEndTick > record.LastSeenTick) opacity = Mathf.Min(opacity, ghostOpacity);
                else if (building.IsUnderConstruction) opacity = Mathf.Min(opacity, unfinishedOpacity);
                rememberedFacility.SetColor(TeamColor, tint);
                rememberedFacility.SetFloat(Opacity, opacity);
                rememberedSilhouette.SetColor(BaseColor, tint);
                rememberedSilhouette.SetColor(SecondColor, tint);
                rememberedSilhouette.SetFloat(Opacity, opacity);

                bool dome = building.Kind == BuildingKind.Dome;
                if (dome || !DrawFacility(building.Kind, center, building.Radius, rememberedFacility, false))
                    DrawOne(dome ? domeMesh : crystal, profiles[lightIndex].Body,
                        BuildingModel(building.Kind, center, building.Radius), rememberedSilhouette);
                if (IsTower(building.Kind) && !building.IsUnderConstruction && ValidElement(building.TowerElement))
                {
                    var profile = profiles[building.TowerElement];
                    DrawOne(profile.Mesh, profile.Body, TowerElementPose(building, center, 0, out _), rememberedSilhouette);
                }
                rememberedSilhouette.SetFloat(Opacity, Mathf.Clamp01(rememberedBuildingOpacity));
                DrawOne(ring, profiles[lightIndex].Body, Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * building.Radius),
                    rememberedSilhouette);
            }
        }
    }
}
