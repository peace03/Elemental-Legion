using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ElementalLegion.Client.Camera;
using ElementalLegion.Client.Input;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Client.FogOfWar;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Client.Bootstrap
{
    /// <summary>클라이언트 세션·입력·표현의 수명을 한곳에서 연결한다. 서버 게임 판정은 실행하지 않는다.</summary>
    public sealed class ClientBootstrap : MonoBehaviour
    {
        [Header("필수 참조")]
        [SerializeField, Tooltip("shared 패키지의 gamedata.json 원본입니다.")] private TextAsset gameData;
        [SerializeField, Tooltip("기존 Input Actions를 사용하는 입력 컴포넌트입니다.")] private LegionInput input;
        [SerializeField, Tooltip("관측 유닛 표시 컴포넌트입니다.")] private LegionPresentation presentation;
        [SerializeField, Tooltip("내 군단을 따라갈 카메라입니다.")] private LegionCamera legionCamera;
        [SerializeField, Tooltip("접속 화면입니다.")] private ConnectionPanel panel;
        [SerializeField, Tooltip("전장 지형을 가리지 않도록 비활성화할 카메라 연무입니다.")] private FogPresentation fog;
        [SerializeField,Tooltip("전투·경제·양성·집결 HUD입니다.")] private CombatHud hud;
        [SerializeField,Tooltip("확정 전투 표현입니다.")] private ElementalVisuals visuals;
        [SerializeField,Tooltip("UI 효과음입니다.")] private CombatAudio audioPool;
        [SerializeField,Tooltip("단독 Play에서 관측소 메뉴에 사용할 글꼴입니다.")] private Font menuFont;
        [SerializeField,Tooltip("단독 Play의 음량 확인에 사용할 효과음입니다.")] private AudioClip menuPreview;
        [SerializeField, Min(1), Tooltip("메시지를 받은 뒤 채팅 기록을 표시할 시간(초)입니다.")] private float chatVisibleSeconds = 6;
        [SerializeField, Range(0, 1), Tooltip("전장 채팅 배경의 불투명도입니다.")] private float chatBackgroundAlpha = 0.5f;
        private GameRules rules;
        private NetworkSettings settings;
        private ClientConnection connection;
        private MovementPrediction prediction;
        private SnapshotTimeline timeline;
        private readonly BuildingMemory buildingMemory = new BuildingMemory();
        private SquadControlState squadControl = new SquadControlState();
        private readonly List<RaycastResult> pointerHits = new List<RaycastResult>(16);
        private PointerEventData pointerEvent;
        private EventSystem pointerSystem;
        private readonly LegionPose?[] squadPoses = new LegionPose?[4];
        private bool baseFocus;
        private ulong sequence;
        private double accumulator, serverReceipt, latestServerTick;
        private ConnectionState displayedState;
        private string displayedDetail = string.Empty;
        private bool ready;
        private bool previousBackground;
        private Coroutine resolving;
        private ClientFlow flow;
        private FieldChat chat;
        private bool managedPanel, inputBlocked, chatBlocking, defeated, victorious, migrationRebase;
        private System.Numerics.Vector2 migrationDirection;
        private int rallyingCount, waitingGarrisonCount;
        private long garrisonArrivals;
        private ulong pendingRally;
        private ulong pendingConstruction;
        private ulong recallCommand;
        private ulong pendingQuickTransfer;
        private byte quickTransferFrom, quickTransferTo;
        private long pendingJourneyId;
        private double constructionTimeout;
        private double rallyTimeout, rallyNoticeUntil;
        private string rallyNotice;
        internal bool Ready => ready;
        internal ConnectionState State => displayedState;
        internal string ConnectionDetail => displayedDetail;
        internal GameplayFrame CurrentGameplay => connection?.Gameplay.State;
        internal bool Defeated => defeated;
        internal bool Victorious => victorious;
        internal byte SelectedSquads => squadControl.SelectedMask;
        internal JourneyResultView FinalJourney { get; private set; }
        private bool GameplayInputBlocked => inputBlocked || chat?.Composing == true || connection?.IsMigrating == true;
        private bool AcceptsGameplayInput(bool focused) => focused && !GameplayInputBlocked;

        private void OnEnable()
        {
            try
            {
                if (gameData == null || input == null || presentation == null || legionCamera == null || panel == null || fog == null || hud==null || visuals==null || audioPool==null) throw new InvalidOperationException("ClientBootstrap 필수 참조가 없습니다.");
                var document = new GameDataDocument(gameData.text);
                previousBackground = Application.runInBackground; Application.runInBackground = true;
                rules = new GameRules(document); settings = new NetworkSettings(document);
                fog.enabled = false;
                var names = new string[rules.StartingElements.Count];
                for (int index = 0; index < names.Length; index++) names[index] = (string)document.Root["elements"][rules.StartingElements[index]]["displayName"];
                input.Initialize(); presentation.ValidateReferences(); presentation.ConfigureGround(rules.MapSize); panel.Configure(names);
                visuals.SetBuildingMemory(buildingMemory);
                hud.Configure(document,rules);hud.TrainRequested+=Train;hud.RallyRequested+=Rally;
                hud.TrainingCancelRequested += CancelTraining;
                hud.SquadSelected += SelectSquad;
                hud.TransferRequested += Transfer;
                hud.RecallRequested += Recall;
                hud.BuildRequested += Build;
                hud.UpgradeRequested += Upgrade;
                hud.MapFocusRequested += InspectMap;
                hud.MapMovePointerChanged += OnMapMovePointer;
                hud.CameraCenterRequested += CenterCamera;
                hud.BaseFocusRequested += FocusBase;
                panel.ConnectRequested += Connect; panel.DisconnectRequested += Disconnect;
                ready = true;
            }
            catch (Exception exception) { Debug.LogException(exception, this); enabled = false; }
        }

        private void Start()
        {
            if (ready && flow == null && !SceneManager.GetSceneByName("Bootstrap").isLoaded && menuFont != null)
                ClientFlow.AttachStandalone(this, gameData, menuFont, menuPreview, input.Controls);
        }

        internal void AttachFlow(ClientFlow navigation, ObservatoryUi ui, bool hideConnectionPanel)
        {
            flow = navigation;
            managedPanel = hideConnectionPanel;
            hud.BuildObservatory(ui, connection?.WorldRules ?? rules, visuals);
            hud.Map?.SetBuildingMemory(buildingMemory);
            chat = new FieldChat(transform, ui, settings, chatVisibleSeconds, chatBackgroundAlpha);
            if (connection != null) chat.Attach(connection);
            ui.ApplyMotion(hud.transform);
            hud.DashboardRequested += flow.OpenDashboard;
            panel.gameObject.SetActive(!managedPanel && displayedState != ConnectionState.Connected);
        }

        internal void ApplyPreferences(ClientPreferences preferences, ControlBindings controls)
        {
            input.Apply(controls);
            preferences.ApplyAudio(audioPool);
            hud.ApplyPreferences(controls, preferences.Display.UiPercent);
            hud.SetGuidanceEnabled(preferences.Guidance);
            chat?.Scale(preferences.Display.UiPercent);
        }

        internal void SetInputBlocked(bool value)
        {
            inputBlocked = value;
            if (value) CancelInputGestures();
            hud.BlockInput(!Application.isFocused || GameplayInputBlocked);
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!ready) return;
            if (!focused) CancelInputGestures();
            hud.BlockInput(!focused || GameplayInputBlocked);
        }

        private void CancelInputGestures()
        {
            squadControl.CancelPoint();
            input?.ResetSquadShortcut();
            hud?.HideMoveDestination();
            hud?.SetQuickTransferSource(0);
        }

        internal void EndSession() => Disconnect();
        /// <summary>현재 본 여정의 확인 의도만 보관하고, 승계가 끝난 동일 여정에서 한 번 전송한다.</summary>
        internal void RequestJourneyAcknowledgment()
        {
            if (connection?.State != ConnectionState.Connected || connection.Gameplay.State.Journey.Id <= 0) return;
            pendingJourneyId = connection.Gameplay.State.Journey.Id;
            FlushJourneyAcknowledgment();
        }

        private void FlushJourneyAcknowledgment()
        {
            if (pendingJourneyId == 0 || connection?.State != ConnectionState.Connected || connection.IsMigrating) return;
            long current = connection.Gameplay.State.Journey.Id;
            if (current <= 0) return;
            long requested = pendingJourneyId;
            pendingJourneyId = 0;
            if (current == requested) connection.SendAcknowledgeJourney(++sequence);
        }
        internal bool CancelConstruction()
        {
            if (!hud.CancelConstruction()) return false;
            squadControl.CancelPoint();
            hud.HideMoveDestination();
            return true;
        }
        internal bool CloseHudPanel()
        {
            if (!hud.ClosePanel()) return false;
            CancelInputGestures();
            return true;
        }

        internal bool HandleChatInput(bool allowed)
        {
            if (chat == null) return false;
            bool consumed = chat.HandleInput(allowed && connection?.IsMigrating != true);
            if (chatBlocking != chat.Composing)
            {
                chatBlocking = chat.Composing;
                if (chatBlocking) CancelInputGestures();
                hud.BlockInput(!Application.isFocused || GameplayInputBlocked);
            }
            return consumed;
        }

        internal void AdoptConnection(ClientConnection transport)
        {
            SetupTransport(transport);
            transport.DeferGameplay = false;
            Show(ConnectionState.Connected, string.Empty);
        }

        private void Connect(string host, int selected) => Connect(host, selected, ClientPreferences.LoadNickname());

        private void Connect(string host, int selected, string nickname)
        {
            Disconnect();
            FinalJourney = default;
            if (selected < 0 || selected >= rules.StartingElements.Count)
            {
                Show(ConnectionState.Disconnected, "시작 원소 선택이 잘못됐습니다.");
                return;
            }
            if (IPAddress.TryParse(host, out IPAddress address))
            {
                ConnectAddress(address.ToString(), selected, nickname);
                return;
            }
            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                Show(ConnectionState.Disconnected, "올바른 IP 또는 서버 이름을 입력하세요.");
                return;
            }
            Show(ConnectionState.Connecting, "서버 주소 확인 중…");
            resolving = StartCoroutine(ResolveAddress(host, selected, nickname));
        }

        private IEnumerator ResolveAddress(string host, int selected, string nickname)
        {
            Task<IPAddress[]> lookup;
            try { lookup = Dns.GetHostAddressesAsync(host); }
            catch (Exception exception) when (exception is ArgumentException || exception is SocketException)
            {
                resolving = null;
                Show(ConnectionState.Disconnected, "서버 주소를 확인하지 못했습니다.");
                yield break;
            }
            // 취소 이후 완료된 DNS 작업의 예외도 관찰하되 연결에는 반영하지 않는다.
            _ = lookup.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            double until = Time.unscaledTimeAsDouble + 8;
            while (!lookup.IsCompleted && Time.unscaledTimeAsDouble < until) yield return null;
            resolving = null;
            if (lookup.Status == TaskStatus.RanToCompletion)
            {
                IPAddress[] candidates = lookup.Result;
                IPAddress found = Array.Find(candidates, candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                    ?? Array.Find(candidates, candidate => candidate.AddressFamily == AddressFamily.InterNetworkV6);
                if (found != null)
                {
                    ConnectAddress(found.ToString(), selected, nickname);
                    yield break;
                }
            }
            Show(ConnectionState.Disconnected, "서버 주소를 확인하지 못했습니다.");
        }

        private void ConnectAddress(string host, int selected, string nickname)
        {
            try
            {
                SetupTransport(new ClientConnection(rules, settings));
                connection.Connect(host, rules.StartingElements[selected], nickname);
                Show(ConnectionState.Connecting, string.Empty);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is System.Net.Sockets.SocketException)
            { Disconnect(); Show(ConnectionState.Disconnected, "접속 실패: " + exception.Message); }
        }

        private void SetupTransport(ClientConnection transport)
        {
            FinalJourney = default;
            pendingJourneyId = 0;
            buildingMemory.Clear();
            timeline = CreateTimeline();
            connection = transport;
            ConfigureWorld(transport.WorldRules);
            connection.WorldConfigured += ConfigureWorld;
            connection.MigrationChanged += OnMigration;
            connection.PoseReceived += OnPose; connection.UnitsReceived += OnUnits;
            connection.FogChanged += OnFogChanged; connection.UnitsForgotten += OnForgotten;
            connection.GameplayReceived += OnGameplay; connection.EntitiesForgotten += OnEntitiesForgotten; connection.CommandResponse += OnCommandResponse;
            chat?.Attach(connection);
            sequence = 0; accumulator = 0; latestServerTick = -1;
            squadControl = new SquadControlState(); baseFocus = false; Array.Clear(squadPoses, 0, squadPoses.Length);
            CancelInputGestures();
            ResetQuickTransferFeedback();
            hud.SelectSquad(squadControl.SelectedMask);
            visuals.SetSelectedSquads(squadControl.SelectedMask);
            input.Initialize(); legionCamera.ResetFollow();
        }

        private void ConfigureWorld(GameRules worldRules)
        {
            prediction = new MovementPrediction(worldRules);
            presentation.ConfigureGround(worldRules.MapSize);
            legionCamera.ConfigureVision(worldRules);
            hud.ConfigureWorld(worldRules);
        }

        private void Update()
        {
            if (!ready || connection == null) { squadControl.CancelPoint(); return; }
            connection.Poll();
            FlushJourneyAcknowledgment();
            hud.ShowMigrationNotice(connection.AwaitingHostLeave || connection.IsOnline && connection.LastPoseAgeSeconds >= settings.MigrationNoticeSeconds,
                connection.AwaitingHostLeave);
            if (connection.State != displayedState)
            {
                defeated = connection.Defeated;
                victorious = connection.Victorious;
                string detail = connection.State == ConnectionState.Disconnected
                    ? defeated ? "본진이 파괴되었습니다." : victorious ? "전투에서 승리했습니다." : FailureText(connection.JoinFailure, connection.Error)
                    : string.Empty;
                Show(connection.State, detail);
                if (connection.State == ConnectionState.Disconnected) { DisconnectTransport(); presentation.Clear(); return; }
            }
            if (connection == null || connection.State != ConnectionState.Connected || !prediction.Ready)
            { CancelInputGestures(); return; }
            bool acceptsInput = AcceptsGameplayInput(Application.isFocused);
            SquadShortcutResult shortcut = input.ReadSquadShortcut(acceptsInput && !hud.EditingQuantity);
            hud.SetQuickTransferSource(input.PendingFromSquad);
            var direction = input.Read(acceptsInput);
            migrationDirection = direction;
            if (connection.IsMigrating)
            {
                squadControl.CancelPoint();
                var frozenCenter = prediction.SampleDisplay(System.Numerics.Vector2.Zero, 0, 0);
                // 새 기준 상태가 준비될 때까지 마지막으로 관측한 전장을 계속 그린다.
                if (!connection.AwaitingHostLeave &&
                    connection.MigrationStatus is MigrationPhase.Recovering or MigrationPhase.Resyncing)
                    presentation.Render(timeline, connection.PlayerId, frozenCenter, timeline.LatestTick, squadControl.Representative);
                FollowCamera(frozenCenter);
                return;
            }
            if (acceptsInput)
            {
                ApplySquadShortcut(shortcut);
                if (input.Controls.Pressed(ControlBindings.BaseFocus)) FocusBase();
                if (input.Controls.Pressed(ControlBindings.Rally)) Rally();
                if (input.Controls.Pressed(ControlBindings.Recall)) Recall();
            }
            HandleWorldRightClick(acceptsInput, direction.LengthSquared() > 0);
            if (pendingRally != 0 && Time.unscaledTimeAsDouble >= rallyTimeout)
            {
                pendingRally = 0;
                NotifyRally("집결 응답을 확인하지 못했습니다.");
            }
            if (pendingConstruction != 0 && Time.unscaledTimeAsDouble >= constructionTimeout)
            {
                pendingConstruction = 0;
                hud.ConstructionResult("건설 응답을 확인하지 못했습니다.");
            }
            if (rallyNoticeUntil > 0 && Time.unscaledTimeAsDouble >= rallyNoticeUntil)
            {
                rallyNoticeUntil = 0;
                PresentRally();
            }
            if (!TickMovement(direction, Time.unscaledDeltaTime)) return;
            double renderTick = timeline.SampleRenderTick(latestServerTick, serverReceipt, Time.realtimeSinceStartupAsDouble,
                settings.InterpolationSeconds, rules.DeltaTime);
            System.Numerics.Vector2? observedCenter = timeline.LatestTick >= 0
                ? timeline.SampleSquadAnchor(squadControl.Representative, renderTick) : null;
            var predicted = prediction.SampleDisplay(direction, accumulator, Time.unscaledDeltaTime, observedCenter);
            presentation.Render(timeline, connection.PlayerId, predicted, renderTick, squadControl.Representative);
            // 카메라 추종에는 유닛 스냅샷의 재생 지연을 추가하지 않는다.
            FollowCamera(prediction.FollowsObservation ? prediction.ConfirmedPosition : predicted);
            if (acceptsInput) ApplyWheelZoom();
            chat?.DrawBubble(legionCamera.View, timeline, renderTick);
            hud.TickBuildingPointer(acceptsInput);
        }

        private bool TickMovement(System.Numerics.Vector2 direction, double seconds)
        {
            if (!squadControl.PointHeld) hud.HideMoveDestination();
            accumulator += seconds;
            if (accumulator >= rules.DeltaTime)
            {
                accumulator = Math.Min(accumulator - rules.DeltaTime, rules.DeltaTime);
                if (direction.LengthSquared() == 0 && squadControl.TryTakePoint(out byte pointMask, out var destination, out bool pressed))
                {
                    connection.SendMoveTo(++sequence, pointMask, destination);
                    visuals.ShowMoveDestination(destination, pressed);
                    if (squadControl.PointHeld) hud.ShowMoveDestination(pointMask, destination);
                }
                else if (squadControl.NextDirect(direction.LengthSquared() > 0, out byte mask))
                {
                    ulong next = ++sequence;
                    if ((mask & (1 << (squadControl.Representative - 1))) != 0 && !prediction.Push(next, direction))
                    { Disconnect(); Show(ConnectionState.Disconnected, "서버 응답이 지연되어 연결을 종료했습니다."); return false; }
                    connection.SendMove(next, mask, direction);
                }
            }
            return true;
        }

        private void SelectSquad(byte mask)
        {
            if (GameplayInputBlocked || connection?.State != ConnectionState.Connected) return;
            byte stop = squadControl.Select(mask);
            hud.HideMoveDestination();
            if (stop != 0) connection.SendMove(++sequence, stop, System.Numerics.Vector2.Zero);
            LegionPose? pose = squadPoses[squadControl.Representative - 1];
            if (pose.HasValue) prediction.Reset(pose.Value);
            else
            {
                prediction = new MovementPrediction(connection.WorldRules);
                prediction.SetBuildings(connection.Gameplay.State.Buildings);
            }
            accumulator = 0; baseFocus = false;
            connection.SendFocus(++sequence, squadControl.Representative);
            hud.SelectSquad(mask);
            visuals.SetSelectedSquads(mask);
            if (squadControl.Representative == 4)
            {
                var position = connection.Gameplay.State.BasePosition;
                legionCamera.SnapFollow(position, position);
            }
            else if (pose.HasValue) legionCamera.SnapFollow(prediction.Position, prediction.ConfirmedPosition);
        }

        private void FocusBase()
        {
            if (GameplayInputBlocked || connection?.State != ConnectionState.Connected) return;
            connection.SendFocus(++sequence, 4);
            baseFocus = true;
            var position = connection.Gameplay.State.BasePosition;
            legionCamera.SnapFollow(position, position);
        }

        private void FollowCamera(System.Numerics.Vector2 predicted)
        {
            if (baseFocus || squadControl.Representative == 4)
            {
                var position = connection.Gameplay.State.BasePosition;
                legionCamera.Follow(position, position);
            }
            else legionCamera.Follow(predicted, prediction.ConfirmedPosition);
        }

        private void Transfer(byte target, int count)
        {
            if (GameplayInputBlocked || connection?.State != ConnectionState.Connected || squadControl.SelectedMask == 7) return;
            connection.SendTransfer(++sequence, squadControl.Representative, target, count);
            audioPool.Click();
        }

        private void ApplySquadShortcut(SquadShortcutResult shortcut)
        {
            if (shortcut.SelectionMask != 0)
            {
                SelectSquad(shortcut.SelectionMask);
                return;
            }
            if (shortcut.TargetSquad == 0 || GameplayInputBlocked || connection?.State != ConnectionState.Connected) return;
            quickTransferFrom = shortcut.FromSquad;
            quickTransferTo = shortcut.TargetSquad;
            pendingQuickTransfer = ++sequence;
            hud.SetQuickTransferFeedback(quickTransferFrom, quickTransferTo, $"{connection.WorldRules.QuickTransferCount}기 출격 요청");
            connection.SendQuickTransfer(pendingQuickTransfer, quickTransferFrom, quickTransferTo);
            audioPool.Click();
        }

        private void ResetQuickTransferFeedback()
        {
            pendingQuickTransfer = 0;
            quickTransferFrom = quickTransferTo = 0;
            hud?.SetQuickTransferFeedback(0, 0, null);
        }

        private void PresentQuickTransferResult(string message, bool complete = true)
        {
            if (complete) pendingQuickTransfer = 0;
            hud.SetQuickTransferFeedback(quickTransferFrom, quickTransferTo, message);
        }

        private void HandleWorldRightClick(bool acceptsInput, bool direct)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) { squadControl.CancelPoint(); return; }
            bool held = mouse.rightButton.isPressed;
            bool pressed = mouse.rightButton.wasPressedThisFrame;
            if (!held && !pressed)
            {
                squadControl.ObservePoint(false, false, acceptsInput, direct, null);
                return;
            }
            Vector2 cursor = mouse.position.ReadValue();
            bool overMap = PointerTargetsMap(cursor, out bool overUi);
            if (overMap)
            {
                // 지도 목적지는 UI 이벤트에서만 받는다. 같은 클릭을 월드 입력에서 다시 예약하지 않는다.
                if (!acceptsInput || direct || !pressed && !hud.Map.RightHeld) squadControl.CancelPoint();
                return;
            }
            if (hud.Map?.RightHeld == true) { squadControl.CancelPoint(); return; }
            bool cancelled = pressed && acceptsInput && hud.CancelConstruction();
            bool allowed = SquadControlState.ShouldMoveTo(acceptsInput, overUi, cancelled);
            System.Numerics.Vector2? destination = null;
            if (allowed && !direct && (held || pressed))
            {
                if (legionCamera.View.pixelRect.Contains(cursor))
                {
                    Ray ray = legionCamera.View.ScreenPointToRay(cursor);
                    if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance))
                    {
                        Vector3 point = ray.GetPoint(distance);
                        float mapSize = connection.WorldRules.MapSize;
                        if (point.x >= 0 && point.x <= mapSize && point.z >= 0 && point.z <= mapSize)
                            destination = new System.Numerics.Vector2(point.x, point.z);
                    }
                }
            }
            squadControl.ObservePoint(held, pressed, allowed, direct, destination);
        }

        private bool PointerTargetsMap(Vector2 cursor, out bool overUi)
        {
            overUi = false;
            EventSystem system = EventSystem.current;
            if (system == null) return false;
            if (pointerSystem != system)
            {
                pointerSystem = system;
                pointerEvent = new PointerEventData(system);
            }
            pointerEvent.Reset();
            pointerEvent.position = cursor;
            // EventSystem의 Update 순서와 무관하게 현재 최상위 UI만 입력 대상으로 삼는다.
            pointerHits.Clear();
            system.RaycastAll(pointerEvent, pointerHits);
            overUi = pointerHits.Count > 0;
            return overUi && hud.Map != null && pointerHits[0].gameObject == hud.Map.gameObject;
        }

        private void OnMapMovePointer(System.Numerics.Vector2? position, bool held, bool pressed)
        {
            if (!position.HasValue || !ready || connection?.State != ConnectionState.Connected || prediction?.Ready != true)
            {
                squadControl.CancelPoint();
                hud.HideMoveDestination();
                return;
            }
            bool allowed = AcceptsGameplayInput(Application.isFocused);
            bool cancelled = pressed && allowed && hud.CancelConstruction();
            squadControl.ObservePoint(held, pressed, allowed && !cancelled,
                input.Read(allowed).LengthSquared() > 0, position);
            if (!squadControl.PointHeld) hud.HideMoveDestination();
        }

        private void ApplyWheelZoom()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;
            float scroll = mouse.scroll.ReadValue().y;
            if (scroll == 0 || !legionCamera.View.pixelRect.Contains(mouse.position.ReadValue())) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(mouse.deviceId)) return;
            legionCamera.ZoomByWheel(scroll);
        }

        private void InspectMap(System.Numerics.Vector2 position)
        {
            if (connection?.State == ConnectionState.Connected && !GameplayInputBlocked)
                legionCamera.Inspect(position);
        }

        private void CenterCamera()
        {
            if (!GameplayInputBlocked) SelectSquad(squadControl.SelectedMask);
        }

        private void OnMigration(HostMigrationNotice state)
        {
            if (state.Phase != MigrationPhase.None)
            {
                CancelInputGestures();
                ResetQuickTransferFeedback();
            }
            hud.BlockInput(!Application.isFocused || GameplayInputBlocked);
            if (connection.AwaitingHostLeave) return;
            if (state.Phase == MigrationPhase.Recovering || state.Phase == MigrationPhase.Resyncing)
            {
                pendingRally = pendingConstruction = 0;
                if (state.Phase == MigrationPhase.Resyncing)
                {
                    migrationRebase = true;
                    migrationDirection = input.Read(Application.isFocused && !inputBlocked && chat?.Composing != true);
                    if (squadControl.NextDirect(migrationDirection.LengthSquared() > 0, out byte mask))
                        connection.SendMove(++sequence, mask, migrationDirection);
                }
            }
            if (state.Phase != MigrationPhase.None || !migrationRebase) return;
            migrationRebase = false;
            hud.ResetCorePickupFeedback();
            buildingMemory.Clear();
            timeline = CreateTimeline();
            prediction = new MovementPrediction(connection.WorldRules);
            Array.Clear(squadPoses, 0, squadPoses.Length);
            latestServerTick = -1; accumulator = 0;
            garrisonArrivals = connection.Gameplay.State.GarrisonArrivals;
            rallyingCount = connection.Gameplay.State.RallyingCount;
            waitingGarrisonCount = connection.Gameplay.State.WaitingGarrisonCount;
            presentation.Clear();
            hud.Map?.ResetMap();
        }

        private SnapshotTimeline CreateTimeline()
        {
            int history = Math.Max(3, (int)Math.Ceiling(settings.InterpolationSeconds /
                (settings.UnitsEveryTicks * rules.DeltaTime)) + 3);
            // 포즈 패킷과 Gameplay 양쪽의 표본이 같은 유닛 이력 시간 안에 남도록 한다.
            double poseHistory = history + Math.Ceiling(history * (double)settings.UnitsEveryTicks / settings.LegionEveryTicks);
            return new SnapshotTimeline(history, (int)Math.Min(SnapshotTimeline.MaxPoseCapacity, poseHistory));
        }

        private void OnPose(LegionPose pose)
        {
            if (pose.PlayerId != connection.PlayerId || pose.SquadId < 1 || pose.SquadId > 4) return;
            LegionPose? previous = squadPoses[pose.SquadId - 1];
            if (previous.HasValue && previous.Value.Tick >= pose.Tick) return;
            squadPoses[pose.SquadId - 1] = pose;
            timeline.ObservePose(pose);
            if (pose.SquadId == squadControl.Representative)
            {
                if (!prediction.Ready) prediction.Reset(pose);
                else prediction.Reconcile(pose);
                if (hud.Map != null) hud.Map.ObservePose(pose.Position);
            }
            ObserveClock(pose.Tick);
        }

        private void OnUnits(UnitSnapshotAssembler snapshot)
        {
            timeline.Push(snapshot);
            ObserveClock(snapshot.Tick);
            RefreshMapUnits();
        }

        private void OnForgotten(long tick, int[] ids, int count)
        {
            for (int index = 0; index < count; index++) timeline.Forget(ids[index], tick);
            RefreshMapUnits();
        }

        private void OnFogChanged(FogStateReplica state)
        {
            if (!state.Ready) return;
            buildingMemory.ConfirmAbsent(state.ClearedBuildingMemories, state.Tick, state.Revision);
            hud.Map?.ObserveFog(state);
        }

        private void RefreshMapUnits()
        {
            if (hud.Map != null) hud.Map.ObserveUnits(timeline, connection.PlayerId);
        }

        private void Train(int element, int count, byte targetSquad)
        {if(GameplayInputBlocked||connection==null||connection.State!=ConnectionState.Connected)return;connection.SendTrain(++sequence,element,count,targetSquad);audioPool.Click();}

        private void CancelTraining(ulong orderId)
        {
            if (GameplayInputBlocked || connection == null || connection.State != ConnectionState.Connected) return;
            connection.SendCancelTraining(++sequence, orderId);
            audioPool.Click();
        }

        private void Build(BuildingKind kind, int x, int z, int element)
        {
            if (BeginConstructionCommand()) connection.SendBuild(pendingConstruction, kind, x, z, element);
        }

        private void Upgrade()
        {
            if (!BeginConstructionCommand()) return;
            hud.ConstructionResult("업그레이드 명령 확인 중…");
            connection.SendUpgradeBase(pendingConstruction);
        }

        private bool BeginConstructionCommand()
        {
            if (GameplayInputBlocked || connection == null || connection.State != ConnectionState.Connected) return false;
            if (pendingConstruction != 0)
            {
                hud.ConstructionResult("이전 명령의 결과를 기다리는 중입니다.");
                return false;
            }
            pendingConstruction = ++sequence;
            constructionTimeout = Time.unscaledTimeAsDouble + 3;
            audioPool.Click();
            return true;
        }

        private void Recall()
        {
            if (GameplayInputBlocked || connection?.State != ConnectionState.Connected ||
                squadControl.SelectedMask == 7 || squadControl.SelectedMask == 8 || !hud.TryGetRecall(out byte squad)) return;
            // 이전 틱에 남은 목적지가 새 귀환 요청보다 늦게 전송되지 않도록 한다.
            squadControl.DiscardPendingPoint();
            recallCommand = ++sequence;
            connection.SendRecall(recallCommand, squad);
            audioPool.Click();
        }

        private void Rally()
        {
            if (GameplayInputBlocked || connection == null || connection.State != ConnectionState.Connected || !CanRally) return;
            if (!hud.TryGetRally(out byte target, out int count)) return;
            pendingRally = ++sequence;
            rallyTimeout = Time.unscaledTimeAsDouble + 3;
            rallyNoticeUntil = 0;
            connection.SendRally(pendingRally, target, count);
            audioPool.Click();
            PresentRally();
        }

        private void OnCommandResponse(ulong command, CommandAdmission admission)
        {
            if (pendingQuickTransfer != 0 && command == pendingQuickTransfer)
            {
                if (admission == CommandAdmission.Accepted) PresentQuickTransferResult("접수됨 · 출격 확인 중", false);
                else PresentQuickTransferResult(admission == CommandAdmission.RateLimited
                    ? "입력이 너무 빠릅니다." : "출격 요청을 받지 못했습니다.");
                return;
            }
            if (pendingConstruction != 0 && pendingConstruction == command && admission != CommandAdmission.Accepted)
            {
                pendingConstruction = 0;
                hud.ConstructionResult($"명령 접수 실패  ·  {admission}");
                return;
            }
            if (pendingRally != 0 && command == pendingRally)
            {
                pendingRally = 0;
                if (admission == CommandAdmission.Accepted) PresentRally();
                else NotifyRally("집결 명령을 받지 못했습니다.");
            }
            if (admission != CommandAdmission.Accepted) hud.Admission(admission);
        }

        private void NotifyRally(string message)
        {
            rallyNotice = message;
            rallyNoticeUntil = Time.unscaledTimeAsDouble + 2.5;
            PresentRally();
        }

        private bool CanRally => pendingRally == 0 && waitingGarrisonCount > 0;

        private void PresentRally()
        {
            string notice = pendingRally != 0 ? "집결 명령 확인 중…" :
                Time.unscaledTimeAsDouble < rallyNoticeUntil ? rallyNotice : null;
            hud.ShowRally(rallyingCount, waitingGarrisonCount, notice, CanRally);
        }
        private void OnEntitiesForgotten(long tick,ObservedEntityId[] entities,int count)
        {
            for(int index=0;index<count;index++)
            {
                ObservedEntityId entity = entities[index];
                visuals.Forget(entity);
                if (entity.Kind == ObservedEntityKind.Candidate ||
                    entity.Kind == ObservedEntityKind.Building && buildingMemory.Forget(entity.Id, tick))
                    hud.Map?.SetVerticesDirty();
            }
            prediction.SetBuildings(connection.Gameplay.State.Buildings);
        }
        private void OnGameplay(GameplayFrame frame)
        {
            buildingMemory.Observe(frame);
            hud.Apply(frame);ObserveClock(frame.Tick);
            foreach (SquadView squad in frame.Squads)
                OnPose(new LegionPose(frame.Tick, connection.PlayerId, squad.AppliedSequence, squad.Position,
                    squad.Direction, squad.Id, squad.MovementRevision));
            long joined = Math.Max(0, frame.GarrisonArrivals - garrisonArrivals);
            bool completed = !frame.IsRallying && (rallyingCount > 0 || joined > 0);
            rallyingCount = frame.RallyingCount;
            waitingGarrisonCount = frame.WaitingGarrisonCount;
            garrisonArrivals = frame.GarrisonArrivals;
            if (completed) NotifyRally(joined > 0 ? "증원 합류 완료" : "증원 이동 종료");
            if (hud.Map != null) hud.Map.ObserveGameplay(frame);
            prediction.SetBasePosition(frame.BasePosition);
            visuals.Observe(frame);if(frame.SpatialReady)prediction.SetBuildings(frame.Buildings);
            foreach(NetworkEvent entry in frame.Events)
            {
                SimEvent data=entry.Data;
                if (data.Kind == SimEventKind.BaseUnderAttack)
                {
                    if (data.OwnerId == connection.PlayerId)
                    {
                        hud.ShowBaseUnderAttack(Time.unscaledTimeAsDouble);
                        audioPool.DefenseAlert();
                    }
                    continue;
                }
                if(data.Kind==SimEventKind.CommandRejected)
                {
                    if (pendingQuickTransfer != 0 && data.Sequence == pendingQuickTransfer)
                    {
                        var failure = (CommandFailure)data.Detail;
                        PresentQuickTransferResult(failure == CommandFailure.Busy ? "보낼 병력이 없습니다." :
                            failure == CommandFailure.PopulationCap ? "수비대 자리가 부족합니다." : CombatHud.FailureMessage(failure));
                    }
                    else if (pendingConstruction != 0 && data.Sequence == pendingConstruction)
                    {
                        pendingConstruction = 0;
                        hud.ConstructionResult(CombatHud.FailureMessage((CommandFailure)data.Detail));
                    }
                    else if (recallCommand != 0 && data.Sequence == recallCommand) hud.RecallFailed((CommandFailure)data.Detail);
                    else hud.CommandFailed((CommandFailure)data.Detail);
                    continue;
                }
                if (data.Kind == SimEventKind.CommandApplied)
                {
                    if (data.Detail == (int)CommandKind.QuickTransfer)
                    {
                        if (pendingQuickTransfer != 0 && data.Sequence == pendingQuickTransfer)
                            PresentQuickTransferResult($"{(int)data.Amount}기 출격");
                        continue;
                    }
                    if (data.Detail == (int)CommandKind.AcknowledgeJourney) continue;
                    if (data.Detail == (int)CommandKind.Recall) { recallCommand = data.Sequence; continue; }
                    if (data.Detail == (int)CommandKind.CancelTraining)
                    {
                        hud.TrainingCanceled();
                        continue;
                    }
                    if (data.Sequence == pendingConstruction) pendingConstruction = 0;
                    hud.ConstructionResult(data.Detail == (int)CommandKind.UpgradeBase ? "기지 업그레이드 완료" : "시설 건설 시작");
                    continue;
                }
                if(data.Kind==SimEventKind.Death&&data.EntityId>0&&data.EntityKind==ObservedEntityKind.Unit)timeline.ResolveDeath(data.EntityId,entry.Tick);
                if(data.Kind==SimEventKind.Converted&&data.EntityId>0&&data.OwnerId>0)timeline.ResolveConversion(data.EntityId,entry.Tick,data.OwnerId,data.Amount);
                // 구출의 전향 이력과 분대는 사건으로 추측하지 않고 최신 NetworkUnit 관측을 유지한다.
                if(frame.SpatialReady)visuals.Play(entry);
            }
            RefreshMapUnits();
            PresentRally();
        }

        private void ObserveClock(long tick)
        {
            if (tick < latestServerTick) return;
            latestServerTick = tick; serverReceipt = Time.realtimeSinceStartupAsDouble;
        }

        private void Disconnect()
        { defeated = victorious = false; DisconnectTransport(); if (presentation != null) presentation.Clear(); if (ready) Show(ConnectionState.Disconnected, string.Empty); }

        private void DisconnectTransport()
        {
            CancelInputGestures();
            ResetQuickTransferFeedback();
            buildingMemory.Clear();
            pendingJourneyId = 0;
            if (resolving != null)
            {
                StopCoroutine(resolving);
                resolving = null;
            }
            pendingRally = 0;
            pendingConstruction = 0;
            recallCommand = 0;
            rallyingCount = waitingGarrisonCount = 0;
            garrisonArrivals = 0;
            rallyNoticeUntil = 0;
            if(hud!=null)hud.ResetHud();if(visuals!=null)visuals.Clear();
            chat?.Reset();
            if (connection == null) return;
            if (connection.FinalJourney.Id > 0) FinalJourney = connection.FinalJourney;
            connection.FogChanged -= OnFogChanged; connection.UnitsForgotten -= OnForgotten;
            connection.WorldConfigured -= ConfigureWorld;
            connection.MigrationChanged -= OnMigration;
            connection.GameplayReceived-=OnGameplay;connection.EntitiesForgotten-=OnEntitiesForgotten;connection.CommandResponse-=OnCommandResponse;
            connection.PoseReceived -= OnPose; connection.UnitsReceived -= OnUnits; connection.Dispose(); connection = null;
        }

        private void Show(ConnectionState state, string detail)
        {
            if (displayedState == state && displayedDetail == detail && detail.Length > 0) return;
            displayedState = state;
            displayedDetail = detail;
            if (flow != null) panel.gameObject.SetActive(!managedPanel && !defeated && state != ConnectionState.Connected);
            if (panel.gameObject.activeSelf) panel.Show(state, detail);
        }

        private void OnDisable()
        {
            ready = false;
            if (panel != null) { panel.ConnectRequested -= Connect; panel.DisconnectRequested -= Disconnect; }
            if(hud!=null)
            {
                hud.TrainRequested-=Train;
                hud.TrainingCancelRequested -= CancelTraining;
                hud.SquadSelected -= SelectSquad;
                hud.TransferRequested -= Transfer;
                hud.RecallRequested -= Recall;
                hud.RallyRequested-=Rally;
                hud.BuildRequested -= Build;
                hud.UpgradeRequested -= Upgrade;
                hud.MapFocusRequested -= InspectMap;
                hud.MapMovePointerChanged -= OnMapMovePointer;
                hud.CameraCenterRequested -= CenterCamera;
                hud.BaseFocusRequested -= FocusBase;
                if (flow != null)
                {
                    hud.DashboardRequested -= flow.OpenDashboard;
                }
            }
            DisconnectTransport(); if (input != null) input.Release(); if (presentation != null) presentation.Clear();
            Application.runInBackground = previousBackground;
        }

        internal static string FailureText(JoinFailure failure, string error)
        {
            switch (failure)
            {
                case JoinFailure.DataMismatch: return "서버와 게임 데이터가 다릅니다.";
                case JoinFailure.VersionMismatch: return "서버와 통신 버전이 다릅니다.";
                case JoinFailure.InvalidElement: return "허용되지 않은 시작 원소입니다.";
                case JoinFailure.WorldFull: return "서버가 가득 찼거나 안전한 시작 위치가 없습니다.";
                case JoinFailure.InvalidNickname: return "서버가 닉네임을 거절했습니다. 타이틀에서 닉네임을 확인하세요.";
                case JoinFailure.InvalidResumeKey: return "서버가 저장된 복귀 키를 확인하지 못했습니다.";
                case JoinFailure.AlreadyConnected: return "이미 이 계정으로 접속 중입니다.";
                case JoinFailure.RoomStarted: return "이 방의 게임이 시작됐습니다. 복귀 가능한 참가자만 입장할 수 있습니다.";
                case JoinFailure.StorageUnavailable: return "서버에 접속 정보를 저장하지 못했습니다.";
                default: return string.IsNullOrEmpty(error) ? "연결이 종료됐습니다." : "연결 종료: " + error;
            }
        }
    }
}
