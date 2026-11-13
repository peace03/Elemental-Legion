using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ElementalLegion.Client.FogOfWar;
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
using UnityEngine.EventSystems;
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

        public static readonly string[] UiControlsPages =
        {
            "Hud", "Training", "Forces", "Construction", "Dashboard", "Pause", "SettingsAudio", "BuildingInfo", "ElementInfo",
            "TrainingQueue", "ConstructionLocked", "BaseAlert", "RecallCasting", "RecallCooldown", "MapOverlap", "QuickTransfer"
        };

        public static readonly string[] UiControlsDetailPages =
        {
            "TrainingEnergy", "DashboardJourney", "DashboardMatchups", "SettingsVideo", "SettingsAccessibility", "SettingsKeys"
        };

        private static readonly string[] FacilityPages =
        {
            "BuildingInfo", "FacilityLocked", "FacilityLowCores", "FacilityConstruction", "FacilityMaxRank",
            "FacilityTower", "FacilityNatureForge", "FacilityEnergyForge", "FacilityBaseMax", "FacilityEnemy"
        };

        [Serializable]
        public sealed class Result
        {
            public string page, image;
            public string fixture;
            public int width, height, percent, buttons;
            public string[] outside, missingGlyphs;
        }

        [MenuItem("Elemental Legion/UI/Capture Menu Previews")]
        public static void CaptureAll()
        {
            foreach (string page in Pages) Capture(page);
        }

        public static Result Capture(string page, int width = 1280, int height = 720, int percent = 100)
        {
            if (!Pages.Contains(page)) throw new ArgumentException("알 수 없는 UI 화면입니다.", nameof(page));
            return CapturePage(page, width, height, percent, false);
        }

        public static Result CaptureUiControls(string page, int width = 1280, int height = 720, int percent = 100)
        {
            if (!UiControlsPages.Contains(page) && !UiControlsDetailPages.Contains(page) && !FacilityPages.Contains(page))
                throw new ArgumentException("알 수 없는 전장 UI 화면입니다.", nameof(page));
            return CapturePage(page, width, height, percent, true);
        }

        public static Result[] CaptureUiControlsAt(int width, int height, int percent) =>
            UiControlsPages.Select(page => CaptureUiControls(page, width, height, percent)).ToArray();

        [MenuItem("Elemental Legion/UI/Capture Facility Upgrade Cards")]
        public static void CaptureFacilityCards()
        {
            if (Application.isBatchMode && string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
                EditorSceneManager.OpenScene(ClientSceneBuilder.ScenePath, OpenSceneMode.Single);
            foreach (var size in new[] { (1280, 720), (1920, 1080) })
                foreach (string page in FacilityPages)
                    CaptureUiControls(page, size.Item1, size.Item2);
        }

        [MenuItem("Elemental Legion/UI/Capture Battlefield UI Matrix")]
        public static void CaptureUiControlsMatrix()
        {
            foreach (var size in new[] { (1280, 720), (1920, 1080), (2560, 1440) })
                foreach (int percent in new[] { 90, 100, 110 })
                    CaptureUiControlsAt(size.Item1, size.Item2, percent);
        }

        private static Result CapturePage(string page, int width, int height, int percent, bool uiControls)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play 중에는 UI 검증을 실행하지 않습니다.");
            if (width < 1280 || height < 720 || percent < 90 || percent > 110) throw new ArgumentOutOfRangeException(nameof(width));
            EventSystem eventSystem = EventSystem.current;
            GameObject selection = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
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
                if (uiControls) rules = rules.ForRoom(2);
                string[] names = rules.Elements.Select(definition => (string)document.Root["elements"][rules.ElementIndex(definition.Id)]["displayName"]).ToArray();
                string[] starting = rules.StartingElements.Select(index => names[index]).ToArray();
                TMP_FontAsset captureFont;
                if (uiControls) captureFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
                else
                {
                    Font sourceFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf");
                    captureFont = font = TMP_FontAsset.CreateFontAsset(sourceFont, 40, 5, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                        1024, 1024, AtlasPopulationMode.Dynamic, true);
                }
                var skin = new ObservatoryUi(captureFont);
                CombatHud hud = null;
                BuildingTooltip tooltipPreview = null;
                RectTransform tooltipParent = null;
                UnityEngine.Camera battlefieldCamera = null;
                ElementalVisuals visuals = null;
                SnapshotTimeline timeline = null;
                GameplayFrame battlefieldFrame = null;
                Point legionPosition = default;
                if (!uiControls && page.StartsWith("Settings", StringComparison.Ordinal))
                {
                    var settings = root.AddComponent<ClientSettingsScreen>();
                    settings.Build(skin, new ClientPreferences(), new ControlBindings(), Nothing, Nothing, _ => { });
                    string tab = page == "SettingsAudio" ? "음향" : page == "SettingsVideo" ? "화면" : page == "SettingsAccessibility" ? "표현" : "조작 키";
                    root.GetComponentsInChildren<Button>(true).Single(button => button.GetComponentInChildren<TMP_Text>().text == tab).onClick.Invoke();
                    settings.Scale(percent);
                }
                else if (uiControls || page == "Hud" || page == "Construction" || page == "ConstructionTooltip")
                {
                    if (!uiControls)
                    {
                        RectTransform background = ObservatoryUi.Canvas(root.transform, "Preview background", -100);
                        skin.Backdrop(background, true);
                    }
                    GameObject client = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath), root.transform);
                    foreach (var component in client.GetComponentsInChildren<UnityEngine.Camera>(true)) component.enabled = false;
                    client.GetComponentInChildren<ConnectionPanel>(true).gameObject.SetActive(false);
                    hud = client.GetComponentInChildren<CombatHud>(true);
                    hud.Configure(document, rules);
                    visuals = client.GetComponent<ElementalVisuals>();
                    hud.BuildObservatory(skin, rules, visuals);
                    GameplayFrame frame;
                    FogStateReplica fog;
                    if (uiControls)
                    {
                        frame = BuildBattlefieldFrame(page, rules, hud.Map, out replica, out fog, out timeline, out legionPosition);
                        battlefieldFrame = frame;
                        battlefieldCamera = visuals.TargetCamera;
                        ConfigureBattlefieldCamera(battlefieldCamera, rules);
                        client.layer = 31;
                        client.GetComponent<LegionPresentation>().ConfigureGround(rules.MapSize);
                        var vision = client.GetComponentInChildren<FogPresentation>(true);
                        vision.Initialize(rules);
                        vision.Apply(fog);
                        visuals.Initialize();
                        visuals.Observe(frame);
                    }
                    else
                    {
                        frame = Frame(rules, out replica, out fog);
                        legionPosition = new Point(72, 70);
                    }
                    hud.Apply(frame);
                    if (!uiControls) hud.Map.ObserveFog(fog);
                    hud.Map.ObserveGameplay(frame);
                    hud.Map.ObservePose(legionPosition);
                    hud.ShowRally(frame.RallyingCount, frame.WaitingGarrisonCount, null, true);
                    hud.ApplyPreferences(new ControlBindings(), percent);
                    if (uiControls) SetBattlefieldPage(page, root, hud, skin, frame, rules, document, names, percent);
                    else if (page != "Hud") hud.SetPanel(HudPanelKind.Construction);
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

                var camera = battlefieldCamera != null ? battlefieldCamera :
                    new GameObject("UI preview camera", typeof(UnityEngine.Camera)).GetComponent<UnityEngine.Camera>();
                camera.enabled = false;
                if (battlefieldCamera == null)
                {
                    camera.orthographic = true;
                    camera.orthographicSize = height * 0.5f;
                    camera.transform.position = new Vector3(0, 0, -10);
                }
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
                if (uiControls && (FacilityPages.Contains(page) || page == "ElementInfo"))
                {
                    ShowBattlefieldCard(page, hud, skin, battlefieldFrame, rules, names);
                    foreach (Transform child in hud.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
                }
                if (tooltipPreview != null)
                {
                    Vector3 corner = tooltipParent.TransformPoint(new Vector2(tooltipParent.rect.xMax - 4, tooltipParent.rect.yMin + 4));
                    tooltipPreview.ShowMenu(BuildingKind.EnergyTower, rules.ElementIndex("dark_matter"), null, new Vector2(corner.x, corner.y));
                }
                if (battlefieldCamera != null)
                {
                    visuals.Render(timeline, 1, legionPosition, battlefieldFrame.Tick);
                    visuals.SimulatePreview((float)rules.DeltaTime);
                    hud.RefreshBattlefieldSquadBadges();
                }
                var missing = new SortedSet<string>(StringComparer.Ordinal);
                foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>())
                {
                    if (uiControls && text.font != null)
                    {
                        string value = text.text ?? string.Empty;
                        foreach (char glyph in text.richText ? Regex.Replace(value, "<[^>]+>", string.Empty) : value)
                            if (!char.IsWhiteSpace(glyph) && !text.font.HasCharacter(glyph, searchFallbacks: true))
                                missing.Add($"U+{(int)glyph:X4} {glyph}");
                    }
                    text.ForceMeshUpdate();
                }
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
                if (battlefieldCamera != null)
                {
                    RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                }
                else RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = target;
                    pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                    pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    pixels.Apply();
                }
                finally { RenderTexture.active = previous; }
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, uiControls
                    ? "../../.artifacts/ui-controls/previews" : "../../.artifacts/ui-reconstruction/previews"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"{page}-{width}x{height}-{percent}.png");
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                var result = new Result { page = page, image = path, width = width, height = height, percent = percent,
                    buttons = root.GetComponentsInChildren<Button>().Length, outside = outside.ToArray(), missingGlyphs = missing.ToArray(), fixture = uiControls
                        ? "2인 방 고정 시드 시뮬레이션 관측과 실제 프리팹 · Editor 렌더" + (page == "BaseAlert" ? " · 경보만 HUD에 직접 전달" : string.Empty) : null };
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
                if (eventSystem != null && eventSystem.currentSelectedGameObject != selection)
                    eventSystem.SetSelectedGameObject(selection);
            }
        }

        private static void ConfigureBattlefieldCamera(UnityEngine.Camera camera, GameRules rules)
        {
            float center = rules.MapSize * .5f;
            camera.orthographic = true;
            camera.orthographicSize = 25;
            camera.transform.position = new Vector3(center, 52, center - 26);
            camera.transform.LookAt(new Vector3(center, 0, center));
        }

        private static void SetBattlefieldPage(string page, GameObject root, CombatHud hud, ObservatoryUi skin,
            GameplayFrame frame, GameRules rules, GameDataDocument document, string[] names, int percent)
        {
            switch (page)
            {
                case "Training":
                case "TrainingQueue":
                case "TrainingEnergy":
                    hud.SetPanel(HudPanelKind.Training);
                    if (page == "TrainingEnergy") Press(hud, "에너지");
                    int element = page == "TrainingEnergy" ? rules.ElementIndex("dark_matter") : frame.MainElement;
                    Press(hud, $"Train{element}");
                    break;
                case "Forces": hud.SetPanel(HudPanelKind.Forces); break;
                case "QuickTransfer":
                    bool found = false;
                    foreach (NetworkEvent entry in frame.Events)
                        if (entry.Data.Kind == SimEventKind.CommandApplied && entry.Data.Detail == (int)CommandKind.QuickTransfer)
                        {
                            found = true;
                            hud.SetQuickTransferFeedback(4, 2, $"{entry.Data.Amount:0}기 출격");
                        }
                    if (!found) throw new InvalidOperationException("본인 빠른 전송 실행 결과가 관측에 없습니다.");
                    break;
                case "Construction":
                case "ConstructionLocked":
                    hud.SetPanel(HudPanelKind.Construction);
                    // 잠긴 버튼의 배치 금지는 유지하고 실제 상세 설명만 재현한다.
                    if (page == "ConstructionLocked") Press(hud, BuildingTooltip.Name(BuildingKind.EnergyForge));
                    break;
                case "BaseAlert": hud.ShowBaseUnderAttack(Time.unscaledTimeAsDouble); break;
                case "Dashboard":
                case "DashboardJourney":
                case "DashboardMatchups":
                case "Pause":
                    hud.BlockInput(true);
                    var screen = root.AddComponent<ClientScreen>();
                    screen.Initialize(skin, 60, false);
                    if (page == "Pause") screen.BuildPause(Nothing, Nothing, Nothing, Nothing);
                    else
                    {
                        bool held = page == "Dashboard";
                        screen.BuildDashboard(held ? null : (Action)Nothing, () => screen.ShowMatchups(rules, document, names),
                            held ? new ControlBindings().Label(ControlBindings.Dashboard) : null);
                        screen.Observe(frame, rules, names);
                        if (page == "DashboardJourney") Press(screen, "여정 기록");
                        if (page == "DashboardMatchups") Press(screen, "원소 상성표");
                    }
                    screen.Scale(percent);
                    break;
                case "SettingsAudio":
                case "SettingsVideo":
                case "SettingsAccessibility":
                case "SettingsKeys":
                    hud.BlockInput(true);
                    var settings = root.AddComponent<ClientSettingsScreen>();
                    settings.Build(skin, new ClientPreferences(), new ControlBindings(), Nothing, Nothing, _ => { });
                    settings.ShowOverBattlefield();
                    if (page != "SettingsAudio") Press(settings, page == "SettingsVideo" ? "화면" : page == "SettingsAccessibility" ? "표현" : "조작 키");
                    settings.Scale(percent);
                    break;
            }
        }

        private static void Press(Component view, string name) =>
            view.GetComponentsInChildren<Button>().Single(button => button.name == name).onClick.Invoke();

        private static void ShowBattlefieldCard(string page, CombatHud hud, ObservatoryUi skin,
            GameplayFrame frame, GameRules rules, string[] names)
        {
            var content = (RectTransform)hud.transform.Find("Connected");
            var managementPanel = (RectTransform)content.Find("Construction");
            Vector2 size = managementPanel.sizeDelta;
            Vector2 offset = managementPanel.anchoredPosition;
            if (FacilityPages.Contains(page))
            {
                var commands = (RectTransform)content.Find("Observatory commands");
                offset.y = commands.anchoredPosition.y + commands.rect.height + 8 + size.y - content.rect.height;
                var tooltip = new BuildingTooltip(skin, content, rules, names);
                tooltip.ConfigureCard(skin, tooltip.Hide, Nothing, (_, _, _) => { });
                tooltip.Layout(size.x, size.y, offset);
                foreach (BuildingView building in frame.Buildings)
                {
                    if ((building.OwnerId == frame.PlayerId) == (page == "FacilityEnemy") || building.Kind != PreviewFacility(page)) continue;
                    tooltip.ShowWorld(building, frame.Tick, Vector2.zero, frame, true);
                    tooltip.SetPinned(true);
                    return;
                }
                throw new InvalidOperationException($"{page} 시설 설명을 검증할 관측이 없습니다.");
            }
            else
            {
                var tooltip = new ElementTooltip(skin, content, rules, names, Nothing);
                tooltip.Layout(size.x, size.y, offset);
                tooltip.Show(rules.ElementIndex("water"), frame.MainElement);
            }
        }

        private static BuildingKind PreviewFacility(string page) => page switch
        {
            "FacilityTower" => BuildingKind.NatureTower,
            "FacilityNatureForge" => BuildingKind.NatureForge,
            "FacilityEnergyForge" => BuildingKind.EnergyForge,
            "FacilityBaseMax" or "FacilityEnemy" => BuildingKind.Base,
            _ => BuildingKind.Collector
        };

        private static GameplayFrame BuildBattlefieldFrame(string page, GameRules rules, ObservatoryMap map,
            out GameplayReplica replica, out FogStateReplica fog, out SnapshotTimeline timeline, out Point legionPosition)
        {
            var setup = new WorldSetup { CameraVisibility = true };
            var center = new Point(rules.MapSize * .5f);
            Point basePosition = center + new Point(-13, -8);
            Point squadPosition = center + new Point(4, 4);
            bool locked = page == "ConstructionLocked" || page == "FacilityLocked";
            int baseLevel = page switch { "FacilityBaseMax" => rules.MaxBaseLevel, "FacilityMaxRank" => 5, _ => locked ? 1 : 4 };
            long cores = page switch { "FacilityLowCores" => rules.BuildingCost(BuildingKind.Collector), "FacilityMaxRank" => 1000, _ => 128 };
            setup.Players.Add(new PlayerSpawn(1, "water", basePosition, squadPosition, baseLevel, cores,
                natureForge: true, energyForge: !locked, nickname: "별의씨앗_01"));
            setup.Players.Add(new PlayerSpawn(2, "fire", center + new Point(45, 40), center + new Point(25, 17), 2, 54,
                nickname: "새벽의파수꾼", colorId: 3));
            for (int index = 0; index < 28; index++)
            {
                bool guard = index >= 22;
                Point position = guard ? basePosition + new Point(-4 + index % 4, 7) :
                    squadPosition + new Point(-5 + index % 7 * 1.6f, index / 7 * 1.6f);
                setup.Units.Add(new UnitSpawn(1, rules.Elements[index % rules.Elements.Count].Id, position, garrison: guard));
            }
            for (int index = 0; index < 7; index++)
                setup.Units.Add(new UnitSpawn(2, rules.Elements[index].Id, center + new Point(22 + index % 3 * 1.5f, 14 + index / 3 * 1.5f)));
            setup.Cores.Add(new CoreSpawn(center + new Point(12, 1), 5));
            var world = new SimulationWorld(rules, setup, 103);
            var snapshot = new UnitSnapshotAssembler(rules);
            timeline = new SnapshotTimeline(4);
            if (page == "MapOverlap")
            {
                PushUnits(world.GetView(1), rules, snapshot, timeline, 1);
                map.ObserveUnits(timeline, 1);
                Send(world, SimCommand.Move(1, 1, Point.UnitX));
                for (int step = 0; step < 12; step++) world.Step();
            }
            else if (page == "TrainingQueue")
            {
                Send(world, SimCommand.Train(1, "water", 2, 1));
                Send(world, SimCommand.Train(2, "fire", 1, 3));
                world.Step();
            }
            else if (page == "Forces" || page == "QuickTransfer")
            {
                Send(world, SimCommand.MoveTo(1, 2, squadPosition));
                for (int step = 0; step < 25; step++) world.Step();
                PushUnits(world.GetView(1), rules, snapshot, timeline, 1);
                map.ObserveUnits(timeline, 1);
                Send(world, SimCommand.QuickTransfer(2, 4, 2));
                world.Step();
            }
            else if (page == "RecallCasting" || page == "RecallCooldown")
            {
                Send(world, SimCommand.Recall(1, 1));
                world.Step();
                if (page == "RecallCooldown")
                    for (long step = 0; step < rules.RecallCastTicks; step++) world.Step();
            }
            else if (FacilityPages.Contains(page))
            {
                BuildingKind kind = PreviewFacility(page);
                if (kind == BuildingKind.Collector || kind == BuildingKind.NatureTower)
                {
                    int x = Mathf.RoundToInt(basePosition.X / rules.BuildGridSize);
                    int z = Mathf.RoundToInt((basePosition.Y - 16) / rules.BuildGridSize);
                    int element = kind == BuildingKind.NatureTower ? rules.ElementIndex("water") : -1;
                    Send(world, SimCommand.Build(1, kind, x, z, element));
                    world.Step();
                    if (page != "FacilityConstruction")
                        for (long step = 0; step < rules.BuildingConstructionTicks(kind); step++) world.Step();
                    if (page == "FacilityMaxRank")
                    {
                        BuildingView collector = world.GetView(1).Buildings.Single(building => building.OwnerId == 1 && building.Kind == kind);
                        for (byte rank = 1; rank <= GameRules.MaxFacilityUpgradeRank; rank++)
                        {
                            Send(world, SimCommand.UpgradeFacility((ulong)rank + 1, collector.Id, FacilityUpgradeBranch.A, rank));
                            world.Step();
                        }
                    }
                }
            }
            else
            {
                Send(world, SimCommand.MoveTo(1, 2, center + new Point(-55, 25)));
                Send(world, SimCommand.MoveTo(2, 4, center + new Point(15, -51)));
                for (int step = 0; step < 110; step++) world.Step();
            }
            WorldView view = world.GetView(1);
            if (page == "TrainingQueue" && view.TrainingQueue.Count == 0 ||
                (page == "Forces" || page == "QuickTransfer") && view.Squads[1].ReinforcingCount == 0 ||
                page == "RecallCasting" && view.RecallSquad == 0 || page == "RecallCooldown" && view.RecallReadyTick <= view.Tick)
                throw new InvalidOperationException($"{page} 화면을 검증할 서버 상태가 만들어지지 않았습니다.");
            GameplayFrame frame = PublishView(rules, view, out replica, out fog);
            map.ObserveFog(fog);
            PushUnits(view, rules, snapshot, timeline, fog.Revision);
            map.ObserveUnits(timeline, 1);
            legionPosition = view.LegionPosition;
            return frame;
        }

        private static void Send(SimulationWorld world, SimCommand command)
        {
            if (world.TryEnqueue(1, command) != CommandAdmission.Accepted)
                throw new InvalidOperationException($"{command.Kind} 미리보기 명령을 접수하지 못했습니다.");
        }

        private static void PushUnits(WorldView view, GameRules rules, UnitSnapshotAssembler snapshot, SnapshotTimeline timeline, uint revision)
        {
            var writer = new NetDataWriter();
            for (int part = 0; part < Protocol.ChunkCount(view.Units.Count); part++)
            {
                Protocol.WriteUnits(writer, view, rules.MapSize, part, revision);
                if (snapshot.TryAccept(new NetDataReader(writer.CopyData()))) timeline.Push(snapshot);
            }
            foreach (SquadView squad in view.Squads)
                timeline.ObservePose(new LegionPose(view.Tick, view.PlayerId, squad.AppliedSequence, squad.Position,
                    squad.Direction, squad.Id, squad.MovementRevision));
        }

        private static GameplayFrame Frame(GameRules rules, out GameplayReplica replica, out FogStateReplica fog)
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Point(50), new Point(72, 70), 4, 128, true, true, "별의씨앗_01"));
            for (int index = 0; index < 28; index++)
                setup.Units.Add(new UnitSpawn(1, rules.Elements[index % rules.Elements.Count].Id,
                    index >= 22 ? new Point(50 + index % 4, 58) : new Point(70 + index % 7, 70 + index / 7), garrison: index >= 22));
            return PublishView(rules, new SimulationWorld(rules, setup, 1).GetView(1), out replica, out fog);
        }

        private static GameplayFrame PublishView(GameRules rules, WorldView view, out GameplayReplica replica, out FogStateReplica fog)
        {
            var fogSender = new FogReplicationState(rules);
            fog = new FogStateReplica(rules);
            var writer = new NetDataWriter();
            fogSender.Prepare(view);
            for (int part = 0; part < fogSender.ChunkCount; part++)
            { fogSender.WriteChunk(writer, part); fog.TryAccept(new NetDataReader(writer.CopyData())); }
            var sender = new GameplayReplicationState(rules);
            replica = new GameplayReplica(rules);
            sender.Capture(view);
            sender.Prepare(view, fogSender.Revision);
            for (int part = 0; part < sender.ChunkCount; part++)
            { sender.WriteChunk(writer, part); replica.TryAccept(new NetDataReader(writer.CopyData()), 0); }
            if (!replica.TryPublish(fog, out GameplayFrame frame)) throw new InvalidOperationException("HUD UI 검증 자료를 구성할 수 없습니다.");
            return frame;
        }

        private static void Nothing() { }
    }
}
