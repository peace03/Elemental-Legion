using System;
using System.Linq;
using ElementalLegion.Client.UI;
using UnityEditor;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    /// <summary>생성한 그림을 공용 UI 설정에 연결한다. 기존 씬은 열거나 저장하지 않는다.</summary>
    public static class UiArtSetup
    {
        private const string Root = "Assets/_Project/Art/UI/";
        public const string AssetPath = "Assets/_Project/Resources/UI/LegionUiArt.asset";
        private static readonly string[] Elements =
        {
            "water", "fire", "wind", "smoke", "ice", "earth", "lightning", "light",
            "crystal", "decay", "dark_matter", "gravity", "magnetism", "life"
        };

        [MenuItem("Elemental Legion/UI/Configure Concept Art")]
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play 중에는 UI 자산을 구성할 수 없습니다.");
            AssetDatabase.Refresh();
            foreach (string name in new[] { "CosmicBackdrop", "ElementIcons", "FacilityIcons" })
            {
                string path = Root + name + ".png";
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) throw new InvalidOperationException("UI 그림이 없습니다: " + path);
                importer.textureType = TextureImporterType.Default;
                importer.mipmapEnabled = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.alphaIsTransparency = name != "CosmicBackdrop";
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.maxTextureSize = 2048;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Resources")) AssetDatabase.CreateFolder("Assets/_Project", "Resources");
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Resources/UI")) AssetDatabase.CreateFolder("Assets/_Project/Resources", "UI");
            var art = AssetDatabase.LoadAssetAtPath<LegionUiArt>(AssetPath);
            if (art == null)
            {
                art = ScriptableObject.CreateInstance<LegionUiArt>();
                AssetDatabase.CreateAsset(art, AssetPath);
            }
            var serialized = new SerializedObject(art);
            serialized.FindProperty("backdrop").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "CosmicBackdrop.png");
            var icons = serialized.FindProperty("elementIcons");
            icons.arraySize = Elements.Length;
            Texture2D elementTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "ElementIcons.png");
            var data = new ElementalLegion.Shared.Data.GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            for (int index = 0; index < Elements.Length; index++)
            {
                var entry = icons.GetArrayElementAtIndex(index);
                entry.FindPropertyRelative("id").stringValue = Elements[index];
                foreach (var definition in data.Root["elements"])
                    if ((string)definition["id"] == Elements[index])
                        entry.FindPropertyRelative("displayName").stringValue = (string)definition["displayName"];
                entry.FindPropertyRelative("sprite").objectReferenceValue = SpriteFor(art, elementTexture, Elements[index], index, 4, 4);
            }
            var facilities = serialized.FindProperty("buildingIcons");
            facilities.arraySize = 6;
            Texture2D facilityTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "FacilityIcons.png");
            for (int index = 0; index < facilities.arraySize; index++)
                facilities.GetArrayElementAtIndex(index).objectReferenceValue = SpriteFor(art, facilityTexture, "Facility" + index, index, 3, 2);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(art);
            AssetDatabase.SaveAssets();
        }

        private static Sprite SpriteFor(LegionUiArt art, Texture2D texture, string name, int index, int columns, int rows)
        {
            Sprite existing = AssetDatabase.LoadAllAssetsAtPath(AssetPath).OfType<Sprite>().FirstOrDefault(sprite => sprite.name == name);
            float width = texture.width / (float)columns, height = texture.height / (float)rows;
            var rect = new Rect(index % columns * width, (rows - 1 - index / columns) * height, width, height);
            if (existing != null && existing.texture == texture && existing.rect == rect) return existing;
            var sprite = Sprite.Create(texture, rect, Vector2.one * 0.5f, 100, 0, SpriteMeshType.FullRect);
            sprite.name = name;
            if (existing != null)
            {
                // 하위 자산의 파일 ID를 유지해 기존 아이콘 참조를 보존한다.
                EditorUtility.CopySerialized(sprite, existing);
                UnityEngine.Object.DestroyImmediate(sprite);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.AddObjectToAsset(sprite, art);
            return sprite;
        }
    }
}
