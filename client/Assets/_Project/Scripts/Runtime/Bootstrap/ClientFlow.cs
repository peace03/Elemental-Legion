using System;
using System.Collections;
using System.Collections.Generic;
using ElementalLegion.Client.Input;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;

namespace ElementalLegion.Client.Bootstrap
{
    /// <summary>화면 전환을 조정하고 활성 전투 씬에 설정을 전달한다.</summary>
    [DefaultExecutionOrder(-20)]
    public sealed partial class ClientFlow : MonoBehaviour
    {
        private const string Scenes = "Assets/_Project/Scenes/";
        private const string LocalAddress = "127.0.0.1";
        private const string SavedAddresses = "ElementalLegion.Servers";
        private const int UiVolumeChannel = 2;
        private static bool startAtServerBrowser;
        [SerializeField, Tooltip("시작 원소 선택의 원본 데이터입니다.")] private TextAsset gameData;
        [SerializeField, Tooltip("메뉴 조작에 사용하는 기존 입력 자산입니다.")] private InputActionAsset actions;
        [SerializeField, Tooltip("한글 메뉴에 사용할 글꼴입니다.")] private Font menuFont;
        [SerializeField, Tooltip("닉네임 입력과 음량 설정에 사용할 UI 효과음입니다.")] private AudioClip previewClip;

        private readonly List<string> addresses = new List<string> { LocalAddress };
        private readonly List<InputActionReference> uiActions = new List<InputActionReference>();
        private Scene primary, overlay;
        private ClientScreen screen, pause, dashboard, defeat;
        private ClientSettingsScreen settings;
        private ClientBootstrap game;
        private ClientPreferences preferences;
        private ControlBindings controls;
        private ObservatoryUi ui;
        private AudioListener menuListener;
        private UnityEngine.Camera menuCamera;
        private AudioSource preview;
        private GameRules rules;
        private string[] elementNames, startingNames;
        private GameDataDocument document;
        private string address = LocalAddress;
        private int element;
        private long dashboardTick = -1;
        private bool standalone, busy, joining, cancelled, returnToPause;
        private bool dashboardFromKey, dashboardReleased;
        private bool DashboardKeyHeld => Application.isFocused && controls.Held(ControlBindings.Dashboard);

        private void Start()
        {
            if (standalone) return;
            Initialize(ControlBindings.Load());
            BuildEventSystem();
            menuListener = gameObject.AddComponent<AudioListener>();
            menuCamera = gameObject.AddComponent<UnityEngine.Camera>();
            menuCamera.clearFlags = CameraClearFlags.SolidColor;
            menuCamera.backgroundColor = ObservatoryUi.Background;
            menuCamera.cullingMask = 0;
            ClientPreferences.ApplyDisplay(preferences.Display);
            string initial = startAtServerBrowser ? "ServerBrowser" : "Title";
            startAtServerBrowser = false;
            StartCoroutine(OpenMain(initial));
        }

        internal static void AttachStandalone(ClientBootstrap session, TextAsset data, Font font, AudioClip sound, ControlBindings bindings)
        {
            var holder = new GameObject("Observatory flow");
            SceneManager.MoveGameObjectToScene(holder, session.gameObject.scene);
            var flow = holder.AddComponent<ClientFlow>();
            flow.standalone = true;
            flow.gameData = data;
            flow.menuFont = font;
            flow.previewClip = sound;
            flow.Initialize(bindings);
            ClientPreferences.ApplyDisplay(flow.preferences.Display);
            flow.primary = session.gameObject.scene;
            flow.AttachGame(session);
        }

        private void Initialize(ControlBindings bindings)
        {
            if (gameData == null || menuFont == null) throw new InvalidOperationException("관측소 데이터 또는 글꼴 참조가 없습니다.");
            controls = bindings;
            preferences = new ClientPreferences();
            document = new GameDataDocument(gameData.text);
            rules = new GameRules(document);
            network = new NetworkSettings(document);
            if (!standalone && string.IsNullOrWhiteSpace(onlineServiceUrl)) onlineServiceUrl = ReadRelayTestUrl();
            if (!string.IsNullOrWhiteSpace(onlineServiceUrl)) roomTab = RoomTab.Internet;
            elementNames = new string[rules.Elements.Count];
            for (int index = 0; index < elementNames.Length; index++)
                elementNames[index] = (string)document.Root["elements"][index]["displayName"];
            startingNames = new string[rules.StartingElements.Count];
            for (int index = 0; index < startingNames.Length; index++) startingNames[index] = elementNames[rules.StartingElements[index]];
            var font = TMP_FontAsset.CreateFontAsset(menuFont, 40, 5, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            if (font == null) throw new InvalidOperationException("관측소 글꼴을 만들 수 없습니다.");
            ui = new ObservatoryUi(font) { ReduceMotion = preferences.Display.ReduceMotion };
            preview = gameObject.AddComponent<AudioSource>();
            preview.playOnAwake = false;
            preview.spatialBlend = 0;
            addresses.Add(LocalAddress + ":" + network.DemoPort);
            foreach (string saved in PlayerPrefs.GetString(SavedAddresses).Split('\n'))
                if (TryEndpoint(saved, out _, out _) && !ContainsAddress(saved) && addresses.Count < 7) addresses.Add(saved);
        }

        private void BuildEventSystem()
        {
            var root = new GameObject("Application EventSystem");
            root.SetActive(false);
            root.transform.SetParent(transform, false);
            root.AddComponent<EventSystem>();
            var module = root.AddComponent<InputSystemUIInputModule>();
            module.actionsAsset = actions;
            module.point = Reference("Point");
            module.leftClick = Reference("Click");
            module.rightClick = Reference("RightClick");
            module.middleClick = Reference("MiddleClick");
            module.scrollWheel = Reference("ScrollWheel");
            module.move = Reference("Navigate");
            module.submit = Reference("Submit");
            module.cancel = Reference("Cancel");
            root.SetActive(true);
        }

        private InputActionReference Reference(string action)
        {
            var reference = InputActionReference.Create(actions.FindAction("UI/" + action, true));
            uiActions.Add(reference);
            return reference;
        }

        private static IEnumerator Load(string name)
        {
            string path = Scenes + name + ".unity";
            yield return SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
        }

        private static Scene Loaded(string name) => SceneManager.GetSceneByPath(Scenes + name + ".unity");

        private IEnumerator OpenMain(string name, string detail = "")
        {
            busy = true;
            if (game != null) game.SetInputBlocked(true);
            yield return LeaveHostedSession();
            if (overlay.IsValid()) yield return CloseOverlayScene();
            ClosePause();
            if (defeat != null) defeat.SetInteractive(false);
            defeat = null;
            if (screen != null) screen.SetInteractive(false);
            Scene previous = primary;
            if (game != null) game.EndSession();
            game = null;
            yield return Load(name);
            primary = Loaded(name);
            SceneManager.SetActiveScene(primary);
            if (previous.IsValid()) yield return SceneManager.UnloadSceneAsync(previous);
            SetMenuEnvironment(true);
            screen = null;
            if (name == "Title")
            {
                NewScreen();
                screen.BuildTitle(preferences.Nickname, preferences.SaveNickname, () => PreviewAudio(UiVolumeChannel),
                    () => Navigate("ServerBrowser"), OpenSettings,
                    () => screen.ShowMatchups(rules, document, elementNames), Quit);
                screen.Focus();
            }
            else
                ShowBrowser(detail);
            busy = false;
            if (name == "ServerBrowser" && detail.Length == 0 && roomTab != RoomTab.Saved) RefreshRooms();
        }

        private void Navigate(string name)
        {
            if (busy) return;
            if (standalone)
            {
                StartCoroutine(NavigateStandalone(name));
                return;
            }
            StartCoroutine(OpenMain(name));
        }

        private bool ContainsAddress(string host) => addresses.Exists(entry => string.Equals(entry, host, StringComparison.OrdinalIgnoreCase));

        private void SaveAddress(string host)
        {
            host = host.Trim();
            if (!TryEndpoint(host, out _, out _))
            {
                screen.SetConnecting(false, "올바른 IP 또는 서버 이름을 입력하세요.");
                return;
            }
            RememberAddress(host);
            screen.SetServers(addresses);
            screen.SetConnecting(false, "서버 주소를 저장했습니다.");
        }

        private void RememberAddress(string host)
        {
            if (ContainsAddress(host)) return;
            if (addresses.Count == 7) addresses.RemoveAt(addresses.Count - 1);
            addresses.Insert(2, host);
            PlayerPrefs.SetString(SavedAddresses, string.Join("\n", addresses));
            PlayerPrefs.Save();
        }

        private void ServerBack()
        {
            if (joining)
            {
                if (hostedConnection?.IsHostServer == true && !leavingHostedRoom)
                { StartCoroutine(CancelJoinAfterHandoff()); return; }
                if (leavingHostedRoom) return;
                cancelled = true;
                roomOperation?.Cancel();
            }
            else if (showingRoomForm && !busy) ShowBrowser();
            else Navigate("Title");
        }

        private void BeginJoin(string host, int selected)
        {
            if (busy) return;
            address = host.Trim();
            element = selected;
            if (!TryEndpoint(address, out string target, out int port))
            {
                screen.SetConnecting(false, "올바른 IP 또는 서버 이름을 입력하세요.");
                return;
            }
            BeginLanJoin(target, port, true);
        }

        private IEnumerator LoadField(ClientConnection transport)
        {
            SetMenuEnvironment(false);
            SceneManager.sceneLoaded += MuteGameEventSystem;
            yield return Load("Legion");
            SceneManager.sceneLoaded -= MuteGameEventSystem;
            Scene legion = Loaded("Legion");
            ClientBootstrap session = null;
            foreach (GameObject root in legion.GetRootGameObjects())
            {
                session = root.GetComponentInChildren<ClientBootstrap>(true);
                if (session != null) break;
            }
            if (!cancelled && session != null && session.Ready && transport.State == ConnectionState.Connected)
            {
                AttachGame(session);
                session.AdoptConnection(transport);
            }
            if (cancelled || game == null || game.State != ConnectionState.Connected)
            {
                string reason = cancelled ? "접속을 취소했습니다." :
                    game == null ? "전투 씬을 초기화할 수 없습니다." : game.ConnectionDetail;
                if (game != null) game.EndSession();
                game = null;
                yield return SceneManager.UnloadSceneAsync(legion);
                SetMenuEnvironment(true);
                ShowBrowser(string.IsNullOrEmpty(reason) ? "서버 접속에 실패했습니다." : reason);
            }
            else
            {
                Scene previous = primary;
                primary = legion;
                SceneManager.SetActiveScene(primary);
                screen.SetInteractive(false);
                screen.gameObject.SetActive(false);
                screen = null;
                yield return SceneManager.UnloadSceneAsync(previous);
            }
        }

        private static void MuteGameEventSystem(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Legion") return;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (EventSystem events in root.GetComponentsInChildren<EventSystem>(true)) events.gameObject.SetActive(false);
        }

        private void AttachGame(ClientBootstrap session)
        {
            game = session;
            game.AttachFlow(this, ui, !standalone);
            ApplyPreferences();
            RefreshInput();
        }

        internal void ShowMenu()
        {
            if (busy || overlay.IsValid() || game == null || game.State != ConnectionState.Connected) return;
            if (game.CancelConstruction()) return;
            if (pause != null) { ClosePause(); return; }
            var root = new GameObject("Field menu");
            SceneManager.MoveGameObjectToScene(root, primary);
            pause = root.AddComponent<ClientScreen>();
            pause.Initialize(ui, 50, false);
            pause.Scale(preferences.Display.UiPercent);
            pause.BuildPause(ClosePause, OpenSettings, () => Navigate("Title"), Quit);
            pause.Focus();
            RefreshInput();
        }

        private IEnumerator OpenDefeat()
        {
            busy = true;
            game.SetInputBlocked(true);
            if (overlay.IsValid()) yield return CloseOverlayScene();
            ClosePause();
            var root = new GameObject("Defeat");
            SceneManager.MoveGameObjectToScene(root, primary);
            defeat = root.AddComponent<ClientScreen>();
            defeat.Initialize(ui, 70, false);
            defeat.Scale(preferences.Display.UiPercent);
            defeat.BuildResult(game.Victorious, () => Navigate("ServerBrowser"));
            defeat.Focus();
            busy = false;
            RefreshInput();
        }

        private void ClosePause()
        {
            if (pause == null) return;
            pause.gameObject.SetActive(false);
            Destroy(pause.gameObject);
            pause = null;
            RefreshInput();
        }

        private void OpenSettings()
        {
            if (busy || overlay.IsValid()) return;
            returnToPause = pause != null;
            StartCoroutine(OpenOverlay("Settings"));
        }

        internal void OpenDashboard() => OpenDashboard(false);

        private void OpenDashboard(bool fromKey)
        {
            if (busy || overlay.IsValid() || pause != null || defeat != null ||
                game == null || game.State != ConnectionState.Connected) return;
            dashboardFromKey = fromKey;
            dashboardReleased = false;
            StartCoroutine(OpenOverlay("PlayerDashboard"));
        }

        private IEnumerator OpenOverlay(string name)
        {
            busy = true;
            ClosePause();
            if (screen != null) screen.SetInteractive(false);
            RefreshInput();
            yield return Load(name);
            overlay = Loaded(name);
            if (name == "PlayerDashboard" && (game == null || game.State != ConnectionState.Connected ||
                (dashboardFromKey && (dashboardReleased || !DashboardKeyHeld))))
            {
                yield return CloseOverlayScene();
                busy = false;
                RefreshInput();
                yield break;
            }
            GameObject root = overlay.GetRootGameObjects()[0];
            if (name == "Settings")
            {
                settings = root.AddComponent<ClientSettingsScreen>();
                settings.Build(ui, preferences, controls, ApplyPreferences, CloseOverlay, PreviewAudio);
            }
            else
            {
                dashboardTick = -1;
                dashboard = root.AddComponent<ClientScreen>();
                dashboard.Initialize(ui, 60, false);
                dashboard.Scale(preferences.Display.UiPercent);
                if (dashboardFromKey)
                    dashboard.BuildDashboard(null, null, controls.Label(ControlBindings.Dashboard));
                else
                    dashboard.BuildDashboard(CloseOverlay, () => dashboard.ShowMatchups(rules, document, elementNames), null);
                dashboard.Focus();
            }
            busy = false;
            RefreshInput();
        }

        private void CloseOverlay()
        {
            if (!busy && overlay.IsValid()) StartCoroutine(ReturnFromOverlay());
        }

        private IEnumerator ReturnFromOverlay()
        {
            busy = true;
            yield return CloseOverlayScene();
            if (screen != null)
            {
                screen.SetInteractive(true);
                screen.Focus();
            }
            busy = false;
            if (returnToPause) ShowMenu();
            returnToPause = false;
            RefreshInput();
        }

        private IEnumerator CloseOverlayScene()
        {
            settings = null;
            if (dashboard != null) dashboard.gameObject.SetActive(false);
            dashboard = null;
            dashboardFromKey = dashboardReleased = false;
            yield return SceneManager.UnloadSceneAsync(overlay);
            overlay = default;
        }

        private void ApplyPreferences()
        {
            if (ui.ReduceMotion != preferences.Display.ReduceMotion)
            {
                ui.ReduceMotion = preferences.Display.ReduceMotion;
                if (screen != null) ui.ApplyMotion(screen.transform);
                if (game != null) ui.ApplyMotion(game.transform);
                if (pause != null) ui.ApplyMotion(pause.transform);
                if (dashboard != null) ui.ApplyMotion(dashboard.transform);
                if (settings != null) ui.ApplyMotion(settings.transform);
            }
            game?.ApplyPreferences(preferences, controls);
            screen?.Scale(preferences.Display.UiPercent);
            pause?.Scale(preferences.Display.UiPercent);
            dashboard?.Scale(preferences.Display.UiPercent);
            settings?.Scale(preferences.Display.UiPercent);
        }

        private void PreviewAudio(int channel)
        {
            int bus = channel == 0 ? UiVolumeChannel : channel;
            if (previewClip != null && !preferences.Muted[0] && !preferences.Muted[bus])
                preview.PlayOneShot(previewClip, preferences.Volume[0] * preferences.Volume[bus]);
        }

        private void RefreshInput() => game?.SetInputBlocked(busy || overlay.IsValid() || pause != null || defeat != null);

        private void SetMenuEnvironment(bool enabled)
        {
            if (menuListener != null) menuListener.enabled = enabled;
            if (menuCamera != null) menuCamera.enabled = enabled;
        }

        private void Update()
        {
            hostMigration?.Poll();
            if (controls == null) return;
            // 씬을 읽는 동안 키를 다시 눌러도 이전 열기 요청은 취소 상태로 둔다.
            if (dashboardFromKey && !DashboardKeyHeld) dashboardReleased = true;
            if (!busy && game != null && game.State == ConnectionState.Disconnected)
            {
                if (game.Defeated || game.Victorious)
                {
                    if (defeat == null) StartCoroutine(OpenDefeat());
                    return;
                }
                if (!standalone)
                {
                    StartCoroutine(OpenMain("ServerBrowser", game.ConnectionDetail));
                    return;
                }
                if (dashboard != null || settings != null)
                {
                    CloseOverlay();
                    return;
                }
                ClosePause();
            }
            if (!busy && dashboardReleased)
            {
                CloseOverlay();
                return;
            }
            GameplayFrame frame = game?.CurrentGameplay;
            if (dashboard != null && frame != null && frame.Tick != dashboardTick)
            {
                dashboardTick = frame.Tick;
                dashboard.Observe(frame, rules, elementNames);
            }
            if (game?.HandleChatInput(Application.isFocused && !busy && !overlay.IsValid() && pause == null && defeat == null) == true) return;
            if (!Application.isFocused) return;
            if (!busy && game != null && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame &&
                game.CancelConstruction()) return;
            if (DashboardKeyHeld && controls.Pressed(ControlBindings.Dashboard)) OpenDashboard(true);
            if (!controls.Pressed(ControlBindings.Menu)) return;
            if (joining) { ServerBack(); return; }
            if (busy || settings != null && settings.Capturing) return;
            if (dashboard?.CloseMatchups() == true || screen?.CloseMatchups() == true) return;
            if (settings != null) settings.Back();
            else if (overlay.IsValid()) CloseOverlay();
            else if (pause != null && pause.Confirming) pause.CloseConfirmation();
            else if (screen != null && screen.Confirming) screen.CloseConfirmation();
            else if (game != null) ShowMenu();
            else if (primary.name == "ServerBrowser" && !screen.IsTyping) ServerBack();
        }

        private void Quit()
        {
            if (quitting) return;
            quitting = true;
            busy = true;
            game?.SetInputBlocked(true);
            StartCoroutine(QuitAfterHandoff());
        }

        private void OnDestroy()
        {
            DisposeHostMigration();
            roomOperation?.Cancel();
            pendingConnection?.Dispose();
            pendingRoomTarget?.Dispose();
            SceneManager.sceneLoaded -= MuteGameEventSystem;
            foreach (InputActionReference reference in uiActions) Destroy(reference);
            if (ui == null || ui.Font == null) return;
            foreach (Texture2D atlas in ui.Font.atlasTextures) if (atlas != null) Destroy(atlas);
            if (ui.Font.material != null) Destroy(ui.Font.material);
            Destroy(ui.Font);
        }
    }
}
