using System;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using UnityEngine;

namespace ElementalLegion.Client.FogOfWar
{
    /// <summary>한 카메라의 서버 시야 텍스처·머티리얼을 소유한다. 비활성화 시 모두 해제한다.</summary>
    [DisallowMultipleComponent]
    public sealed class FogPresentation : MonoBehaviour
    {
        [SerializeField, Tooltip("연무를 그릴 Base 카메라입니다.")] private UnityEngine.Camera targetCamera;
        [SerializeField, Tooltip("활용하는 Renderer Data의 연무 Feature입니다.")] private FogOfWarRendererFeature[] features;
        [SerializeField, Tooltip("표현 품질과 밀도 설정입니다.")] private FogSettings settings;
        [SerializeField, Tooltip("전용 볼륨 셰이더입니다.")] private Shader shader;
        [SerializeField, Tooltip("반복 가능한 절차적 3D 노이즈입니다.")] private Texture3D noise;
        [SerializeField, Tooltip("연무에 산란을 제공할 단일 방향광입니다. 비어 있으면 씬의 Sun을 사용합니다.")] private Light directionalLight;
        private Texture2D mask;
        private Material material;
        private byte[] pixels;
        private int width;
        private float mapSize;
        private GameRules configuredRules;
        private FogStateReplica latestState;
        public Texture2D Mask => mask;
        public Material CameraMaterial => material;
        public FogSettings Settings => settings;
        public FogQuality? QualityOverride { get; set; }
        public bool Decorative { get; set; }
        public int Downsample => QualityOverride.HasValue
            ? (QualityOverride.Value == FogQuality.High ? 1 : 2)
            : settings.Downsample;
        private MaterialPropertyBlock rayProperties, compositeProperties;
        internal MaterialPropertyBlock RayProperties => rayProperties ?? (rayProperties = new MaterialPropertyBlock());
        internal MaterialPropertyBlock CompositeProperties => compositeProperties ?? (compositeProperties = new MaterialPropertyBlock());

        private void OnEnable()
        { if (mask == null && configuredRules != null) Initialize(configuredRules); else Register(); }

        public void Initialize(GameRules rules)
        {
            if (targetCamera == null || settings == null || shader == null || noise == null || features == null || features.Length == 0)
                throw new InvalidOperationException("FogPresentation 필수 참조가 없습니다.");
            configuredRules = rules; FogStateReplica previous = latestState;
            Release();
            width = Mathf.CeilToInt(rules.MapSize / rules.CellSize); mapSize = rules.MapSize;
            pixels = new byte[width * width * 2];
            mask = new Texture2D(width, width, TextureFormat.RG16, false, true)
            { name = "Camera Vision " + targetCamera.GetEntityId(), wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
            material = new Material(shader) { name = "Camera Space Fog " + targetCamera.GetEntityId(), hideFlags = HideFlags.DontSave };
            material.SetTexture("_VisionMask", mask); material.SetTexture("_VolumeNoise", noise);
            material.SetFloat("_DecorativeFog", Decorative ? 1 : 0);
            material.SetVector("_FogMap", new Vector4(mapSize, width, rules.CellSize, 0));
            ResetVision(); if (previous != null && previous.Ready) Apply(previous); Register();
        }

        public void Apply(FogStateReplica state)
        {
            latestState = state;
            if (mask == null || state.CellCount != width * width) return;
            for (int cell = 0; cell < state.CellCount; cell++)
            {
                int bit = 1 << (cell & 7);
                pixels[cell * 2] = (byte)((state.Current[cell >> 3] & bit) != 0 ? 255 : 0);
                pixels[cell * 2 + 1] = (byte)((state.Explored[cell >> 3] & bit) != 0 ? 255 : 0);
            }
            Upload();
        }

        public void ResetVision()
        { latestState = null; if (pixels == null) return; Array.Clear(pixels, 0, pixels.Length); Upload(); }

        internal bool Prepare()
        {
            if (!isActiveAndEnabled || material == null || mask == null) return false;
            settings.Apply(material, QualityOverride);
            Light sun = directionalLight != null ? directionalLight : RenderSettings.sun;
            Vector3 direction = sun != null && sun.enabled ? -sun.transform.forward : Vector3.up;
            Color color = sun != null && sun.enabled ? sun.color.linear * sun.intensity : Color.black;
            material.SetVector("_FogSunDirection", direction); material.SetColor("_FogSunColor", color);
            material.SetColor("_FogAmbientColor", RenderSettings.ambientLight.linear);
            return true;
        }

        private void Upload() { mask.LoadRawTextureData(pixels); mask.Apply(false, false); }

        private void Register()
        {
            if (!isActiveAndEnabled || features == null || targetCamera == null) return;
            foreach (FogOfWarRendererFeature feature in features) if (feature != null) feature.Register(targetCamera, this);
        }

        private void OnDisable() { ReleaseResources(); }

        public void ReleaseResources()
        {
            if (features != null && targetCamera != null)
                foreach (FogOfWarRendererFeature feature in features) if (feature != null) feature.Unregister(targetCamera, this);
            Release();
        }

        private void Release()
        {
            if (material != null) DestroyResource(material);
            if (mask != null) DestroyResource(mask);
            material = null; mask = null; pixels = null;
            rayProperties?.Clear(); compositeProperties?.Clear();
        }

        private static void DestroyResource(UnityEngine.Object resource)
        { if (Application.isPlaying) Destroy(resource); else DestroyImmediate(resource); }
    }
}
