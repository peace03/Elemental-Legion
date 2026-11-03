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
    public sealed class CombatHud : MonoBehaviour
    {
        [SerializeField,Tooltip("접속 중에만 표시하는 전투 패널입니다.")] private GameObject content;
        [SerializeField,Tooltip("핵·현재/예약/최대 인구·수비대 표시입니다.")] private TMP_Text economy;
        [SerializeField,Tooltip("원소 구성과 양성 진행 표시입니다.")] private TMP_Text composition;
        [SerializeField,Tooltip("명령 처리 결과 표시입니다.")] private TMP_Text feedback;
        [SerializeField,Tooltip("14종 양성 버튼입니다. 공유 원소 순서를 따릅니다.")] private UnityEngine.UI.Button[] train;
        [SerializeField,Tooltip("이름·비용·시간·잠금 이유 표시입니다.")] private TMP_Text[] labels;
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

        internal void BuildObservatory(ObservatoryUi ui, GameRules gameRules, ElementalVisuals buildingVisuals)
        {
            if (Map != null) return;
            skin = ui;
            visuals = buildingVisuals;
            worldCamera = visuals.TargetCamera;
            scaler = GetComponentInParent<CanvasScaler>();
            interaction = content.AddComponent<CanvasGroup>();
            foreach (TMP_Text label in GetComponentsInChildren<TMP_Text>(true)) label.font = ui.Font;
            Transform oldAudio = content.transform.Find("AudioControls");
            if (oldAudio != null) oldAudio.gameObject.SetActive(false);

            var header = (RectTransform)economy.transform.parent;
            ObservatoryUi.Place(header, Vector2.up, new Vector2(16, -14), new Vector2(790, 76));
            var headerBackground = header.GetComponent<UnityEngine.UI.Image>();
            headerBackground.color = ObservatoryUi.Surface;
            headerBackground.raycastTarget = true;
            ObservatoryUi.Place((RectTransform)economy.transform, Vector2.up, new Vector2(18, -9), new Vector2(752, 28));
            ObservatoryUi.Place((RectTransform)composition.transform, Vector2.up, new Vector2(18, -39), new Vector2(752, 26));

            RectTransform navigation = ObservatoryUi.Panel(content.transform, "Observatory navigation", 0, 0, 344, 56, ObservatoryUi.Surface, true);
            ObservatoryUi.Place(navigation, Vector2.one, new Vector2(-16, -14), new Vector2(344, 56));
            ui.Button(navigation, "현황", 8, 8, 172, 40, () => DashboardRequested?.Invoke());
            menuKey = ui.Button(navigation, "메뉴", 188, 8, 148, 40, () => MenuRequested?.Invoke()).GetComponentInChildren<TMP_Text>();

            RectTransform mapPanel = ObservatoryUi.Panel(content.transform, "Observatory map", 0, 0, 238, 288, ObservatoryUi.Surface, true);
            ObservatoryUi.Place(mapPanel, Vector2.zero, new Vector2(16, 16), new Vector2(238, 288));
            ui.Label(mapPanel, "Map title", "탐색 지도                    북 ↑", 16, 12, 6, 214, 25, ObservatoryUi.Gold);
            RectTransform terrain = ObservatoryUi.Rect(mapPanel, "Exploration", 12, 34, 214, 214);
            RawImage mapImage = terrain.gameObject.AddComponent<RawImage>();
            mapImage.raycastTarget = false;
            terrain.gameObject.AddComponent<RectMask2D>();
            RectTransform markers = ObservatoryUi.Rect(terrain, "Observed markers", 0, 0, 214, 214);
            ObservatoryUi.Fill(markers);
            Map = markers.gameObject.AddComponent<ObservatoryMap>();
            Map.Initialize(mapImage, gameRules);
            mapStatus = ui.Label(mapPanel, "Map status", "", 14, 12, 251, 214, 28, ObservatoryUi.Accent);

            RectTransform commands = ObservatoryUi.Panel(content.transform, "Observatory commands", 0, 0, 258, 186, ObservatoryUi.Surface, true);
            ObservatoryUi.Place(commands, Vector2.right, new Vector2(-16, 16), new Vector2(258, 186));
            ui.Label(commands, "Command title", "군단 명령", 18, 12, 7, 234, 25, ObservatoryUi.Gold);
            construction = new ConstructionPanel(ui, content.transform, gameRules, names,
                (kind, x, z, element) => BuildRequested?.Invoke(kind, x, z, element), () => UpgradeRequested?.Invoke());
            ui.Button(commands, "양성 목록", 12, 39, 113, 36, () =>
            {
                construction.SetOpen(false);
                trainingPanel.gameObject.SetActive(!trainingPanel.gameObject.activeSelf);
            });
            ui.Button(commands, "건설 목록", 133, 39, 113, 36, () =>
            {
                trainingPanel.gameObject.SetActive(false);
                construction.SetOpen(!construction.IsOpen);
            });
            trainingPanel = (RectTransform)train[0].transform.parent;
            var trainingBackground = trainingPanel.GetComponent<UnityEngine.UI.Image>();
            trainingBackground.color = ObservatoryUi.Surface;
            trainingBackground.raycastTarget = true;
            ObservatoryUi.Place(trainingPanel, Vector2.right, new Vector2(-16, 212), new Vector2(596, 365));
            ui.Label(trainingPanel, "Training title", "원소 양성", 19, 12, 8, 440, 31, ObservatoryUi.Gold);
            ui.Button(trainingPanel, "닫기", 484, 7, 100, 34, () => trainingPanel.gameObject.SetActive(false));
            for (int index = 0; index < train.Length; index++)
            {
                ObservatoryUi.Place((RectTransform)train[index].transform, Vector2.up,
                    new Vector2(12 + index % 4 * 145, -49 - index / 4 * 60), new Vector2(137, 53));
                ObservatoryUi.Place((RectTransform)labels[index].transform, Vector2.up, new Vector2(6, -2), new Vector2(125, 49));
                labels[index].fontSize = 13;
                train[index].GetComponent<UnityEngine.UI.Image>().color = ObservatoryUi.Raised;
            }
            GameObject queueRoot = UnityEngine.UI.DefaultControls.CreateScrollView(new UnityEngine.UI.DefaultControls.Resources());
            queueRoot.transform.SetParent(trainingPanel, false);
            ObservatoryUi.Place((RectTransform)queueRoot.transform, Vector2.up, new Vector2(12, -290), new Vector2(572, 64));
            queueRoot.GetComponent<UnityEngine.UI.Image>().color = ObservatoryUi.Background;
            trainingQueueScroll = queueRoot.GetComponent<UnityEngine.UI.ScrollRect>();
            trainingQueueScroll.horizontalScrollbar.gameObject.SetActive(false);
            trainingQueueScroll.horizontalScrollbar = null;
            trainingQueueScroll.horizontal = false;
            trainingQueueScroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
            trainingQueueScroll.content.sizeDelta = new Vector2(0, 60);
            trainingQueue = ui.Label(trainingQueueScroll.content, "Training queue", "", 15, 8, 0, 536, 24, ObservatoryUi.Muted);
            rally.transform.SetParent(commands, false);
            ObservatoryUi.Place((RectTransform)rally.transform, Vector2.up, new Vector2(12, -83), new Vector2(234, 39));
            rally.GetComponent<UnityEngine.UI.Image>().color = ObservatoryUi.Accent;
            rallyKey = rally.GetComponentInChildren<TMP_Text>();
            rallyKey.color = ObservatoryUi.Background;
            rallyKey.alignment = TextAlignmentOptions.Center;
            ObservatoryUi.Fill((RectTransform)rallyKey.transform);
            rallyStatus = ui.Label(commands, "Rally status", "", 15, 12, 128, 234, 26, ObservatoryUi.Accent);
            feedback.transform.SetParent(commands, false);
            ObservatoryUi.Place((RectTransform)feedback.transform, Vector2.up, new Vector2(12, -153), new Vector2(234, 28));
            feedback.fontSize = 12;
            trainingPanel.gameObject.SetActive(false);
            buildingTimerLayer = ObservatoryUi.Rect(content.transform, "Building timers", 0, 0, 0, 0);
            ObservatoryUi.Fill(buildingTimerLayer);
            buildingTimerLayer.SetAsFirstSibling();
        }

        internal void ApplyPreferences(ControlBindings controls, int percent)
        {
            if (scaler != null) ObservatoryUi.SetScale(scaler, percent);
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

        private void LateUpdate()
        {
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
                        timer.SetText("유령 기지   {0:00}:{1:00}", (float)(seconds / 60), seconds % 60);
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
                labels[index].text = $"{name}  ·  {cost}핵\n{rules.TrainingSeconds(index, frame.MainElement):0.#}초  |  {reason}";
            }
        }

        private void UpdateTrainingQueue(IReadOnlyList<TrainingOrderView> orders)
        {
            if (trainingQueue == null) return;
            const int columns = 3, rowHeight = 34, headerHeight = 24;
            trainingQueue.text = orders.Count == 0 ? "양성 예약 없음" : $"양성 예약 {orders.Count}기";
            visibleOrders.Clear();
            for (int index = 0; index < orders.Count; index++)
            {
                TrainingOrderView order = orders[index];
                visibleOrders.Add(order.Id);
                if (!trainingOrders.TryGetValue(order.Id, out var row))
                {
                    ulong orderId = order.Id;
                    var button = skin.Button(trainingQueueScroll.content, "", 0, 0, 172, 30,
                        () => TrainingCancelRequested?.Invoke(orderId));
                    var label = button.GetComponentInChildren<TMP_Text>();
                    label.fontSize = 14;
                    row = (button, label);
                    trainingOrders.Add(orderId, row);
                }
                ObservatoryUi.Place((RectTransform)row.Button.transform, Vector2.up,
                    new Vector2(8 + index % columns * 178, -headerHeight - index / columns * rowHeight), new Vector2(172, 30));
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
            trainingQueueScroll.content.sizeDelta = new Vector2(0, Mathf.Max(60, headerHeight + rows * rowHeight));
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
