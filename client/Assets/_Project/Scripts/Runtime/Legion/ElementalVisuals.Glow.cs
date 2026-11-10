using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ElementalLegion.Client.Legion
{
    public sealed partial class ElementalVisuals
    {
        [Header("원소 발광")]
        [SerializeField, Range(0, 2), Tooltip("밝은 원소 주변의 빛 번짐 강도입니다. 0이면 추가 후처리를 끕니다.")] private float glowIntensity = .55f;
        [SerializeField, Min(0), Tooltip("빛 번짐을 시작하는 HDR 밝기입니다.")] private float glowThreshold = .85f;
        [SerializeField, Range(0, 1), Tooltip("빛이 주변으로 퍼지는 정도입니다.")] private float glowScatter = .55f;
        [SerializeField, Range(-1, 1), Tooltip("전장 화면의 노출 보정입니다. UI에는 적용하지 않습니다.")] private float exposure = .05f;
        [SerializeField, Range(0, 25), Tooltip("밝은 원소와 어두운 암석을 구분할 화면 대비입니다.")] private float contrast = 10;
        [SerializeField, Range(-20, 10), Tooltip("원소와 금속의 채도를 조절합니다.")] private float saturation = -3;
        private Volume glowVolume;
        private VolumeProfile glowProfile;
        private UniversalAdditionalCameraData glowCamera;
        private bool originalPostProcessing;
        private bool addedCameraData;
        private LayerMask originalVolumeMask;

        private void InitializeGlow()
        {
            if (glowIntensity <= 0) return;
            glowCamera = targetCamera.GetComponent<UniversalAdditionalCameraData>();
            addedCameraData = glowCamera == null;
            if (addedCameraData) glowCamera = targetCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            originalPostProcessing = glowCamera.renderPostProcessing;
            originalVolumeMask = glowCamera.volumeLayerMask;
            glowCamera.renderPostProcessing = true;
            glowCamera.volumeLayerMask = originalVolumeMask.value | (1 << gameObject.layer);
            glowProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            glowProfile.name = "Element glow runtime";
            glowProfile.hideFlags = HideFlags.HideAndDontSave;
            Bloom bloom = glowProfile.Add<Bloom>(true);
            bloom.intensity.value = Mathf.Clamp(glowIntensity, 0, 2);
            bloom.threshold.value = Mathf.Max(0, glowThreshold);
            bloom.scatter.value = Mathf.Clamp01(glowScatter);
            bloom.highQualityFiltering.value = false;
            bloom.maxIterations.value = 4;
            bloom.clamp.value = 8;
            var tonemapping = glowProfile.Add<Tonemapping>(true);
            tonemapping.mode.value = TonemappingMode.ACES;
            var grading = glowProfile.Add<ColorAdjustments>(true);
            grading.postExposure.value = exposure;
            grading.contrast.value = contrast;
            grading.saturation.value = saturation;
            poolRoot.layer = gameObject.layer;
            glowVolume = poolRoot.AddComponent<Volume>();
            glowVolume.isGlobal = true;
            glowVolume.priority = 10;
            glowVolume.sharedProfile = glowProfile;
        }

        private void ReleaseGlow()
        {
            if (glowCamera != null)
            {
                glowCamera.renderPostProcessing = originalPostProcessing;
                glowCamera.volumeLayerMask = originalVolumeMask;
                if (addedCameraData) ReleaseGlowObject(glowCamera);
            }
            if (glowVolume != null) { glowVolume.enabled = false; glowVolume.sharedProfile = null; }
            if (glowProfile != null)
            {
                foreach (VolumeComponent component in glowProfile.components) ReleaseGlowObject(component);
                ReleaseGlowObject(glowProfile);
            }
            glowProfile = null;
            glowCamera = null;
            glowVolume = null;
            addedCameraData = false;
        }

        private static void ReleaseGlowObject(Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
