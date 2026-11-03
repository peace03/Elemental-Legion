using UnityEngine;

namespace ElementalLegion.Client.FogOfWar
{
    public enum FogQuality { Low, Normal, High }

    /// <summary>표현 전용 설정이다. 시야 반경·맵 크기는 서버 데이터에서만 받는다.</summary>
    [CreateAssetMenu(menuName = "Elemental Legion/Space Fog Settings")]
    public sealed class FogSettings : ScriptableObject
    {
        [SerializeField, Tooltip("낮음: 반해상도 16, 기본: 반해상도 32, 높음: 전체 해상도 64회 적분입니다.")] private FogQuality quality = FogQuality.Normal;
        [SerializeField, Min(1), Tooltip("월드 바닥 위 연무 높이(m)입니다.")] private float height = 24;
        [SerializeField, Range(0, 1), Tooltip("탐색된 지역의 소광 밀도입니다.")] private float density = 0.18f;
        [SerializeField, Range(0, 1), Tooltip("현재 시야의 상대 밀도입니다.")] private float visibleDensity = 0.12f;
        [SerializeField, Range(0, 1), Tooltip("탐색된 지형의 밝기입니다.")] private float exploredBrightness = 0.22f;
        [SerializeField, Range(0.1f, 8), Tooltip("상층으로 갈수록 밀도가 감소하는 비율입니다.")] private float heightFalloff = 2;
        [SerializeField, Min(0.001f), Tooltip("3D 노이즈의 공간 주파수입니다.")] private float noiseScale = 0.035f;
        [SerializeField, Tooltip("초당 노이즈 이동 거리(m)입니다.")] private Vector3 wind = new Vector3(0.7f, 0.05f, 0.4f);
        [SerializeField, ColorUsage(false, true), Tooltip("연무의 청록 산란 색입니다.")] private Color cyan = new Color(0.1f, 0.55f, 0.65f);
        [SerializeField, ColorUsage(false, true), Tooltip("연무의 보라 산란 색입니다.")] private Color violet = new Color(0.35f, 0.12f, 0.55f);
        [SerializeField, Range(0, 4), Tooltip("주변광 산란 세기입니다.")] private float ambient = 0.6f;
        [SerializeField, Range(0, 4), Tooltip("단일 방향광 산란 세기입니다.")] private float sunlight = 0.8f;
        [SerializeField, Range(-0.8f, 0.8f), Tooltip("방향광 산란의 전방 편향입니다.")] private float anisotropy = 0.25f;
        [SerializeField, Range(0, 1), Tooltip("탐색 영역 내부 현재 시야 경계의 보간 비율입니다.")] private float boundaryBlend = 1;
        [SerializeField, Min(0.01f), Tooltip("깊이 차이에 따른 업샘플 가중치 감쇠입니다.")] private float depthSharpness = 1;
        public FogQuality Quality => quality;
        public int Samples => quality == FogQuality.Low ? 16 : quality == FogQuality.High ? 64 : 32;
        public int Downsample => quality == FogQuality.High ? 1 : 2;
        public float Height => height;

        public void Apply(Material material, FogQuality? overrideQuality = null)
        {
            material.SetVector("_FogDensity", new Vector4(Mathf.Max(0, density), Mathf.Clamp01(visibleDensity), Mathf.Clamp01(exploredBrightness), Mathf.Max(0.1f, heightFalloff)));
            FogQuality selected = overrideQuality ?? quality;
            int samples = selected == FogQuality.Low ? 16 : selected == FogQuality.High ? 64 : 32;
            material.SetVector("_FogShape", new Vector4(Mathf.Max(1, height), Mathf.Max(0.001f, noiseScale), samples, Mathf.Clamp01(boundaryBlend)));
            material.SetVector("_FogWind", wind);
            material.SetColor("_FogCyan", cyan.linear); material.SetColor("_FogViolet", violet.linear);
            material.SetVector("_FogLighting", new Vector4(ambient, sunlight, anisotropy, Mathf.Max(0.01f, depthSharpness)));
        }
    }
}
