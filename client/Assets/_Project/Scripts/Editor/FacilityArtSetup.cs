using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    /// <summary>제공 모델 사본의 원래 표면을 연결하고 실제 메시의 지면·반경을 기록한다.</summary>
    public static class FacilityArtSetup
    {
        private const string Folder = "Assets/_Project/Art/Facilities";
        private const string Reflection = "Assets/_Project/Art/Surfaces/studio_small_08/studio_small_08_1k.hdr";

        [MenuItem("Elemental Legion/Configure Facility Models")]
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play 중에는 시설 자산을 구성하지 않습니다.");
            Shader shader = Shader.Find("ElementalLegion/Facility");
            if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("시설 셰이더 컴파일을 확인하세요.");
            JObject manifest = JObject.Parse(File.ReadAllText(Folder + "/manifest.json"));
            var models = new List<FacilityModelCatalog.Model>(5);
            foreach (JObject source in manifest["models"])
            {
                string folder = (string)source["folder"];
                string fbx = (string)source["fbx"];
                BuildingKind kind = Kind(folder);
                var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
                foreach (JObject entry in source["materials"])
                {
                    string name = (string)entry["materialName"];
                    JObject maps = (JObject)entry["textures"];
                    foreach (JProperty map in maps.Properties())
                        if (map.Value.Type == JTokenType.String) PrepareTexture((string)map.Value, map.Name);
                    string materialPath = Folder + "/" + folder + "/" + name + ".mat";
                    Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if (material == null) { material = new Material(shader) { name = name }; AssetDatabase.CreateAsset(material, materialPath); }
                    material.shader = shader;
                    SetMap(material, "_BaseMap", maps, maps["TeamBaseColor"].Type == JTokenType.String ? "TeamBaseColor" : "BaseColor");
                    SetMap(material, "_NormalMap", maps, "Normal");
                    SetMap(material, "_RoughnessMap", maps, "Roughness");
                    SetMap(material, "_MetallicMap", maps, "Metallic");
                    SetMap(material, "_TeamMask", maps, "TeamMask");
                    material.SetFloat("_HasRoughnessMap", maps["Roughness"].Type == JTokenType.String ? 1 : 0);
                    material.SetFloat("_HasMetallicMap", maps["Metallic"].Type == JTokenType.String ? 1 : 0);
                    material.SetFloat("_HasTeamMask", maps["TeamMask"].Type == JTokenType.String ? 1 : 0);
                    material.SetFloat("_NormalStrength", .65f);
                    material.SetFloat("_Roughness", .5f);
                    material.SetFloat("_AmbientFill", .18f);
                    material.SetFloat("_Opacity", 1);
                    var reflection = AssetDatabase.LoadAssetAtPath<Cubemap>(Reflection);
                    material.SetTexture("_ReflectionCube", reflection);
                    material.SetFloat("_ReflectionMipCount", reflection != null ? reflection.mipmapCount - 1 : 0);
                    material.SetFloat("_ReflectionStrength", reflection != null ? .35f : 0);
                    material.enableInstancing = true;
                    EditorUtility.SetDirty(material);
                    materials.Add(name, material);
                }

                var importer = AssetImporter.GetAtPath(fbx) as ModelImporter;
                if (importer == null) throw new InvalidOperationException("시설 FBX를 찾지 못했습니다: " + fbx);
                importer.importAnimation = false;
                importer.importCameras = false;
                importer.importLights = false;
                importer.isReadable = true;
                importer.bakeAxisConversion = true;
                importer.importNormals = ModelImporterNormals.Import;
                importer.importTangents = ModelImporterTangents.CalculateMikk;
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                foreach (var material in materials)
                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), material.Key), material.Value);
                importer.SaveAndReimport();
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
                MeshFilter[] filters = model.GetComponentsInChildren<MeshFilter>(true);
                if (filters.Length == 0) throw new InvalidOperationException("시설 모델에 메시가 없습니다: " + fbx);
                var points = new List<Vector3>();
                var parts = new List<FacilityModelCatalog.Part>();
                foreach (MeshFilter filter in filters)
                {
                    Mesh mesh = filter.sharedMesh;
                    Matrix4x4 local = filter.transform.localToWorldMatrix;
                    foreach (Vector3 vertex in mesh.vertices) points.Add(local.MultiplyPoint3x4(vertex));
                    JObject meshSource = source["meshes"].OfType<JObject>().FirstOrDefault(entry =>
                        (string)entry["name"] == filter.name || (string)entry["name"] == mesh.name);
                    // Unity는 단일 메시 FBX의 루트를 파일 이름으로 바꿀 수 있다.
                    if (meshSource == null && source["meshes"].Count() == 1) meshSource = (JObject)source["meshes"][0];
                    if (meshSource == null) throw new InvalidOperationException("원본 부품을 대조하지 못했습니다: " + filter.name);
                    for (int index = 0; index < mesh.subMeshCount; index++)
                    {
                        JObject slot = meshSource["slots"].OfType<JObject>().FirstOrDefault(entry => (int)entry["index"] == index);
                        if (slot == null || !materials.TryGetValue((string)slot["materialName"], out Material material))
                            throw new InvalidOperationException("시설 부품의 재질이 누락됐습니다: " + filter.name);
                        var motion = kind == BuildingKind.EnergyForge ? IgnitionPartMotion(mesh.name) : FacilityModelCatalog.PartMotion.None;
                        parts.Add(new FacilityModelCatalog.Part { Mesh = mesh, Material = material, LocalMatrix = local, Submesh = index, Motion = motion });
                    }
                }
                Bounds bounds = new Bounds(points[0], Vector3.zero);
                foreach (Vector3 point in points) bounds.Encapsulate(point);
                Vector3 origin = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                float radius = 0;
                foreach (Vector3 point in points) radius = Mathf.Max(radius, new Vector2(point.x - origin.x, point.z - origin.z).magnitude);
                if (radius < .01f || bounds.size.y < .01f) throw new InvalidOperationException("시설 크기를 확인할 수 없습니다: " + fbx);
                for (int index = 0; index < parts.Count; index++)
                {
                    var part = parts[index];
                    part.LocalMatrix = Matrix4x4.Translate(-origin) * part.LocalMatrix;
                    parts[index] = part;
                }
                if (kind == BuildingKind.EnergyForge && parts.Select(part => part.Motion)
                    .Where(motion => motion != FacilityModelCatalog.PartMotion.None).Distinct().Count() != 4)
                    throw new InvalidOperationException("점화로의 중앙 수정과 독립 금속 링 세 개를 확인하세요.");
                models.Add(new FacilityModelCatalog.Model { Kind = kind, HorizontalRadius = radius, Height = bounds.size.y, Parts = parts.ToArray() });
            }
            if (models.Count != 5) throw new InvalidOperationException("시설 모델은 5종이어야 합니다.");
            var catalog = AssetDatabase.LoadAssetAtPath<FacilityModelCatalog>(FacilityModelCatalog.AssetPath);
            if (catalog == null) { catalog = ScriptableObject.CreateInstance<FacilityModelCatalog>(); AssetDatabase.CreateAsset(catalog, FacilityModelCatalog.AssetPath); }
            catalog.SetImportedModels(models.ToArray());
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
        }

        private static FacilityModelCatalog.PartMotion IgnitionPartMotion(string meshName) => meshName switch
        {
            "Ignition_Hearth_Crystal_Low" => FacilityModelCatalog.PartMotion.IgnitionCrystal,
            "Ignition_Hearth_Ring_Low" => FacilityModelCatalog.PartMotion.IgnitionRingLower,
            "Ignition_Hearth_Ring_Low.001" => FacilityModelCatalog.PartMotion.IgnitionRingMiddle,
            "Ignition_Hearth_Ring_Low.002" => FacilityModelCatalog.PartMotion.IgnitionRingUpper,
            _ => FacilityModelCatalog.PartMotion.None
        };

        private static BuildingKind Kind(string folder) => folder switch
        {
            "Primitive" => BuildingKind.Base,
            "Cohesion" => BuildingKind.NatureForge,
            "Ignition" => BuildingKind.EnergyForge,
            "Collecting" => BuildingKind.Collector,
            "Guarding" => BuildingKind.NatureTower,
            _ => throw new InvalidOperationException("알 수 없는 시설 폴더: " + folder)
        };

        private static void SetMap(Material material, string property, JObject maps, string name)
        {
            string path = (string)maps[name];
            Texture texture = path == null ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (path != null && texture == null) throw new FileNotFoundException("시설 텍스처를 찾지 못했습니다.", path);
            material.SetTexture(property, texture);
        }

        private static void PrepareTexture(string path, string role)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new FileNotFoundException("시설 이미지가 없습니다.", path);
            bool normal = role == "Normal";
            bool color = role == "BaseColor" || role == "TeamBaseColor";
            TextureImporterType type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (importer.textureType == type && importer.sRGBTexture == color && importer.maxTextureSize == 2048 && importer.mipmapEnabled) return;
            importer.textureType = type;
            importer.sRGBTexture = color;
            importer.maxTextureSize = 2048;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
        }
    }
}
