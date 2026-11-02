using System;
using ElementalLegion.Client.Camera;
using ElementalLegion.Client.Input;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Client.FogOfWar;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using UnityEngine;
using UnityEngine.InputSystem;
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
        [SerializeField, Tooltip("서버 시야를 카메라별 볼륨 연무로 표시합니다.")] private FogPresentation fog;
        [SerializeField,Tooltip("전투·경제·양성·집결 HUD입니다.")] private CombatHud hud;
        [SerializeField,Tooltip("확정 전투 표현입니다.")] private ElementalVisuals visuals;
        [SerializeField,Tooltip("UI 효과음입니다.")] private CombatAudio audioPool;
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

        private void OnEnable()
        {
            try
            {
                if (gameData == null || input == null || presentation == null || legionCamera == null || panel == null || fog == null || hud==null || visuals==null || audioPool==null) throw new InvalidOperationException("ClientBootstrap 필수 참조가 없습니다.");
                var document = new GameDataDocument(gameData.text);
                previousBackground = Application.runInBackground; Application.runInBackground = true;
                rules = new GameRules(document); settings = new NetworkSettings(document);
                fog.Initialize(rules);
                var names = new string[rules.StartingElements.Count];
                for (int index = 0; index < names.Length; index++) names[index] = (string)document.Root["elements"][rules.StartingElements[index]]["displayName"];
                input.Initialize(); presentation.ValidateReferences(); panel.Configure(names);
                hud.Configure(document,rules);hud.TrainRequested+=Train;hud.RallyRequested+=Rally;
                panel.ConnectRequested += Connect; panel.DisconnectRequested += Disconnect;
                ready = true;
            }
            catch (Exception exception) { Debug.LogException(exception, this); enabled = false; }
        }

        private void Connect(string host, int selected)
        {
            Disconnect();
            try
            {
                if (selected < 0 || selected >= rules.StartingElements.Count) throw new ArgumentException("시작 원소 선택이 잘못됐습니다.");
                prediction = new MovementPrediction(rules);
                int history = Math.Max(3, (int)Math.Ceiling(settings.InterpolationSeconds / (settings.UnitsEveryTicks * rules.DeltaTime)) + 3);
                timeline = new SnapshotTimeline(history);
                connection = new ClientConnection(rules, settings);
                connection.PoseReceived += OnPose; connection.UnitsReceived += OnUnits;
                connection.FogChanged += fog.Apply; connection.UnitsForgotten += OnForgotten;
                connection.GameplayReceived+=OnGameplay;connection.EntitiesForgotten+=OnEntitiesForgotten;connection.CommandResponse+=OnCommandResponse;
                sequence = 0; accumulator = 0; latestServerTick = -1;
                input.Initialize(); legionCamera.ResetFollow();
                connection.Connect(host, rules.StartingElements[selected]);
                Show(ConnectionState.Connecting, string.Empty);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is System.Net.Sockets.SocketException)
            { Disconnect(); Show(ConnectionState.Disconnected, "접속 실패: " + exception.Message); }
        }

        private void Update()
        {
            if (!ready || connection == null) return;
            connection.Poll();
            if (connection.State != displayedState)
            {
                string detail = connection.State == ConnectionState.Disconnected ? FailureText(connection.JoinFailure, connection.Error) : string.Empty;
                Show(connection.State, detail);
                if (connection.State == ConnectionState.Disconnected) { DisconnectTransport(); presentation.Clear(); return; }
            }
            if (connection == null || connection.State != ConnectionState.Connected || !prediction.Ready) return;
            var direction = input.Read(Application.isFocused && !panel.BlocksMovement);
            if(Application.isFocused&&!panel.BlocksMovement&&Keyboard.current!=null&&Keyboard.current.gKey.wasPressedThisFrame)Rally();
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
            presentation.Render(timeline, connection.PlayerId, predicted, renderTick); legionCamera.Follow(predicted);
        }

        private void OnPose(LegionPose pose)
        {
            prediction.Reconcile(pose); ObserveClock(pose.Tick);
        }

        private void OnUnits(UnitSnapshotAssembler snapshot)
        { timeline.Push(snapshot); ObserveClock(snapshot.Tick); }

        private void OnForgotten(long tick, int[] ids, int count)
        { for (int index = 0; index < count; index++) timeline.Forget(ids[index], tick); }

        private void Train(int element)
        {if(connection==null||connection.State!=ConnectionState.Connected)return;connection.SendTrain(++sequence,element);audioPool.Click();}
        private void Rally()
        {if(connection==null||connection.State!=ConnectionState.Connected)return;connection.SendRally(++sequence);audioPool.Click();}
        private void OnCommandResponse(ulong command,CommandAdmission admission){if(admission!=CommandAdmission.Accepted)hud.Admission(admission);}
        private void OnEntitiesForgotten(long tick,ObservedEntityId[] entities,int count)
        {for(int index=0;index<count;index++)visuals.Forget(entities[index]);prediction.SetBuildings(connection.Gameplay.State.Buildings);}
        private void OnGameplay(GameplayFrame frame)
        {
            hud.Apply(frame);ObserveClock(frame.Tick);
            visuals.Observe(frame);if(frame.SpatialReady)prediction.SetBuildings(frame.Buildings);
            foreach(NetworkEvent entry in frame.Events)
            {
                SimEvent data=entry.Data;
                if(data.Kind==SimEventKind.CommandRejected){hud.CommandFailed((CommandFailure)data.Detail);continue;}
                if(data.Kind==SimEventKind.Death&&data.EntityId>0)timeline.ResolveDeath(data.EntityId,entry.Tick);
                if(data.Kind==SimEventKind.Converted&&data.EntityId>0&&data.OwnerId>0)timeline.ResolveConversion(data.EntityId,entry.Tick,data.OwnerId,data.Amount);
                if(frame.SpatialReady)visuals.Play(entry);
            }
        }

        private void ObserveClock(long tick)
        {
            if (tick < latestServerTick) return;
            latestServerTick = tick; serverReceipt = Time.realtimeSinceStartupAsDouble;
        }

        private void Disconnect()
        { DisconnectTransport(); if (presentation != null) presentation.Clear(); if (ready) Show(ConnectionState.Disconnected, string.Empty); }

        private void DisconnectTransport()
        {
            if (fog != null) fog.ResetVision();
            if(hud!=null)hud.ResetHud();if(visuals!=null)visuals.Clear();
            if (connection == null) return;
            connection.FogChanged -= fog.Apply; connection.UnitsForgotten -= OnForgotten;
            connection.GameplayReceived-=OnGameplay;connection.EntitiesForgotten-=OnEntitiesForgotten;connection.CommandResponse-=OnCommandResponse;
            connection.PoseReceived -= OnPose; connection.UnitsReceived -= OnUnits; connection.Dispose(); connection = null;
        }

        private void Show(ConnectionState state, string detail)
        {
            if (displayedState == state && displayedDetail == detail && detail.Length > 0) return;
            displayedState = state; displayedDetail = detail; panel.Show(state, detail);
        }

        private void OnDisable()
        {
            ready = false;
            if (panel != null) { panel.ConnectRequested -= Connect; panel.DisconnectRequested -= Disconnect; }
            if(hud!=null){hud.TrainRequested-=Train;hud.RallyRequested-=Rally;}
            DisconnectTransport(); if (input != null) input.Release(); if (presentation != null) presentation.Clear();
            Application.runInBackground = previousBackground;
        }

        private static string FailureText(JoinFailure failure, string error)
        {
            switch (failure)
            {
                case JoinFailure.DataMismatch: return "서버와 게임 데이터가 다릅니다.";
                case JoinFailure.VersionMismatch: return "서버와 통신 버전이 다릅니다.";
                case JoinFailure.InvalidElement: return "허용되지 않은 시작 원소입니다.";
                case JoinFailure.WorldFull: return "서버의 월드 인원이 가득 찼습니다.";
                default: return string.IsNullOrEmpty(error) ? "연결이 종료됐습니다." : "연결 종료: " + error;
            }
        }
    }
}
