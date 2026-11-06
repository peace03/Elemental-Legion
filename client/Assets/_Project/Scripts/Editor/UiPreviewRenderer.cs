using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElementalLegion.Client.Input;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Editor
{
    /// <summary>실제 UI를 별도 씬에서 렌더링해 검사한다. Play·접속·사용자 씬 저장은 수행하지 않는다.</summary>
    public static class UiPreviewRenderer
    {
        public static readonly string[] Pages =
        {
            "Title", "Rooms", "RoomCreate", "Lobby", "SettingsAudio", "SettingsVideo", "SettingsAccessibility", "SettingsKeys",
            "Dashboard", "Pause", "Defeat", "Hud", "Construction", "LobbyPartial", "ConstructionTooltip"
        };

        [Serializable]
        public sealed class Result
        {
            public string page, image;
            public int width, height, percent, buttons;
            public string[] outside;
        }

        [MenuItem("Elemental Legion/UI/Capture Menu Previews")]
        public static void CaptureAll()
        {
            foreach (string page in Pages) Capture(page);
        }

        public static Result Capture(string page, int width = 1280, int height = 720, int percent = 100)
        {
            if (!Pages.Contains(page)) throw new ArgumentException("알 수 없는 UI 화면입니다.", nameof(page));
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play 중에는 UI 검증을 실행하지 않습니다.");
            if (width < 1280 || height < 720 || percent < 90 || percent > 110) throw new ArgumentOutOfRangeException(nameof(width));
            Scene original = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            RenderTexture target = null;
            Texture2D pixels = null;
            GameplayReplica replica = null;
            TMP_FontAsset font = null;
            try
            {
                var root = new GameObject("UI preview fixture");
                var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);
                var rules = new GameRules(document);
                string[] names = rules.Elements.Select(definition => (string)document.Root["elements"][rules.ElementIndex(definition.Id)]["displayName"]).ToArray();
                string[] starting = rules.StartingElements.Select(index => names[index]).ToArray();
                Font sourceFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf");
                font = TMP_FontAsset.CreateFontAsset(sourceFont, 40, 5, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                    1024, 1024, AtlasPopulationMode.Dynamic, true);
                var skin = new ObservatoryUi(font);
                CombatHud hud = null;
                BuildingTooltip tooltipPreview = null;
                RectTransform tooltipParent = null;
                if (page.StartsWith("Settings", StringComparison.Ordinal))
                {
                    var settings = root.AddComponent<ClientSettingsScreen>();
                    settings.Build(skin, new ClientPreferences(), new ControlBindings(), Nothing, Nothing, _ => { });
                    string tab = page == "SettingsAudio" ? "음향" : page == "SettingsVideo" ? "화면" : page == "SettingsAccessibility" ? "표현" : "조작 키";
                    root.GetComponentsInChildren<Button>(true).Single(button => button.GetComponentInChildren<TMP_Text>().text == tab).onClick.Invoke();
                    settings.Scale(percent);
                }
                else if (page == "Hud" || page == "Construction" || page == "ConstructionTooltip")
                {
                    RectTransform background = ObservatoryUi.Canvas(root.transform, "Preview background", -100);
                    skin.Backdrop(background, true);
                    GameObject client = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath), root.transform);
                    foreach (var component in client.GetComponentsInChildren<UnityEngine.Camera>(true)) component.enabled = false;
                    client.GetComponentInChildren<ConnectionPanel>(true).gameObject.SetActive(false);
                    hud = client.GetComponentInChildren<CombatHud>(true);
                    hud.Configure(document, rules);
                    hud.BuildObservatory(skin, rules, client.GetComponent<ElementalVisuals>());
                    GameplayFrame frame = Frame(rules, out replica, out FogStateReplica fog);
                    hud.Apply(frame);
                    hud.Map.ObserveFog(fog);
                    hud.Map.ObserveGameplay(frame);
                    hud.Map.ObservePose(new Point(72, 70));
                    hud.ShowRally(frame.RallyingCount, frame.WaitingGarrisonCount, null, true);
                    hud.ApplyPreferences(new ControlBindings(), percent);
                    if (page != "Hud")
                        client.GetComponentsInChildren<Button>(true).Single(button => button.GetComponentInChildren<TMP_Text>().text == "건설").onClick.Invoke();
                    if (page == "ConstructionTooltip")
                    {
                        tooltipParent = (RectTransform)hud.transform.Find("Connected");
                        tooltipPreview = new BuildingTooltip(skin, tooltipParent, rules, names);
                    }
                }
                else
                {
                    ClientScreen screen = root.AddComponent<ClientScreen>();
                    screen.Initialize(skin, 0, true);
                    switch (page)
                    {
                        case "Title": screen.BuildTitle("별의씨앗_01", _ => { }, Nothing, Nothing, Nothing, Nothing, Nothing); break;
                        case "Rooms":
                            screen.BuildRoomIndex(0, new[]
                            {
                                new ClientScreen.RoomCard("새벽의 은하", 4, 8, false, Nothing),
                                new ClientScreen.RoomCard("별의 요람", 2, 6, false, Nothing),
                                new ClientScreen.RoomCard("원소 연습장", 3, 8, true, Nothing),
                                new ClientScreen.RoomCard("친구들의 우주", 2, 4, false, Nothing, false, true)
                            }, starting, 0, _ => { }, _ => { }, Nothing, Nothing, Nothing); break;
                        case "RoomCreate": screen.BuildRoomForm(rules.MaxPlayers, false, (_, __, ___, ____, _____) => { }, null, Nothing); break;
                        case "Lobby":
                        case "LobbyPartial":
                            screen.BuildLobby(starting, rules, 0, rules.MaxPlayers, (_, __) => { }, _ => { }, (_, __) => { }, Nothing, Nothing, _ => { });
                            var members = Enumerable.Range(0, page == "LobbyPartial" ? 2 : rules.MaxPlayers).Select(index =>
                                new LobbyMember(new string('별', 14) + index.ToString("D2"),
                                    rules.StartingElements[index % starting.Length], index % 3 != 2)).ToArray();
                            var packet = new NetDataWriter();
                            M5Protocol.WriteLobbyState(packet, new string('별', 32), 0, 2, rules.MaxPlayers, members, page == "LobbyPartial" ? 1 : 0);
                            if (!M5Protocol.TryReadLobbyState(new NetDataReader(packet.CopyData()), rules, out LobbyState lobby))
                                throw new InvalidOperationException("대기실 UI 검증 자료가 잘못됐습니다.");
                            screen.SetLobby(lobby, names, rules.StartingElements, null); break;
                        case "Dashboard":
                            screen.BuildDashboard(Nothing, Nothing, null);
                            screen.Observe(Frame(rules, out replica, out _), rules, names); break;
                        case "Pause": screen.BuildPause(Nothing, Nothing, Nothing, Nothing); break;
                        case "Defeat": screen.BuildResult(false, Nothing); break;
                    }
                    screen.Scale(percent);
                }

                var cameraObject = new GameObject("UI preview camera", typeof(UnityEngine.Camera));
                var camera = cameraObject.GetComponent<UnityEngine.Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.transform.position = new Vector3(0, 0, -10);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = ObservatoryUi.Background;
                camera.cullingMask = 1 << 31;
                target = new RenderTexture(width, height, 24);
                target.Create();
                camera.targetTexture = target;
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
                foreach (Canvas canvas in root.GetComponentsInChildren<Canvas>(true))
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = camera;
                    canvas.planeDistance = 1;
                    CanvasScaler scale = canvas.GetComponent<CanvasScaler>();
                    if (scale != null) scale.enabled = false;
                    canvas.scaleFactor = Mathf.Min(width / 1280f, height / 720f) * percent / 100;
                }
                Canvas.ForceUpdateCanvases();
                if (hud != null) hud.ApplyPreferences(new ControlBindings(), percent);
                if (tooltipPreview != null)
                {
                    Vector3 corner = tooltipParent.TransformPoint(new Vector2(tooltipParent.rect.xMax - 4, tooltipParent.rect.yMin + 4));
                    tooltipPreview.ShowMenu(BuildingKind.EnergyTower, rules.ElementIndex("dark_matter"), null, new Vector2(corner.x, corner.y));
                }
                foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
                    if (text.gameObject.activeInHierarchy) text.ForceMeshUpdate();
                Canvas.ForceUpdateCanvases();
                var outside = new List<string>();
                var corners = new Vector3[4];
                foreach (Selectable control in root.GetComponentsInChildren<Selectable>())
                {
                    ((RectTransform)control.transform).GetWorldCorners(corners);
                    foreach (Vector3 corner in corners)
                    {
                        Vector3 point = camera.WorldToViewportPoint(corner);
                        if (point.x < -0.002f || point.x > 1.002f || point.y < -0.002f || point.y > 1.002f)
                        { outside.Add(control.name); break; }
                    }
                }
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = target;
                    pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                    pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    pixels.Apply();
                }
                finally { RenderTexture.active = previous; }
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.artifacts/ui-reconstruction/previews"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"{page}-{width}x{height}-{percent}.png");
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                var result = new Result { page = page, image = path, width = width, height = height, percent = percent,
                    buttons = root.GetComponentsInChildren<Button>().Length, outside = outside.ToArray() };
                File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonUtility.ToJson(result, true));
                return result;
            }
            finally
            {
                replica?.Dispose();
                if (pixels != null) Object.DestroyImmediate(pixels);
                if (target != null) { target.Release(); Object.DestroyImmediate(target); }
                EditorSceneManager.CloseScene(scene, true);
                if (font != null)
                {
                    foreach (Texture2D texture in font.atlasTextures) if (texture != null) Object.DestroyImmediate(texture);
                    if (font.material != null) Object.DestroyImmediate(font.material);
                    Object.DestroyImmediate(font);
                }
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            }
        }

        private static GameplayFrame Frame(GameRules rules, out GameplayReplica replica, out FogStateReplica fog)
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Point(50), new Point(72, 70), 4, 128, true, true, "별의씨앗_01"));
            for (int index = 0; index < 28; index++)
                setup.Units.Add(new UnitSpawn(1, rules.Elements[index % rules.Elements.Count].Id,
                    index >= 22 ? new Point(50 + index % 4, 58) : new Point(70 + index % 7, 70 + index / 7), garrison: index >= 22));
            var world = new SimulationWorld(rules, setup, 1);
            var fogSender = new FogReplicationState(rules);
            fog = new FogStateReplica(rules);
            var writer = new NetDataWriter();
            fogSender.Prepare(world.GetView(1));
            for (int part = 0; part < fogSender.ChunkCount; part++)
            { fogSender.WriteChunk(writer, part); fog.TryAccept(new NetDataReader(writer.CopyData())); }
            var sender = new GameplayReplicationState(rules);
            replica = new GameplayReplica(rules);
            sender.Prepare(world.GetView(1), fogSender.Revision);
            for (int part = 0; part < sender.ChunkCount; part++)
            { sender.WriteChunk(writer, part); replica.TryAccept(new NetDataReader(writer.CopyData()), 0); }
            if (!replica.TryPublish(fog, out GameplayFrame frame)) throw new InvalidOperationException("HUD UI 검증 자료를 구성할 수 없습니다.");
            return frame;
        }

        private static void Nothing() { }
    }
}
