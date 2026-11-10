using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    // 현재 표시하는 원소 중 일부만 공유 점광원으로 바닥과 시설을 비춘다.
    public sealed partial class ElementalVisuals
    {
        [SerializeField, Range(0, 4), Tooltip("바닥과 시설을 비추는 원소 점광원 개수의 상한입니다. 초기화할 때 적용합니다.")]
        private int maxLocalLights = 4;
        [SerializeField, Min(0), Tooltip("원소 점광원의 밝기입니다. 게임 판정에는 영향을 주지 않습니다.")]
        private float localLightIntensity = 1.2f;
        [SerializeField, Min(.1f), Tooltip("원소 점광원이 바닥과 주변 시설을 비추는 거리(m)입니다.")]
        private float localLightRange = 3.5f;
        private readonly Light[] localLights = new Light[4];
        private readonly int[] localLightSourceIds = new int[4];
        private readonly int[] localLightCandidates = new int[4];
        private readonly float[] localLightScores = new float[4];
        private Color[] localLightColors;
        private float[] localLightStrengths;

        private void InitializeLocalLights()
        {
            maxLocalLights = Mathf.Clamp(maxLocalLights, 0, localLights.Length);
            localLightColors = new Color[profiles.Length];
            localLightStrengths = new float[profiles.Length];
            for (int element = 0; element < profiles.Length; element++)
            {
                var profile = profiles[element];
                string id = profile.ElementId;
                if (id != "fire" && id != "lightning" && id != "light" && id != "decay") continue;
                var material = profile.UnitBody;
                Color color = material != null && material.HasProperty(BaseColor) ? material.GetColor(BaseColor) : profile.Tint;
                localLightColors[element] = color;
                localLightStrengths[element] = Mathf.Max(0, color.maxColorComponent);
            }
            ClearLocalLights();
            for (int slot = 0; slot < localLights.Length; slot++)
            {
                var light = localLights[slot];
                if (light != null && (slot >= maxLocalLights || light.transform.parent != poolRoot.transform))
                {
                    if (Application.isPlaying) Destroy(light.gameObject); else DestroyImmediate(light.gameObject);
                    light = localLights[slot] = null;
                }
                if (slot >= maxLocalLights || light != null) continue;
                var go = new GameObject("Element Local Light " + slot);
                go.transform.SetParent(poolRoot.transform, false);
                light = localLights[slot] = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.shadows = LightShadows.None;
                light.enabled = false;
            }
        }

        private void UpdateLocalLights(int displayCount)
        {
            if (localLightStrengths == null || targetCamera == null) { ClearLocalLights(); return; }
            Vector3 center = targetCamera.transform.position;
            var ray = targetCamera.ViewportPointToRay(new Vector3(.5f, .5f));
            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance)) center = ray.GetPoint(distance);
            for (int slot = 0; slot < localLights.Length; slot++) { localLightScores[slot] = 0; localLightCandidates[slot] = -1; }
            int limit = Mathf.Clamp(maxLocalLights, 0, localLights.Length);
            for (int index = 0; index < displayCount && limit > 0; index++)
            {
                ref DisplayUnit item = ref displayUnits[index];
                int element = item.Unit.Element;
                if (item.Unit.Ghost || (uint)element >= localLightStrengths.Length || localLightStrengths[element] <= 0) continue;
                float dx = item.Position.X - center.x, dz = item.Position.Y - center.z;
                float score = localLightStrengths[element] / (1 + dx * dx + dz * dz);
                int weakest = 0;
                for (int slot = 1; slot < limit; slot++) if (localLightScores[slot] < localLightScores[weakest]) weakest = slot;
                if (score <= localLightScores[weakest]) continue;
                localLightScores[weakest] = score;
                localLightCandidates[weakest] = index;
            }
            for (int slot = 0; slot < localLights.Length; slot++)
            {
                var light = localLights[slot];
                int candidate = localLightCandidates[slot];
                localLightSourceIds[slot] = 0;
                if (light == null) continue;
                if (candidate < 0 || localLightIntensity <= 0) { light.enabled = false; continue; }
                ref DisplayUnit item = ref displayUnits[candidate];
                localLightSourceIds[slot] = item.Unit.Id;
                light.transform.position = new Vector3(item.Position.X, bodyHeight, item.Position.Y);
                light.color = localLightColors[item.Unit.Element];
                light.intensity = Mathf.Max(0, localLightIntensity);
                light.range = Mathf.Max(.1f, localLightRange);
                light.enabled = true;
            }
        }

        private void ClearLocalLights()
        {
            for (int slot = 0; slot < localLights.Length; slot++)
            {
                if (localLights[slot] != null) localLights[slot].enabled = false;
                localLightSourceIds[slot] = 0;
            }
        }

        private void ForgetLocalLight(ObservedEntityId entity)
        {
            if (entity.Kind != ObservedEntityKind.Unit) return;
            for (int slot = 0; slot < localLights.Length; slot++)
                if (localLightSourceIds[slot] == entity.Id)
                {
                    if (localLights[slot] != null) localLights[slot].enabled = false;
                    localLightSourceIds[slot] = 0;
                }
        }
    }
}
