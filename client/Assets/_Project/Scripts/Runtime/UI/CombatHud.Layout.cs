using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    public sealed partial class CombatHud
    {
        private RectTransform hudHeader, mapPanel, squadPanel, commands, hudRect, basePanel, baseHealthFill;
        private TMP_Text baseFocusLabel;
        private Image baseHealthIndicator;
        private Vector2 layoutSize;
        [Header("전장 HUD 배치")]
        [SerializeField, Range(8, 26), Tooltip("전투 UI를 화면 가장자리에서 띄우는 거리(Canvas 픽셀)입니다.")] private float hudMargin = 16;
        [SerializeField, Range(10, 24), Tooltip("미니맵 분대 번호의 글자 크기(Canvas 픽셀)입니다.")] private float mapNumberSize = 10;
        [SerializeField, Range(10, 24), Tooltip("전장 원소 위 분대 번호의 글자 크기(Canvas 픽셀)입니다.")] private float battlefieldNumberSize = 13;
        [SerializeField, Min(0.05f), Tooltip("이동 점으로 표시하기 위한 두 서버 관측 사이의 거리(m)입니다.")] private float mapMotionThreshold = 0.2f;
        [SerializeField, Min(0.5f), Tooltip("미니맵 이동 점의 밝기가 반복되는 주기(초)입니다.")] private float mapMotionPeriod = 2.4f;

        /// <summary>프리팹의 참조를 유지하고 전투 중 필요한 정보만 화면 가장자리에 배치한다.</summary>
        internal void BuildObservatory(ObservatoryUi ui, GameRules gameRules, ElementalVisuals buildingVisuals)
        {
            if (Map != null) return;
            skin = ui;
            visuals = buildingVisuals;
            worldCamera = visuals.TargetCamera;
            scaler = GetComponentInParent<CanvasScaler>();
            hudRect = (RectTransform)content.transform;
            interaction = content.AddComponent<CanvasGroup>();
            foreach (TMP_Text label in GetComponentsInChildren<TMP_Text>(true)) label.font = ui.Font;
            Transform oldAudio = content.transform.Find("AudioControls");
            if (oldAudio != null) oldAudio.gameObject.SetActive(false);

            BuildTopCapsules(ui);
            BuildMap(ui, gameRules);
            BuildManagementPanels(ui, gameRules);
            BuildSquadControls(ui);
            feedback.transform.SetParent(content.transform, false);
            ObservatoryUi.Place(feedback.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -76), new Vector2(500, 30));
            feedback.fontSize = 15;
            feedback.alignment = TextAlignmentOptions.Center;
            feedback.color = ObservatoryUi.Gold;
            feedback.raycastTarget = false;
            BuildMainWorldGuidance(ui);
            buildingTimerLayer = ObservatoryUi.Rect(content.transform, "Building timers", 0, 0, 0, 0);
            ObservatoryUi.Fill(buildingTimerLayer);
            buildingTimerLayer.SetAsFirstSibling();
            BuildBattlefieldSquadBadges(ui);
            RefreshLayout();
        }

        private void BuildTopCapsules(ObservatoryUi ui)
        {
            Transform legacyHeader = economy.transform.parent;
            hudHeader = ObservatoryUi.Panel(content.transform, "Resources", 0, 0, 354, 44, ObservatoryUi.Surface, true);
            BuildCorePickupFeedback(ui);
            economy.transform.SetParent(hudHeader, false);
            ObservatoryUi.Place(economy.rectTransform, Vector2.up, new Vector2(44, -3), new Vector2(246, 38));
            economy.fontSize = 15;
            economy.alignment = TextAlignmentOptions.MidlineLeft;
            economy.raycastTarget = false;
            ui.Button(hudHeader, "현황", 292, 6, 54, 32, () => DashboardRequested?.Invoke())
                .GetComponentInChildren<TMP_Text>().fontSize = 13;

            basePanel = ObservatoryUi.Panel(content.transform, "Base health", 0, 0, 270, 44, ObservatoryUi.Surface, true);
            mapStatus = ui.Label(basePanel, "Base status", "", 15, 12, 3, 173, 26, ObservatoryUi.TextColor);
            RectTransform track = ObservatoryUi.Panel(basePanel, "Base health track", 12, 31, 173, 4, ObservatoryUi.Raised);
            baseHealthFill = ObservatoryUi.Panel(track, "Base health fill", 0, 0, 0, 0, ObservatoryUi.Accent);
            baseHealthIndicator = baseHealthFill.GetComponent<Image>();
            ObservatoryUi.Fill(baseHealthFill);
            baseHealthFill.anchorMax = Vector2.up;
            baseFocusLabel = ui.Button(basePanel, "B", 193, 6, 68, 32, () => BaseFocusRequested?.Invoke())
                .GetComponentInChildren<TMP_Text>();
            ObservatoryUi.Fill(baseFocusLabel.rectTransform);
            baseFocusLabel.fontSize = 12;
            baseFocusLabel.enableAutoSizing = true;
            baseFocusLabel.fontSizeMin = 9;
            baseFocusLabel.fontSizeMax = 12;
            defenseAlert = ui.Label(content.transform, "Defense alert", "", 13, 0, 0, 270, 36, ObservatoryUi.Danger);
            defenseAlert.alignment = TextAlignmentOptions.TopRight;
            defenseAlert.gameObject.SetActive(false);

            // 예전 머리글 아래에 있던 참조가 비활성화되기 전에 각 새 영역으로 옮긴다.
            if (composition.transform.IsChildOf(legacyHeader)) composition.transform.SetParent(content.transform, false);
            if (feedback.transform.IsChildOf(legacyHeader)) feedback.transform.SetParent(content.transform, false);
            legacyHeader.gameObject.SetActive(false);
        }

        private void BuildMap(ObservatoryUi ui, GameRules gameRules)
        {
            mapPanel = ObservatoryUi.Panel(content.transform, "Observatory map", 0, 0, 194, 218, ObservatoryUi.Surface, true);
            ui.Label(mapPanel, "Map north", "북 ↑", 12, 10, 3, 52, 24, ObservatoryUi.Muted);
            ui.Button(mapPanel, "군단 중심", 96, 3, 88, 24, () => CameraCenterRequested?.Invoke())
                .GetComponentInChildren<TMP_Text>().fontSize = 12;
            RectTransform terrain = ObservatoryUi.Rect(mapPanel, "Exploration", 10, 30, 174, 174);
            RawImage mapImage = terrain.gameObject.AddComponent<RawImage>();
            mapImage.raycastTarget = false;
            terrain.gameObject.AddComponent<RectMask2D>();
            RectTransform markers = ObservatoryUi.Rect(terrain, "Observed markers", 0, 0, 174, 174);
            ObservatoryUi.Fill(markers);
            Map = markers.gameObject.AddComponent<ObservatoryMap>();
            Map.Initialize(mapImage, gameRules);
            Map.ConfigureMarkers(ui, mapNumberSize, mapMotionThreshold, mapMotionPeriod);
            Map.SetSelectedSquads(selectedSquadMask);
            Map.FocusRequested += position => MapFocusRequested?.Invoke(position);
            Map.MovePointerChanged += (position, held, pressed) => MapMovePointerChanged?.Invoke(position, held, pressed);
        }

        private void RefreshLayout()
        {
            if (hudRect == null || scaler == null) return;
            Vector2 size = hudRect.rect.size;
            if (size.x <= 0 || size.y <= 0) size = scaler.referenceResolution;
            if (size == layoutSize) return;
            layoutSize = size;
            float margin = Mathf.Clamp(hudMargin, 8, 26);
            ObservatoryUi.Place(hudHeader, Vector2.up, new Vector2(margin, -margin), hudHeader.sizeDelta);
            ObservatoryUi.Place(basePanel, Vector2.one, new Vector2(-margin, -margin), basePanel.sizeDelta);
            ObservatoryUi.Place(defenseAlert.rectTransform, Vector2.one, new Vector2(-margin, -margin - 48), defenseAlert.rectTransform.sizeDelta);
            ObservatoryUi.Place(mapPanel, Vector2.zero, new Vector2(margin, margin), mapPanel.sizeDelta);
            ObservatoryUi.Place(squadPanel, new Vector2(.5f, 0), new Vector2(0, margin), squadPanel.sizeDelta);
            ObservatoryUi.Place(commands, Vector2.right, new Vector2(-margin, margin), commands.sizeDelta);
            LayoutManagementPanels(size.y, margin);
        }
    }
}
