using System;
using ElementalLegion.Client.Legion;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ElementalLegion.Client.Editor
{
    /// <summary>기존 클라이언트 프리팹에 제공 시설과 암석 전장을 연결한다.</summary>
    public static class IngameVisualArtSetup
    {
        [MenuItem("Elemental Legion/Apply Ingame Visual Target")]
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play 중에는 인게임 자산을 구성하지 않습니다.");
            ElementVisualArtSetup.Configure();
            FacilityArtSetup.Configure();
            BattlefieldArtSetup.Configure();
            ConfigurePrefab();
        }

        public static void ConfigurePrefab()
        {
            ElementStatusArtSetup.Configure();
            var catalog = AssetDatabase.LoadAssetAtPath<FacilityModelCatalog>(FacilityModelCatalog.AssetPath);
            var ground = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Materials/BattlefieldGround.mat");
            var rock = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Materials/BattlefieldRock.mat");
            if (catalog == null || catalog.Count != 5 || ground == null || rock == null)
                throw new InvalidOperationException("시설과 전장 자산을 먼저 구성하세요.");
            const string shadowPath = "Assets/_Project/Materials/UnitGroundShadow.mat";
            Material shadow = AssetDatabase.LoadAssetAtPath<Material>(shadowPath);
            if (shadow == null)
            {
                var shader = Shader.Find("ElementalLegion/UnitGroundShadow");
                if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("접지 그림자 셰이더를 확인하세요.");
                shadow = new Material(shader) { name = "UnitGroundShadow", enableInstancing = true };
                AssetDatabase.CreateAsset(shadow, shadowPath);
            }
            GameObject root = PrefabUtility.LoadPrefabContents(ClientSceneBuilder.PrefabPath);
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                var battlefield = root.GetComponent<BattlefieldPresentation>();
                if (battlefield == null) battlefield = root.AddComponent<BattlefieldPresentation>();
                Set(visuals, "facilityModels", catalog);
                Set(visuals, "groundShadow", shadow);
                Set(visuals, "statusShell", AssetDatabase.LoadAssetAtPath<Material>(ElementStatusArtSetup.MaterialPath));
                Set(battlefield, "groundMaterial", ground);
                Set(battlefield, "rockMaterial", rock);
                Set(root.GetComponent<LegionPresentation>(), "battlefield", battlefield);
                // 기존 연무 보조광과 전장 주광이 겹쳐 재질 대비가 낮아지지 않게 한다.
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                    if (light.name == "SpaceSun") light.intensity = .15f;
                PrefabUtility.SaveAsPrefabAsset(root, ClientSceneBuilder.PrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            foreach (string path in new[] { "Assets/_Project/Settings/Rendering/PC_RPAsset.asset", "Assets/_Project/Settings/Rendering/Mobile_RPAsset.asset" })
            {
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (pipeline == null) continue;
                pipeline.shadowDistance = Mathf.Max(80, pipeline.shadowDistance);
                EditorUtility.SetDirty(pipeline);
            }
            AssetDatabase.SaveAssets();
        }

        private static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
