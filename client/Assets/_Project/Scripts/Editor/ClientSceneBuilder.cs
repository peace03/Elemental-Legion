using System;
using System.IO;
using System.Linq;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.Camera;
using ElementalLegion.Client.Input;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace ElementalLegion.Client.Editor
{
    /// <summary>M1 시작 씬과 프리팹을 명시적으로 한 번 생성한다. 기존 자산을 덮어쓰거나 사용자 씬을 저장하지 않는다.</summary>
    public static class ClientSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/Legion.unity";
        public const string PrefabPath = "Assets/_Project/Prefabs/LegionClient.prefab";
        private const string InputPath = "Assets/_Project/Settings/Input/InputSystem_Actions.inputactions";
        private const string FontPath = "Assets/_Project/Settings/UI/LegionUiSDF.asset";
        private static readonly Color Background = new Color(0.025f, 0.05f, 0.09f, 0.97f);

        [MenuItem("Elemental Legion/Create M1 Scene")]
        public static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play 중에는 씬을 구성할 수 없습니다.");
            if (File.Exists(ScenePath) || File.Exists(PrefabPath)) throw new InvalidOperationException("M1 씬/프리팹이 이미 있습니다. 기존 자산을 덮어쓰지 않습니다.");
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputPath);
            var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
            if (actions == null || data == null) throw new InvalidOperationException("공유 데이터/Input Actions가 없습니다.");
            Folder("Assets/_Project/Prefabs"); Folder("Assets/_Project/Materials"); Folder("Assets/_Project/Settings/UI");
            TMP_FontAsset font = CreateFont();
            Scene original = SceneManager.GetActiveScene();
            bool clean = true;
            for (int index = 0; index < SceneManager.sceneCount; index++) clean &= !SceneManager.GetSceneAt(index).isDirty;
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var root = new GameObject("LegionClient"); root.SetActive(false);
            try
            {
                var bootstrap = root.AddComponent<ClientBootstrap>();
                var input = root.AddComponent<LegionInput>(); Set(input, "actions", actions);
                var presentation = root.AddComponent<LegionPresentation>();
                ParticleSystem particles = CreateMarkers(root.transform);
                Transform grid = CreateGrid(root.transform);
                Set(presentation, "markers", particles); Set(presentation, "grid", grid);
                Set(presentation, "groundMaterial", AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Materials/MovementGrid.mat"));
                Set(bootstrap, "menuFont", AssetDatabase.LoadAssetAtPath<Font>("Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf"));
                var cameraObject = new GameObject("LegionCamera", typeof(UnityEngine.Camera), typeof(AudioListener), typeof(LegionCamera));
                cameraObject.transform.SetParent(root.transform, false);
                var camera = cameraObject.GetComponent<UnityEngine.Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.012f, 0.025f, 0.05f);
                camera.orthographic = true; camera.orthographicSize = 30; camera.nearClipPlane = 0.1f; camera.farClipPlane = 300;
                cameraObject.transform.position = new Vector3(0, 48, -25);
                cameraObject.transform.rotation = Quaternion.LookRotation(new Vector3(0, -48, 25));
                var follow = cameraObject.GetComponent<LegionCamera>(); Set(follow, "view", camera);
                ConnectionPanel panel = CreateUi(root.transform, actions, font);
                Set(bootstrap, "gameData", data); Set(bootstrap, "input", input); Set(bootstrap, "presentation", presentation);
                Set(bootstrap, "legionCamera", follow); Set(bootstrap, "panel", panel);
                root.SetActive(true);
                PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath, InteractionMode.AutomatedAction);
                EditorSceneManager.SaveScene(scene, ScenePath);
                var existing = EditorBuildSettings.scenes.Where(item => item.path != ScenePath).ToList();
                existing.Insert(0, new EditorBuildSettingsScene(ScenePath, true)); EditorBuildSettings.scenes = existing.ToArray();
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            }
            if (clean) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log("M1 씬과 프리팹 구성 완료. 서버 --listen 실행 후 사람이 Play에서 접속·이동을 확인하세요.");
        }

        private static TMP_FontAsset CreateFont()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (existing != null) { EnsureUiGlyphs(existing); return existing; }
            Font source = AssetDatabase.LoadAssetAtPath<Font>("Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf");
            if (source == null || Shader.Find("TextMeshPro/Distance Field") == null) throw new InvalidOperationException("Noto 원본과 TMP 기본 자산을 먼저 가져와야 합니다.");
            var font = TMP_FontAsset.CreateFontAsset(source, 48, 5, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            font.name = "LegionUiSDF";
            EnsureUiGlyphs(font);
            AssetDatabase.CreateAsset(font, FontPath);
            AssetDatabase.AddObjectToAsset(font.material, font);
            foreach (Texture2D texture in font.atlasTextures) if (texture != null) AssetDatabase.AddObjectToAsset(texture, font);
            EditorUtility.SetDirty(font);
            return font;
        }

        /// <summary>데이터의 실제 표시 이름과 현재 화면 문구를 정적 아틀라스에 포함한다.</summary>
        public static void EnsureUiGlyphs(TMP_FontAsset font)
        {
            font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            Set(font, "m_SourceFontFile", AssetDatabase.LoadAssetAtPath<Font>("Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf"));
            var data = new ElementalLegion.Shared.Data.GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new ElementalLegion.Shared.Data.GameRules(data);
            string text = "ELEMENTAL LEGION 군단 이동 서버 주소 시작 원소 접속 연결 종료 서버 주소와 시작 원소를 선택하세요.";
            foreach (int index in rules.StartingElements) text += (string)data.Root["elements"][index]["displayName"];
            foreach (string path in new[] { "Assets/_Project/Scripts/Runtime/Bootstrap/ClientBootstrap.cs", "Assets/_Project/Scripts/Runtime/UI/ConnectionPanel.cs" })
                foreach (System.Text.RegularExpressions.Match literal in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), "\"[^\"\\r\\n]*\"")) text += literal.Value;
            string characters = new string(Enumerable.Range(32, 95).Select(value => (char)value).Concat(text).Distinct().ToArray());
            if (!font.TryAddCharacters(characters, out string missing)) throw new InvalidOperationException("UI 글리프 생성 실패: " + missing);
            font.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(font);
            foreach (Texture2D texture in font.atlasTextures) if (texture != null) EditorUtility.SetDirty(texture);
        }

        private static ParticleSystem CreateMarkers(Transform parent)
        {
            var go = new GameObject("UnitMarkers", typeof(ParticleSystem)); go.transform.SetParent(parent, false);
            var system = go.GetComponent<ParticleSystem>(); system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = system.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = 4097;
            var emission = system.emission; emission.enabled = false;
            var shape = system.shape; shape.enabled = false;
            Shader shader = Shader.Find("ElementalLegion/LegionMarkers");
            if (shader == null) throw new InvalidOperationException("군단 마커 셰이더가 없습니다.");
            var material = new Material(shader) { name = "LegionMarkers" };
            AssetDatabase.CreateAsset(material, "Assets/_Project/Materials/LegionMarkers.mat");
            var renderer = go.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = material; renderer.renderMode = ParticleSystemRenderMode.Billboard;
            return system;
        }

        private static Transform CreateGrid(Transform parent)
        {
            var grid = new GameObject("MovementGrid"); grid.transform.SetParent(parent, false);
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            var material = new Material(shader) { name = "MovementGrid" }; material.SetColor("_BaseColor", new Color(0.055f, 0.12f, 0.17f));
            AssetDatabase.CreateAsset(material, "Assets/_Project/Materials/MovementGrid.mat");
            for (int line = -6; line <= 6; line++)
            {
                for (int axis = 0; axis < 2; axis++)
                {
                    var go = new GameObject((axis == 0 ? "X" : "Z") + (line + 6)); go.transform.SetParent(grid.transform, false);
                    var renderer = go.AddComponent<LineRenderer>(); renderer.sharedMaterial = material; renderer.useWorldSpace = false;
                    renderer.positionCount = 2; renderer.startWidth = 0.035f; renderer.endWidth = 0.035f;
                    renderer.SetPosition(0, axis == 0 ? new Vector3(-60, 0, line * 10) : new Vector3(line * 10, 0, -60));
                    renderer.SetPosition(1, axis == 0 ? new Vector3(60, 0, line * 10) : new Vector3(line * 10, 0, 60));
                }
            }
            return grid.transform;
        }

        private static ConnectionPanel CreateUi(Transform parent, InputActionAsset actions, TMP_FontAsset font)
        {
            var canvasObject = new GameObject("ConnectionCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(parent, false);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720); scaler.matchWidthOrHeight = 0.5f;
            var card = new GameObject("ConnectionPanel", typeof(RectTransform), typeof(Image), typeof(ConnectionPanel)); card.transform.SetParent(canvasObject.transform, false);
            RectTransform rect = card.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(24, -24); rect.sizeDelta = new Vector2(440, 440);
            card.GetComponent<Image>().color = Background;
            Label("Title", "ELEMENTAL LEGION", card.transform, font, 28, new Vector2(24, -24), new Vector2(392, 42), new Color(0.25f, 0.9f, 1));
            Label("Subtitle", "군단 이동", card.transform, font, 18, new Vector2(24, -68), new Vector2(392, 30), new Color(0.6f, 0.72f, 0.8f));
            Label("HostLabel", "서버 주소", card.transform, font, 16, new Vector2(24, -112), new Vector2(392, 28), Color.white);
            var hostObject = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources()); hostObject.name = "ServerAddress"; hostObject.transform.SetParent(card.transform, false);
            Place(hostObject, new Vector2(24, -144), new Vector2(392, 44)); var host = hostObject.GetComponent<TMP_InputField>(); host.text = "127.0.0.1"; host.characterLimit = 253;
            Label("ElementLabel", "시작 원소", card.transform, font, 16, new Vector2(24, -202), new Vector2(392, 28), Color.white);
            var dropdownObject = TMP_DefaultControls.CreateDropdown(new TMP_DefaultControls.Resources()); dropdownObject.name = "StartingElement"; dropdownObject.transform.SetParent(card.transform, false);
            Place(dropdownObject, new Vector2(24, -234), new Vector2(392, 44)); var dropdown = dropdownObject.GetComponent<TMP_Dropdown>();
            var data = new ElementalLegion.Shared.Data.GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new ElementalLegion.Shared.Data.GameRules(data);
            dropdown.ClearOptions(); dropdown.AddOptions(rules.StartingElements.Select(index => (string)data.Root["elements"][index]["displayName"]).ToList());
            foreach (TMP_Text text in card.GetComponentsInChildren<TMP_Text>(true)) { text.font = font; if (text != card.transform.Find("Title").GetComponent<TMP_Text>()) text.fontSize = 18; text.enableAutoSizing = false; }
            Button join = Button("Join", "접속", card.transform, font, new Vector2(24, -302), new Color(0.07f, 0.45f, 0.55f));
            Button leave = Button("Leave", "연결 종료", card.transform, font, new Vector2(228, -302), new Color(0.12f, 0.2f, 0.27f)); leave.interactable = false;
            TMP_Text status = Label("Status", "서버 주소와 시작 원소를 선택하세요.", card.transform, font, 17, new Vector2(24, -364), new Vector2(392, 64), new Color(0.7f, 0.83f, 0.9f));
            var panel = card.GetComponent<ConnectionPanel>(); Set(panel, "host", host); Set(panel, "element", dropdown); Set(panel, "join", join); Set(panel, "leave", leave); Set(panel, "status", status);
            var eventObject = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule)); eventObject.transform.SetParent(parent, false);
            var module = eventObject.GetComponent<InputSystemUIInputModule>();
            ConfigureUiInput(module, actions);
            return panel;
        }

        /// <summary>Input Actions의 영속 하위 자산을 직접 연결해 임시 ScriptableObject 참조가 프리팹 저장에서 사라지는 것을 막는다.</summary>
        public static void ConfigureUiInput(InputSystemUIInputModule module, InputActionAsset actions)
        {
            var references = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(actions)).OfType<InputActionReference>().ToArray();
            Set(module, "m_ActionsAsset", actions);
            string[] fields = { "m_PointAction", "m_LeftClickAction", "m_ScrollWheelAction", "m_MoveAction", "m_SubmitAction", "m_CancelAction" };
            string[] names = { "Point", "Click", "ScrollWheel", "Navigate", "Submit", "Cancel" };
            for (int index = 0; index < fields.Length; index++)
            {
                InputAction action = actions.FindAction("UI/" + names[index], true);
                Set(module, fields[index], references.First(reference => reference.action == action));
            }
            EditorUtility.SetDirty(module);
        }

        private static Button Button(string name, string label, Transform parent, TMP_FontAsset font, Vector2 position, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button)); go.transform.SetParent(parent, false);
            Place(go, position, new Vector2(188, 46)); go.GetComponent<Image>().color = color;
            var text = Label("Label", label, go.transform, font, 18, Vector2.zero, new Vector2(188, 46), Color.white); text.alignment = TextAlignmentOptions.Center;
            return go.GetComponent<Button>();
        }

        private static TMP_Text Label(string name, string content, Transform parent, TMP_FontAsset font, int size, Vector2 position, Vector2 dimensions, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(parent, false); Place(go, position, dimensions);
            var text = go.GetComponent<TextMeshProUGUI>(); text.font = font; text.fontSize = size; text.text = content; text.color = color; text.raycastTarget = false; text.enableAutoSizing = false;
            return text;
        }

        private static void Place(GameObject go, Vector2 position, Vector2 size)
        { var rect = go.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = position; rect.sizeDelta = size; }

        private static void Set(UnityEngine.Object target, string field, UnityEngine.Object value)
        { var serialized = new SerializedObject(target); serialized.FindProperty(field).objectReferenceValue = value; serialized.ApplyModifiedPropertiesWithoutUndo(); }

        private static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            Folder(Path.GetDirectoryName(path).Replace('\\', '/')); AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\', '/'), Path.GetFileName(path));
        }
    }
}
