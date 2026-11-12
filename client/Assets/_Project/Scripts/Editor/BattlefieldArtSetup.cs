using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    /// <summary>기존 CC0 암석 자료를 연결한 전장 재질만 생성한다. 씬·프리팹·원본 표면은 변경하지 않는다.</summary>
    public static class BattlefieldArtSetup
    {
        public const string GroundMaterialPath = "Assets/_Project/Materials/BattlefieldGround.mat";
        public const string RockMaterialPath = "Assets/_Project/Materials/BattlefieldRock.mat";
        private const string SurfaceFolder = "Assets/_Project/Art/Surfaces/";

        /// <summary>명시적 메뉴 실행 시 두 재질의 기본값과 기존 표면 참조를 갱신한다.</summary>
        [MenuItem("Elemental Legion/Configure Battlefield Art")]
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Play 중에는 전장 재질을 구성하지 않습니다.");
            var shader = Shader.Find("ElementalLegion/Battlefield");
            if (shader == null || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("Battlefield 셰이더의 임포트와 컴파일을 먼저 확인하세요.");
            Texture2D albedo = Required<Texture2D>(SurfaceFolder + "rock_08/rock_08_diff_1k.jpg");
            Texture2D normal = Required<Texture2D>(SurfaceFolder + "rock_08/rock_08_nor_gl_1k.jpg");
            Texture2D roughness = Required<Texture2D>(SurfaceFolder + "rock_08/rock_08_rough_1k.jpg");
            Cubemap reflection = Required<Cubemap>(SurfaceFolder + "studio_small_08/studio_small_08_1k.hdr");
            Directory.CreateDirectory("Assets/_Project/Materials");
            AssetDatabase.Refresh();
            ConfigureMaterial(GroundMaterialPath, shader, albedo, normal, roughness, reflection, false);
            ConfigureMaterial(RockMaterialPath, shader, albedo, normal, roughness, reflection, true);
        }

        private static void ConfigureMaterial(string path, Shader shader, Texture albedo, Texture normal,
            Texture roughness, Cubemap reflection, bool rock)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = rock ? "BattlefieldRock" : "BattlefieldGround" };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetTexture("_BaseMap", albedo);
            material.SetTexture("_NormalMap", normal);
            material.SetTexture("_RoughnessMap", roughness);
            material.SetTexture("_ReflectionCube", reflection);
            material.SetColor("_BaseColor", rock ? new Color(.25f, .28f, .32f) : new Color(.27f, .30f, .34f));
            material.SetColor("_VeinColor", new Color(.12f, .15f, .17f));
            material.SetFloat("_TextureScale", rock ? .5f : .025f);
            material.SetFloat("_NormalStrength", rock ? 1.5f : 1.4f);
            material.SetFloat("_DrySmoothness", rock ? .23f : .3f);
            material.SetFloat("_WetSmoothness", .68f);
            material.SetFloat("_WetCoverage", rock ? .05f : .12f);
            material.SetFloat("_VeinStrength", rock ? .04f : .08f);
            material.SetFloat("_ReflectionStrength", rock ? .2f : .45f);
            material.SetFloat("_AmbientFill", .14f);
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
        }

        private static T Required<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException("전장에 재사용할 표면 자료가 없습니다: " + path);
            return asset;
        }
    }
}
