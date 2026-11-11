using System;
using System.Collections.Generic;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;
using ElementalLegion.Client.Input;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>관측 상태를 표시하고 양성·집결·건설 의도를 전달하는 전장 HUD다.</summary>
    [DefaultExecutionOrder(20)]
    public sealed partial class CombatHud : MonoBehaviour
    {
        [SerializeField,Tooltip("접속 중에만 표시하는 전투 패널입니다.")] private GameObject content;
        [SerializeField,Tooltip("핵·현재/예약/최대 인구·수비대 표시입니다.")] private TMP_Text economy;
        [SerializeField,Tooltip("원소 구성과 양성 진행 표시입니다.")] private TMP_Text composition;
        [SerializeField,Tooltip("명령 처리 결과 표시입니다.")] private TMP_Text feedback;
        [SerializeField,Tooltip("14종 양성 버튼입니다. 공유 원소 순서를 따릅니다.")] private UnityEngine.UI.Button[] train;
        [SerializeField,Tooltip("이름·전투 역할·비용·시간·잠금 이유 표시입니다.")] private TMP_Text[] labels;
        [SerializeField,Tooltip("수비대를 집결하는 버튼입니다.")] private UnityEngine.UI.Button rally;
        [SerializeField, Min(1), Tooltip("본진 피격 경보를 화면에 유지할 시간(초)입니다.")] private float defenseAlertSeconds = 6;
        [SerializeField, Range(.2f, 5), Tooltip("전향 후보 안내를 지면 위에 배치할 높이(m)입니다.")] private float candidateLabelHeight = 1.6f;
        [SerializeField, Tooltip("전향 후보 안내의 가로·세로 크기(Canvas 픽셀)입니다.")] private Vector2 candidateLabelSize = new Vector2(280, 48);
        private TMP_Dropdown rallyTarget;
        private TMP_InputField rallyCount;
        private int availableGuards;
        private bool rallyAvailable;
        private TMP_Text defenseAlert;
        private double defenseAlertUntil;
        private string baseFocusKey = "B";
        private GameRules rules;
        private string[] names;
        private bool subscribed;
        private TMP_Text rallyStatus, mapStatus, rallyKey;
        private string lastRallyNotice;
        private TMP_Text trainingQueue;
        private UnityEngine.UI.ScrollRect trainingQueueScroll;
        private readonly Dictionary<ulong, (UnityEngine.UI.Button Button, TMP_Text Label)> trainingOrders = new();
        private readonly HashSet<ulong> visibleOrders = new();
        private readonly List<ulong> obsoleteOrders = new();
        private CanvasScaler scaler;
        private CanvasGroup interaction;
        private RectTransform trainingPanel;
        private ConstructionPanel construction;
        private ObservatoryUi skin;
        private ElementalVisuals visuals;
        private UnityEngine.Camera worldCamera;
        private RectTransform buildingTimerLayer;
        private GameplayFrame observation;
        private readonly List<TMP_Text> buildingTimers = new List<TMP_Text>();
        internal ObservatoryMap Map { get; private set; }
        private void OnValidate()
        {
            candidateLabelHeight = Mathf.Clamp(candidateLabelHeight, .2f, 5);
            candidateLabelSize = CandidateLabelSize;
            ValidateMainWorldPresentation();
            layoutSize = default;
        }
        private Vector2 CandidateLabelSize => new Vector2(Mathf.Clamp(candidateLabelSize.x, 190, 600), Mathf.Clamp(candidateLabelSize.y, 48, 120));
        internal void ConfigureWorld(GameRules rules)
        {
            this.rules = rules;
            Map?.ConfigureWorld(rules);
            construction?.ConfigureWorld(rules);
        }
        public event Action<int, int, byte> TrainRequested;
        internal event Action<ulong> TrainingCancelRequested;
        public event Action RallyRequested;
        public event Action DashboardRequested;
        internal event Action<BuildingKind, int, int, int> BuildRequested;
        internal event Action UpgradeRequested;
        internal event Action<System.Numerics.Vector2> MapFocusRequested;
        internal event Action<System.Numerics.Vector2?, bool, bool> MapMovePointerChanged;
        internal event Action CameraCenterRequested;
        internal event Action BaseFocusRequested;


        internal void ApplyPreferences(ControlBindings controls, int percent)
        {
            if (scaler != null) ObservatoryUi.SetScale(scaler, percent);
            RefreshLayout();
            ApplySquadBindings(controls);
            baseFocusKey = controls.Label(ControlBindings.BaseFocus);
            RefreshDefenseAlertText();
            if (rallyKey != null) rallyKey.text = $"야전 집결   [{controls.Label(ControlBindings.Rally)}]";
            if (baseFocusLabel != null) baseFocusLabel.text = $"[{baseFocusKey}]";
            RefreshGuidance();
        }

        internal void BlockInput(bool blocked)
        {
            if (interaction != null && interaction.interactable == blocked)
            {
                interaction.interactable = !blocked;
                RefreshGuidance();
            }
            Map?.SetPointerBlocked(blocked);
            if (!blocked) return;
            construction?.CancelPlacement();
            construction?.HideWorldInfo();
            if (CurrentPanel != HudPanelKind.None && CanShowWorldInfo(CurrentPanel)) SetPanel(HudPanelKind.None);
        }

        internal bool CancelConstruction() => construction?.CancelPlacement() == true;
        internal void TickBuildingPointer(bool inputAllowed)
        {
            if (construction == null) return;
            bool infoAllowed = CanShowWorldInfo(CurrentPanel);
            construction.TickPointer(visuals, inputAllowed, infoAllowed);
            if (infoAllowed && CurrentPanel != construction.WorldInfoKind) SetPanel(construction.WorldInfoKind);
        }
        internal void ConstructionResult(string result) => construction?.SetResult(result);

        internal void ShowMigrationNotice(bool visible, bool defeatedHost = false)
        {
            const string notice = "연결 복구 중…";
            const string defeatNotice = "본진 파괴 · 서버 인계 중…";
            if (visible) feedback.SetText(defeatedHost ? defeatNotice : notice);
            else if (feedback.text == notice || feedback.text == defeatNotice) feedback.SetText(string.Empty);
        }


        private void LateUpdate()
        {
            TickDefenseAlert(Time.unscaledTimeAsDouble);
            TickQuickTransferFeedback(Time.unscaledTimeAsDouble);
            RefreshLayout();
            if (buildingTimerLayer == null || worldCamera == null) return;
            int limit = Mathf.Clamp(maxWorldTimers, 1, 128);
            while (buildingTimers.Count > limit)
            {
                TMP_Text removed = buildingTimers[buildingTimers.Count - 1];
                buildingTimers.RemoveAt(buildingTimers.Count - 1);
                removed.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(removed.gameObject); else DestroyImmediate(removed.gameObject);
            }
            int count = 0;
            int omitted = 0;
            RenderCandidateTimers(true, limit, ref count, ref omitted);
            if (observation != null && observation.SpatialReady)
                foreach (BuildingView building in observation.Buildings)
                {
                    if (count >= limit) break;
                    bool constructing = building.IsUnderConstruction;
                    bool ghost = building.Kind == BuildingKind.Base && building.GhostEndTick > observation.Tick;
                    if (!constructing && !ghost) continue;
                    float height = visuals.VisualBuildingHeight(building) + 1.2f;
                    Vector3 point = worldCamera.WorldToScreenPoint(new Vector3(building.Position.X, height, building.Position.Y));
                    if (point.z <= 0 || !worldCamera.pixelRect.Contains(point)) continue;
                    if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(buildingTimerLayer, point, null, out Vector2 local)) continue;
                    TMP_Text timer = TakeWorldTimer(count++, local, new Vector2(190, 26));
                    if (constructing)
                    {
                        timer.color = ObservatoryUi.Accent;
                        float seconds = (float)(Math.Max(0, building.ConstructionEndTick - observation.Tick) * rules.DeltaTime);
                        timer.SetText("건설 중   {0:1}초", seconds);
                    }
                    else
                    {
                        timer.color = ObservatoryUi.Gold;
                        int seconds = (int)Math.Ceiling((building.GhostEndTick - observation.Tick) * rules.DeltaTime);
                        timer.SetText("표류 행성   {0:00}:{1:00}", (float)(seconds / 60), seconds % 60);
                    }
                }
            RenderCandidateTimers(false, limit, ref count, ref omitted);
            if (worldTimerSummary != null)
            {
                worldTimerSummary.gameObject.SetActive(omitted > 0);
                if (omitted > 0 && omitted != lastOmittedCandidates)
                    worldTimerSummary.text = $"추가 후보 {omitted:N0}개 · 전장 표식으로 확인";
                lastOmittedCandidates = omitted;
            }
            for (int index = count; index < buildingTimers.Count; index++) buildingTimers[index].gameObject.SetActive(false);
        }

        private void RenderCandidateTimers(bool own, int limit, ref int count, ref int omitted)
        {
            if (observation == null || !observation.SpatialReady) return;
            foreach (ConversionCandidateView candidate in observation.Candidates)
            {
                if (candidate.EndTick <= observation.Tick || (candidate.Role != RecoveryRole.Observer) != own) continue;
                Vector3 point = worldCamera.WorldToScreenPoint(new Vector3(candidate.Position.X,
                    Mathf.Clamp(candidateLabelHeight, .2f, 5), candidate.Position.Y));
                if (point.z <= 0 || !worldCamera.pixelRect.Contains(point) ||
                    !RectTransformUtility.ScreenPointToLocalPointInRectangle(buildingTimerLayer, point, null, out Vector2 local)) continue;
                if (count >= limit) { omitted++; continue; }
                TMP_Text timer = TakeWorldTimer(count++, local, CandidateLabelSize);
                timer.color = candidate.Role == RecoveryRole.Observer ? ObservatoryUi.Gold : ObservatoryUi.Accent;
                float remaining = (float)((candidate.EndTick - observation.Tick) * rules.DeltaTime);
                if (candidate.Role == RecoveryRole.Observer)
                    timer.SetText("전향 후보 · 만료 {0:1}초", remaining);
                else
                {
                    float progress = (float)(Math.Min(candidate.ProgressTicks, rules.PvpRecoverySecureTicks) * rules.DeltaTime);
                    float required = (float)(rules.PvpRecoverySecureTicks * rules.DeltaTime);
                    timer.SetText(candidate.Role == RecoveryRole.Secure ?
                        "확보 {0:1}/{1:1}초\n만료 {2:1}초" : "구출 {0:1}/{1:1}초\n만료 {2:1}초", progress, required, remaining);
                }
            }
        }

        private TMP_Text TakeWorldTimer(int index, Vector2 position, Vector2 size)
        {
            if (index == buildingTimers.Count)
            {
                TMP_Text label = skin.Label(buildingTimerLayer, "Observed timer", "", 16, 0, 0, size.x, size.y, ObservatoryUi.Gold);
                label.alignment = TextAlignmentOptions.Center;
                label.raycastTarget = false;
                ObservatoryUi.Place(label.rectTransform, buildingTimerLayer.pivot, Vector2.zero, size);
                label.rectTransform.pivot = new Vector2(.5f, .5f);
                buildingTimers.Add(label);
            }
            TMP_Text timer = buildingTimers[index];
            timer.gameObject.SetActive(true);
            timer.rectTransform.sizeDelta = size;
            timer.rectTransform.anchoredPosition = position;
            return timer;
        }

        internal void ShowRally(int rallying, int waiting, string notice, bool canRally)
        {
            availableGuards = waiting;
            rallyAvailable = canRally;
            RefreshRallyInput();
            if (rallyStatus == null) return;
            string label = $"이동 중 증원 {rallying}기  ·  본진 대기 {waiting}기";
            if (rallyStatus.text != label) rallyStatus.text = label;
            if (notice != null && notice != lastRallyNotice) feedback.SetText(notice);
            else if (notice == null && lastRallyNotice != null && feedback.text == lastRallyNotice) feedback.SetText(string.Empty);
            lastRallyNotice = notice;
        }

        internal bool TryGetRally(out byte target, out int count)
        {
            target = (byte)((rallyTarget != null ? rallyTarget.value : 0) + 1);
            count = 0;
            return rallyCount != null && target >= 1 && target <= 3 &&
                int.TryParse(rallyCount.text, out count) && count > 0 && count <= availableGuards;
        }

        private void RefreshRallyInput() => rally.interactable = rallyAvailable && TryGetRally(out _, out _);

        internal void ShowBaseUnderAttack(double now)
        {
            if (defenseAlert == null) return;
            defenseAlertUntil = now + Mathf.Max(1, defenseAlertSeconds);
            RefreshDefenseAlertText();
            defenseAlert.gameObject.SetActive(true);
            if (baseHealthIndicator != null) baseHealthIndicator.color = ObservatoryUi.Danger;
            Map?.SetBaseAlert(true);
            RefreshGuidance();
        }

        private void RefreshDefenseAlertText()
        {
            if (defenseAlert != null) defenseAlert.text = $"본진 구역 피격 · [{baseFocusKey}] 본진 확인";
        }

        internal void TickDefenseAlert(double now)
        {
            if (defenseAlert != null && defenseAlert.gameObject.activeSelf && now >= defenseAlertUntil)
            {
                defenseAlert.gameObject.SetActive(false);
                if (baseHealthIndicator != null) baseHealthIndicator.color = ObservatoryUi.Accent;
                Map?.SetBaseAlert(false);
                RefreshGuidance();
            }
        }
        public void Configure(GameDataDocument document,GameRules gameRules)
        {
            if(content==null||economy==null||composition==null||feedback==null||rally==null||train.Length!=gameRules.Elements.Count||labels.Length!=train.Length)
                throw new InvalidOperationException("CombatHud 필수 참조가 없습니다.");
            rules=gameRules;names=new string[train.Length];
            for(int index=0;index<train.Length;index++)
            {
                int element=index;names[index]=(string)document.Root["elements"][index]["displayName"];
                if (!subscribed) train[index].onClick.AddListener(() =>
                {
                    if (trainingPanel == null) RequestTraining(element);
                    else SelectTrainingElement(element);
                });
            }
            if(!subscribed)rally.onClick.AddListener(()=>RallyRequested?.Invoke());subscribed=true;ResetHud();
        }
        public void Apply(GameplayFrame frame)
        {
            content.SetActive(true);
            observation = frame;
            RefreshBaseHealth();
            RefreshGuidance();
            RefreshSquads();
            construction?.Observe(frame);
            economy.text = $"핵 {frame.CoreBalance:N0}  ·  인구 {frame.Population:N0}/{frame.PopulationCap:N0}  ·  예약 {frame.ReservedPopulation:N0}";
            RefreshTraining();
        }

        private void RequestTraining(int element)
        {
            if (!TryGetTraining(element, out _, out _, out string reason) || reason != null) return;
            TrainRequested?.Invoke(element, trainingQuantity, trainingDestination);
        }

        private static string TrainingDestination(byte target) => target == 4 ? "수비대" : $"{target}번";

        private void UpdateTrainingQueue(IReadOnlyList<TrainingOrderView> orders)
        {
            if (trainingQueue == null) return;
            const int rowHeight = 28, headerHeight = 20, rowWidth = 302;
            trainingQueue.text = orders.Count == 0 ? "양성 예약 없음" : $"양성 예약 {orders.Count}기";
            visibleOrders.Clear();
            for (int index = 0; index < orders.Count; index++)
            {
                TrainingOrderView order = orders[index];
                visibleOrders.Add(order.Id);
                if (!trainingOrders.TryGetValue(order.Id, out var row))
                {
                    ulong orderId = order.Id;
                    var button = skin.Button(trainingQueueScroll.content, "", 0, 0, rowWidth, 25,
                        () => TrainingCancelRequested?.Invoke(orderId));
                    var label = button.GetComponentInChildren<TMP_Text>();
                    label.fontSize = 12;
                    skin.ButtonIcon(button, skin.Art.Element(rules.Elements[order.Element].Id), 21);
                    row = (button, label);
                    trainingOrders.Add(orderId, row);
                }
                ObservatoryUi.Place((RectTransform)row.Button.transform, Vector2.up,
                    new Vector2(6, -headerHeight - index * rowHeight), new Vector2(rowWidth, 25));
                row.Label.text = $"{index + 1}. {names[order.Element]} → {TrainingDestination(order.TargetSquad)} · 취소";
            }
            obsoleteOrders.Clear();
            foreach (var item in trainingOrders)
                if (!visibleOrders.Contains(item.Key)) obsoleteOrders.Add(item.Key);
            foreach (ulong id in obsoleteOrders)
            {
                GameObject button = trainingOrders[id].Button.gameObject;
                button.SetActive(false);
                if (Application.isPlaying) Destroy(button); else DestroyImmediate(button);
                trainingOrders.Remove(id);
            }
            trainingQueueScroll.content.sizeDelta = new Vector2(0, headerHeight + orders.Count * rowHeight);
        }

        internal static string FailureMessage(CommandFailure failure) => failure switch
        {
            CommandFailure.InsufficientCores => "핵이 부족합니다.",
            CommandFailure.PopulationCap => "인구 상한에 도달했습니다.",
            CommandFailure.Busy => "이미 양성 중입니다.",
            CommandFailure.NotUnlocked => "원소 또는 시설이 해금되지 않았습니다.",
            CommandFailure.InvalidPlacement => "이 위치에는 건설할 수 없습니다.",
            CommandFailure.BuildingLimit => "시설 또는 타워 슬롯이 가득 찼습니다.",
            CommandFailure.MaxLevel => "최고 본진 레벨입니다.",
            CommandFailure.TrainingNotWaiting => "이미 양성이 시작됐거나 취소된 예약입니다.",
            CommandFailure.RecallInterrupted => "이동 명령, 피격 또는 본진 상태 변화로 양자 이동을 취소했습니다.",
            _ => "명령 실패"
        };
        public void CommandFailed(CommandFailure failure) => feedback.SetText(FailureMessage(failure));
        internal void TrainingCanceled() => feedback.SetText("양성 예약 취소 · 핵 반환");
        public void Admission(CommandAdmission admission)=>feedback.SetText(admission==CommandAdmission.Accepted?"":$"명령 접수 실패: {admission}");
        public void ResetHud()
        {
            if(content!=null)content.SetActive(false);
            if(feedback!=null)feedback.text=string.Empty;
            moveDestinationFeedback = null;
            defenseAlertUntil = 0;
            if (defenseAlert != null) defenseAlert.gameObject.SetActive(false);
            availableGuards = 0;
            rallyAvailable = false;
            if (rally != null) RefreshRallyInput();
            lastRallyNotice = null;
            SetPanel(HudPanelKind.None);
            UpdateTrainingQueue(Array.Empty<TrainingOrderView>());
            if (Map != null) Map.ResetMap();
            observation = null;
            baseHealthText = "본진 HP 확인 중";
            RefreshMapStatus();
            if (baseHealthFill != null) baseHealthFill.anchorMax = Vector2.up;
            if (baseHealthIndicator != null) baseHealthIndicator.color = ObservatoryUi.Accent;
            RefreshGuidance();
            if (worldTimerSummary != null) worldTimerSummary.gameObject.SetActive(false);
            foreach (TMP_Text timer in buildingTimers) timer.gameObject.SetActive(false);
            ResetTrainingSelection();
            if (rallyTarget != null) rallyTarget.SetValueWithoutNotify(0);
            if (rallyCount != null) rallyCount.SetTextWithoutNotify("1");
            if (transferTarget != null) transferTarget.SetValueWithoutNotify(1);
            if (transferCount != null) transferCount.SetTextWithoutNotify("1");
            if (forcesPanel != null)
            {
                SetForcesTab(false);
                SetRearrangement(false);
            }
            SetQuickTransferSource(0);
            SetQuickTransferFeedback(0, 0, string.Empty);
            RefreshRecall();
            construction?.Clear();
        }
        private void OnDestroy()
        {
            if (train != null) foreach (var button in train) if (button != null) button.onClick.RemoveAllListeners();
            if (rally != null) rally.onClick.RemoveAllListeners();
            if (rallyTarget != null) rallyTarget.onValueChanged.RemoveAllListeners();
            if (rallyCount != null) rallyCount.onValueChanged.RemoveAllListeners();
        }
    }
}
