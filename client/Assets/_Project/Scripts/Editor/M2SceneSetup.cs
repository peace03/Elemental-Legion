using System;
using System.IO;
using System.Linq;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.FogOfWar;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ElementalLegion.Client.Editor
{
    /// <summary>승인된 M2 자산과 기존 프리팹 참조만 연결한다. 기존 Renderer Feature는 보존한다.</summary>
    public static class M2SceneSetup
    {
        public const string SettingsPath = "Assets/_Project/Settings/Rendering/SpaceFog.asset";
        public const string NoisePath = "Assets/_Project/Settings/Rendering/SpaceFogNoise.asset";

        [MenuItem("Elemental Legion/Configure M2 Fog")]
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play 중에는 자산을 구성하지 않습니다.");
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Shaders/FogOfWar.shader");
            if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("연무 셰이더를 먼저 컴파일해야 합니다.");
            var settings = AssetDatabase.LoadAssetAtPath<FogSettings>(SettingsPath);
            if (settings == null) { settings = ScriptableObject.CreateInstance<FogSettings>(); AssetDatabase.CreateAsset(settings, SettingsPath); }
            var noise = AssetDatabase.LoadAssetAtPath<Texture3D>(NoisePath);
            if (noise == null) { noise = CreateNoise(); AssetDatabase.CreateAsset(noise, NoisePath); }
            string folder = "Assets/_Project/Settings/Rendering/";
            var features = new[] { Feature(folder + "PC_Renderer.asset"), Feature(folder + "Mobile_Renderer.asset") };
            GameObject root = PrefabUtility.LoadPrefabContents(ClientSceneBuilder.PrefabPath);
            try
            {
                var camera = root.GetComponentInChildren<UnityEngine.Camera>(true);
                var fog = camera.GetComponent<FogPresentation>();
                if (fog == null) fog = camera.gameObject.AddComponent<FogPresentation>();
                var sun = root.GetComponentsInChildren<Light>(true).FirstOrDefault(light => light.type == LightType.Directional);
                if (sun == null)
                {
                    var go = new GameObject("SpaceSun"); go.transform.SetParent(root.transform, false);
                    go.transform.rotation = Quaternion.Euler(45, -35, 0); sun = go.AddComponent<Light>(); sun.type = LightType.Directional;
                    sun.color = new Color(0.65f, 0.85f, 1); sun.intensity = 1; sun.shadows = LightShadows.None;
                }
                Set(fog, "targetCamera", camera); Set(fog, "settings", settings); Set(fog, "shader", shader); Set(fog, "noise", noise); Set(fog, "directionalLight", sun);
                var serialized = new SerializedObject(fog); SerializedProperty array = serialized.FindProperty("features"); array.arraySize = features.Length;
                for (int index = 0; index < features.Length; index++) array.GetArrayElementAtIndex(index).objectReferenceValue = features[index];
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Set(root.GetComponent<ClientBootstrap>(), "fog", fog);
                PrefabUtility.SaveAsPrefabAsset(root, ClientSceneBuilder.PrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
        }

        private static FogOfWarRendererFeature Feature(string path)
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
            if (data == null) throw new FileNotFoundException(path);
            var feature = data.rendererFeatures.OfType<FogOfWarRendererFeature>().SingleOrDefault();
            if (feature != null) return feature;
            feature = ScriptableObject.CreateInstance<FogOfWarRendererFeature>(); feature.name = "Space Fog of War"; feature.hideFlags = HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(feature, data); data.rendererFeatures.Add(feature);
            var serialized = new SerializedObject(data); var map = serialized.FindProperty("m_RendererFeatureMap"); map.arraySize = data.rendererFeatures.Count;
            for (int index = 0; index < map.arraySize; index++)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data.rendererFeatures[index], out string _, out long id);
                map.GetArrayElementAtIndex(index).longValue = id;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo(); data.SetDirty(); EditorUtility.SetDirty(data);
            return feature;
        }

        private static Texture3D CreateNoise()
        {
            const int size = 64; var bytes = new byte[size * size * size];
            for (int z = 0; z < size; z++) for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                Vector3 uv = new Vector3(x, y, z) / size;
                float value = ValueNoise(uv, 8) * 0.72f + ValueNoise(uv, 16) * 0.2f + ValueNoise(uv, 32) * 0.08f;
                bytes[(z * size + y) * size + x] = (byte)Mathf.RoundToInt(Mathf.Pow(value, 1.3f) * 255);
            }
            var texture = new Texture3D(size, size, size, TextureFormat.R8, false)
            { name = "Space Fog Periodic Noise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
            texture.SetPixelData(bytes, 0); texture.Apply(false, true); return texture;
        }

        private static float ValueNoise(Vector3 uv, int period)
        {
            Vector3 p = uv * period; int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
            Vector3 f = new Vector3(p.x - x, p.y - y, p.z - z); f = Vector3.Scale(Vector3.Scale(f, f), Vector3.one * 3 - 2 * f);
            float a = Mathf.Lerp(Hash(x, y, z, period), Hash(x + 1, y, z, period), f.x);
            float b = Mathf.Lerp(Hash(x, y + 1, z, period), Hash(x + 1, y + 1, z, period), f.x);
            float c = Mathf.Lerp(Hash(x, y, z + 1, period), Hash(x + 1, y, z + 1, period), f.x);
            float d = Mathf.Lerp(Hash(x, y + 1, z + 1, period), Hash(x + 1, y + 1, z + 1, period), f.x);
            return Mathf.Lerp(Mathf.Lerp(a, b, f.y), Mathf.Lerp(c, d, f.y), f.z);
        }

        private static float Hash(int x, int y, int z, int period)
        {
            unchecked
            {
                uint value = (uint)((x % period) * 73856093 ^ (y % period) * 19349663 ^ (z % period) * 83492791 ^ 721);
                value = (value ^ (value >> 16)) * 0x7feb352dU; value = (value ^ (value >> 15)) * 0x846ca68bU;
                return (value ^ (value >> 16)) / (float)uint.MaxValue;
            }
        }

        private static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        { var serialized = new SerializedObject(target); serialized.FindProperty(field).objectReferenceValue = value; serialized.ApplyModifiedPropertiesWithoutUndo(); }
    }
}
