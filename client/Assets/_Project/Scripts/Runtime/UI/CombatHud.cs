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
        private GameRules rules;
        private string[] names;
        private bool subscribed;
        private TMP_Text rallyStatus, mapStatus, rallyKey, menuKey;
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
        internal void ConfigureWorld(GameRules rules)
        {
            Map?.ConfigureWorld(rules);
            construction?.ConfigureWorld(rules);
        }
        public event Action<int> TrainRequested;
        internal event Action<ulong> TrainingCancelRequested;
        public event Action RallyRequested;
        public event Action MenuRequested;
        public event Action DashboardRequested;
        internal event Action<BuildingKind, int, int, int> BuildRequested;
        internal event Action UpgradeRequested;
        internal event Action<System.Numerics.Vector2> MapFocusRequested;
        internal event Action CameraCenterRequested;


        internal void ApplyPreferences(ControlBindings controls, int percent)
        {
            if (scaler != null) ObservatoryUi.SetScale(scaler, percent);
            RefreshLayout();
            ApplySquadBindings(controls);
            if (rallyKey != null) rallyKey.text = $"집결   [{controls.Label(ControlBindings.Rally)}]";
            if (menuKey != null) menuKey.text = $"메뉴   [{controls.Label(ControlBindings.Menu)}]";
        }

        internal void BlockInput(bool blocked)
        {
            if (interaction != null) interaction.interactable = !blocked;
            if (blocked) construction?.CancelPlacement();
        }

        internal bool CancelConstruction() => construction?.CancelPlacement() == true;
        internal void TickBuildingPointer(bool inputAllowed) => construction?.TickPointer(visuals, inputAllowed);
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
            RefreshLayout();
            if (buildingTimerLayer == null || worldCamera == null) return;
            int count = 0;
            if (observation != null && observation.SpatialReady)
                foreach (BuildingView building in observation.Buildings)
                {
                    bool constructing = building.IsUnderConstruction;
                    bool ghost = building.Kind == BuildingKind.Base && building.GhostEndTick > observation.Tick;
                    if (!constructing && !ghost) continue;
                    float height = visuals.VisualBuildingHeight(building) + 1.2f;
                    Vector3 point = worldCamera.WorldToScreenPoint(new Vector3(building.Position.X, height, building.Position.Y));
                    if (point.z <= 0 || !worldCamera.pixelRect.Contains(point)) continue;
                    if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(buildingTimerLayer, point, null, out Vector2 local)) continue;
                    if (count == buildingTimers.Count)
                    {
                        TMP_Text label = skin.Label(buildingTimerLayer, "Building timer", "", 16, 0, 0, 190, 26, ObservatoryUi.Gold);
                        label.alignment = TextAlignmentOptions.Center;
                        ObservatoryUi.Place(label.rectTransform, buildingTimerLayer.pivot, Vector2.zero, new Vector2(190, 26));
                        label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                        buildingTimers.Add(label);
                    }
                    TMP_Text timer = buildingTimers[count++];
                    timer.gameObject.SetActive(true);
                    timer.rectTransform.anchoredPosition = local;
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
            for (int index = count; index < buildingTimers.Count; index++) buildingTimers[index].gameObject.SetActive(false);
        }

        internal void ShowRally(int rallying, int waiting, string notice, bool canRally)
        {
            rally.interactable = canRally;
            if (rallyStatus == null) return;
            string label = $"집결 중 {rallying}기  ·  본진 대기 {waiting}기";
            if (rallyStatus.text != label) rallyStatus.text = label;
            string mapLabel = notice ?? (rallying > 0 ? $"집결 중 {rallying}기" : waiting > 0 ? $"본진 대기 {waiting}기" : "수비대 없음");
            if (mapStatus.text != mapLabel) mapStatus.text = mapLabel;
        }
        public void Configure(GameDataDocument document,GameRules gameRules)
        {
            if(content==null||economy==null||composition==null||feedback==null||rally==null||train.Length!=gameRules.Elements.Count||labels.Length!=train.Length)
                throw new InvalidOperationException("CombatHud 필수 참조가 없습니다.");
            rules=gameRules;names=new string[train.Length];
            for(int index=0;index<train.Length;index++)
            {
                int element=index;names[index]=(string)document.Root["elements"][index]["displayName"];
                if(!subscribed)train[index].onClick.AddListener(()=>TrainRequested?.Invoke(element));
            }
            if(!subscribed)rally.onClick.AddListener(()=>RallyRequested?.Invoke());subscribed=true;ResetHud();
        }
        public void Apply(GameplayFrame frame)
        {
            content.SetActive(true);
            observation = frame;
            RefreshSquads();
            if (rosterLabels != null)
                for (int index = 0; index < rosterLabels.Length; index++)
                {
                    rosterLabels[index].SetText(rosterFormats[index], frame.ElementCounts[index]);
                    rosterLabels[index].color = frame.ElementCounts[index] > 0 ? ObservatoryUi.TextColor : ObservatoryUi.Muted;
                }
            if (trainingProgress != null)
            {
                float amount = frame.TrainingElement < 0 ? 0 : frame.TrainingEndTick == frame.TrainingStartTick ? 1 :
                    Mathf.Clamp01((float)(frame.Tick - frame.TrainingStartTick) / (frame.TrainingEndTick - frame.TrainingStartTick));
                trainingProgress.anchorMax = new Vector2(amount, 1);
            }
            construction?.Observe(frame);
            economy.text=$"핵 {frame.CoreBalance}    인구 {frame.Population} + 예약 {frame.ReservedPopulation} / {frame.PopulationCap}    수비대 {frame.GarrisonCount}";
            var text=new System.Text.StringBuilder();
            if (Map == null)
            {
                for(int index=0;index<names.Length;index++)if(frame.ElementCounts[index]>0)text.Append(names[index]).Append(' ').Append(frame.ElementCounts[index]).Append("   ");
                text.Append('\n');
            }
            if(frame.TrainingElement>=0)
            {
                double seconds=Math.Max(0,(frame.TrainingEndTick-frame.Tick)*rules.DeltaTime);
                float progress=frame.TrainingEndTick==frame.TrainingStartTick?1:(float)(frame.Tick-frame.TrainingStartTick)/(frame.TrainingEndTick-frame.TrainingStartTick);
                text.Append("양성: ").Append(names[frame.TrainingElement]).Append(' ').Append(Mathf.Clamp01(progress).ToString("P0")).Append(" · ").Append(seconds.ToString("F1")).Append("초");
            }
            else text.Append("양성 대기");
            if (frame.TrainingQueue.Count > 0)
                text.Append("   ·   예약 ").Append(frame.TrainingQueue.Count).Append("기");
            composition.text=text.ToString();rally.interactable=frame.GarrisonCount>0;
            UpdateTrainingQueue(frame.TrainingQueue);
            bool natureForge=false,energyForge=false;
            foreach(BuildingView building in frame.Buildings)if(building.OwnerId==frame.PlayerId && !building.IsUnderConstruction)
            {natureForge|=building.Kind==BuildingKind.NatureForge;energyForge|=building.Kind==BuildingKind.EnergyForge;}
            for(int index=0;index<train.Length;index++)
            {
                bool nature=rules.Elements[index].Group==ElementGroup.Nature;int cost=rules.TrainingCost(index,frame.MainElement);
                bool unlocked=rules.IsUnlocked(frame.BaseLevel,nature?"forge_nature":"forge_energy")&&(nature?natureForge:energyForge);
                bool hasRoom = frame.Population + frame.ReservedPopulation < frame.PopulationCap;
                bool available = hasRoom && unlocked && frame.CoreBalance >= cost;
                string reason = !unlocked ? "미해금 · 양성소 필요" : !hasRoom ? "인구 상한" :
                    frame.CoreBalance < cost ? "핵 부족" : frame.TrainingElement >= 0 ? "예약" : "양성";
                train[index].interactable = available;
                string name = index == frame.MainElement ? $"{names[index]} (주력)" : names[index];
                string role = rules.Elements[index].Role switch
                {
                    UnitRole.Melee => "근접",
                    UnitRole.Mid => "중거리",
                    UnitRole.Ranged => "원거리",
                    _ => throw new InvalidOperationException("알 수 없는 원소 전투 역할입니다.")
                };
                labels[index].text = trainingPanel == null
                    ? $"{name}\n{role} · {cost}핵 · {rules.TrainingSeconds(index, frame.MainElement):0.#}초\n{reason}"
                    : $"{name}\n<size=9>{role} {cost}핵 {rules.TrainingSeconds(index, frame.MainElement):0.#}초</size>\n<size=9>{reason}</size>";
            }
        }

        private void UpdateTrainingQueue(IReadOnlyList<TrainingOrderView> orders)
        {
            if (trainingQueue == null) return;
            const int columns = 1, rowHeight = 28, headerHeight = 20;
            trainingQueue.text = orders.Count == 0 ? "양성 예약 없음" : $"양성 예약 {orders.Count}기";
            visibleOrders.Clear();
            for (int index = 0; index < orders.Count; index++)
            {
                TrainingOrderView order = orders[index];
                visibleOrders.Add(order.Id);
                if (!trainingOrders.TryGetValue(order.Id, out var row))
                {
                    ulong orderId = order.Id;
                    var button = skin.Button(trainingQueueScroll.content, "", 0, 0, 224, 25,
                        () => TrainingCancelRequested?.Invoke(orderId));
                    var label = button.GetComponentInChildren<TMP_Text>();
                    label.fontSize = 12;
                    skin.ButtonIcon(button, skin.Art.Element(rules.Elements[order.Element].Id), 21);
                    row = (button, label);
                    trainingOrders.Add(orderId, row);
                }
                ObservatoryUi.Place((RectTransform)row.Button.transform, Vector2.up,
                    new Vector2(6, -headerHeight - index * rowHeight), new Vector2(224, 25));
                row.Label.text = $"{index + 1}. {names[order.Element]} · 취소";
            }
            obsoleteOrders.Clear();
            foreach (var item in trainingOrders)
                if (!visibleOrders.Contains(item.Key)) obsoleteOrders.Add(item.Key);
            foreach (ulong id in obsoleteOrders)
            {
                GameObject button = trainingOrders[id].Button.gameObject;
                button.SetActive(false);
                Destroy(button);
                trainingOrders.Remove(id);
            }
            int rows = (orders.Count + columns - 1) / columns;
            trainingQueueScroll.content.sizeDelta = new Vector2(0, Mathf.Max(headerHeight, headerHeight + rows * rowHeight));
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
            _ => "명령 실패"
        };
        public void CommandFailed(CommandFailure failure) => feedback.SetText(FailureMessage(failure));
        internal void TrainingCanceled() => feedback.SetText("양성 예약 취소 · 핵 반환");
        public void Admission(CommandAdmission admission)=>feedback.SetText(admission==CommandAdmission.Accepted?"":$"명령 접수 실패: {admission}");
        public void ResetHud()
        {
            if(content!=null)content.SetActive(false);
            if(feedback!=null)feedback.text=string.Empty;
            if (trainingPanel != null) trainingPanel.gameObject.SetActive(false);
            UpdateTrainingQueue(Array.Empty<TrainingOrderView>());
            if (Map != null) Map.ResetMap();
            observation = null;
            construction?.Clear();
        }
        private void OnDestroy(){if(train!=null)foreach(var button in train)if(button!=null)button.onClick.RemoveAllListeners();if(rally!=null)rally.onClick.RemoveAllListeners();}
    }
}
