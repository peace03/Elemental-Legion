using System;
using System.Collections;
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
        private ulong sequence;
        private double accumulator, serverReceipt, latestServerTick;
        private ConnectionState displayedState;
        private string displayedDetail = string.Empty;
        private bool ready;
        private bool previousBackground;
        private Coroutine resolving;
        private ClientFlow flow;
        private FieldChat chat;
        private bool managedPanel, inputBlocked, chatBlocking, defeated, victorious;
        private int rallyingCount, waitingGarrisonCount;
        private long garrisonArrivals;
        private ulong pendingRally;
        private ulong pendingConstruction;
        private double constructionTimeout;
        private double rallyTimeout, rallyNoticeUntil;
        private string rallyNotice;
        internal bool Ready => ready;
        internal ConnectionState State => displayedState;
        internal string ConnectionDetail => displayedDetail;
        internal GameplayFrame CurrentGameplay => connection?.Gameplay.State;
        internal bool Defeated => defeated;
        internal bool Victorious => victorious;
        private bool GameplayInputBlocked => inputBlocked || chat?.Composing == true;

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
                hud.Configure(document,rules);hud.TrainRequested+=Train;hud.RallyRequested+=Rally;
                hud.TrainingCancelRequested += CancelTraining;
                hud.BuildRequested += Build;
                hud.UpgradeRequested += Upgrade;
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
            chat = new FieldChat(transform, ui, settings, chatVisibleSeconds, chatBackgroundAlpha);
            if (connection != null) chat.Attach(connection);
            ui.ApplyMotion(hud.transform);
            hud.MenuRequested += flow.ShowMenu;
            hud.DashboardRequested += flow.OpenDashboard;
            panel.gameObject.SetActive(!managedPanel && displayedState != ConnectionState.Connected);
        }

        internal void ApplyPreferences(ClientPreferences preferences, ControlBindings controls)
        {
            input.Apply(controls);
            preferences.ApplyAudio(audioPool);
            hud.ApplyPreferences(controls, preferences.Display.UiPercent);
            chat?.Scale(preferences.Display.UiPercent);
        }

        internal void SetInputBlocked(bool value)
        {
            inputBlocked = value;
            hud.BlockInput(GameplayInputBlocked);
        }

        internal void EndSession() => Disconnect();
        internal bool CancelConstruction() => hud.CancelConstruction();
        internal bool HandleChatInput(bool allowed)
        {
            if (chat == null) return false;
            bool consumed = chat.HandleInput(allowed);
            if (chatBlocking != chat.Composing)
            {
                chatBlocking = chat.Composing;
                hud.BlockInput(GameplayInputBlocked);
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
            int history = Math.Max(3, (int)Math.Ceiling(settings.InterpolationSeconds / (settings.UnitsEveryTicks * rules.DeltaTime)) + 3);
            timeline = new SnapshotTimeline(history);
            connection = transport;
            ConfigureWorld(transport.WorldRules);
            connection.WorldConfigured += ConfigureWorld;
            connection.PoseReceived += OnPose; connection.UnitsReceived += OnUnits;
            connection.FogChanged += OnFogChanged; connection.UnitsForgotten += OnForgotten;
            connection.GameplayReceived += OnGameplay; connection.EntitiesForgotten += OnEntitiesForgotten; connection.CommandResponse += OnCommandResponse;
            chat?.Attach(connection);
            sequence = 0; accumulator = 0; latestServerTick = -1;
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
            if (!ready || connection == null) return;
            connection.Poll();
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
            if (connection == null || connection.State != ConnectionState.Connected || !prediction.Ready) return;
            bool acceptsInput = Application.isFocused && !GameplayInputBlocked && !panel.BlocksMovement;
            var direction = input.Read(acceptsInput);
            if (acceptsInput && input.Controls.Pressed(ControlBindings.Rally)) Rally();
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
            accumulator += Time.unscaledDeltaTime;
            if (accumulator >= rules.DeltaTime)
            {
                accumulator = Math.Min(accumulator - rules.DeltaTime, rules.DeltaTime);
                ulong next = sequence + 1;
                if (!prediction.Push(next, direction)) { Disconnect(); Show(ConnectionState.Disconnected, "서버 응답이 지연되어 연결을 종료했습니다."); return; }
                sequence = next; connection.SendMove(sequence, direction);
            }
            var predicted = prediction.Sample(direction, accumulator);
            double renderTick = latestServerTick + (Time.realtimeSinceStartupAsDouble - serverReceipt - settings.InterpolationSeconds) / rules.DeltaTime;
            presentation.Render(timeline, connection.PlayerId, predicted, renderTick);
            legionCamera.Follow(predicted, prediction.ConfirmedPosition);
            chat?.DrawBubble(legionCamera.View, timeline, renderTick);
            hud.TickBuildingPointer(acceptsInput);
        }

        private void OnPose(LegionPose pose)
        {
            prediction.Reconcile(pose); ObserveClock(pose.Tick);
            if (hud.Map != null) hud.Map.ObservePose(pose.Position);
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
            if (hud.Map != null && state.Ready) hud.Map.ObserveFog(state);
        }

        private void RefreshMapUnits()
        {
            if (hud.Map != null) hud.Map.ObserveUnits(timeline, connection.PlayerId);
        }

        private void Train(int element)
        {if(GameplayInputBlocked||connection==null||connection.State!=ConnectionState.Connected)return;connection.SendTrain(++sequence,element);audioPool.Click();}

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

        private void Rally()
        {
            if (GameplayInputBlocked || connection == null || connection.State != ConnectionState.Connected || !CanRally) return;
            pendingRally = ++sequence;
            rallyTimeout = Time.unscaledTimeAsDouble + 3;
            rallyNoticeUntil = 0;
            connection.SendRally(pendingRally);
            audioPool.Click();
            PresentRally();
        }

        private void OnCommandResponse(ulong command, CommandAdmission admission)
        {
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

        private bool CanRally => pendingRally == 0 && (waitingGarrisonCount > 0 || rallyingCount > 0);

        private void PresentRally()
        {
            string notice = pendingRally != 0 ? "집결 명령 확인 중…" :
                Time.unscaledTimeAsDouble < rallyNoticeUntil ? rallyNotice : null;
            hud.ShowRally(rallyingCount, waitingGarrisonCount, notice, CanRally);
        }
        private void OnEntitiesForgotten(long tick,ObservedEntityId[] entities,int count)
        {for(int index=0;index<count;index++)visuals.Forget(entities[index]);prediction.SetBuildings(connection.Gameplay.State.Buildings);}
        private void OnGameplay(GameplayFrame frame)
        {
            hud.Apply(frame);ObserveClock(frame.Tick);
            long joined = Math.Max(0, frame.GarrisonArrivals - garrisonArrivals);
            bool completed = !frame.IsRallying && (rallyingCount > 0 || joined > 0);
            rallyingCount = frame.RallyingCount;
            waitingGarrisonCount = frame.WaitingGarrisonCount;
            garrisonArrivals = frame.GarrisonArrivals;
            if (completed) NotifyRally(joined > 0 ? "합류 완료" : "집결 종료");
            if (hud.Map != null) hud.Map.ObserveGameplay(frame);
            visuals.Observe(frame);if(frame.SpatialReady)prediction.SetBuildings(frame.Buildings);
            foreach(NetworkEvent entry in frame.Events)
            {
                SimEvent data=entry.Data;
                if(data.Kind==SimEventKind.CommandRejected)
                {
                    if (pendingConstruction != 0 && data.Sequence == pendingConstruction)
                    {
                        pendingConstruction = 0;
                        hud.ConstructionResult(CombatHud.FailureMessage((CommandFailure)data.Detail));
                    }
                    else hud.CommandFailed((CommandFailure)data.Detail);
                    continue;
                }
                if (data.Kind == SimEventKind.CommandApplied)
                {
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
            if (resolving != null)
            {
                StopCoroutine(resolving);
                resolving = null;
            }
            pendingRally = 0;
            pendingConstruction = 0;
            rallyingCount = waitingGarrisonCount = 0;
            garrisonArrivals = 0;
            rallyNoticeUntil = 0;
            if(hud!=null)hud.ResetHud();if(visuals!=null)visuals.Clear();
            chat?.Reset();
            if (connection == null) return;
            connection.FogChanged -= OnFogChanged; connection.UnitsForgotten -= OnForgotten;
            connection.WorldConfigured -= ConfigureWorld;
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
                hud.RallyRequested-=Rally;
                hud.BuildRequested -= Build;
                hud.UpgradeRequested -= Upgrade;
                if (flow != null)
                {
                    hud.MenuRequested -= flow.ShowMenu;
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
