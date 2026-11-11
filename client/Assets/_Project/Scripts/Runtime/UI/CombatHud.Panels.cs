using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    internal enum HudPanelKind { None, Training, Forces, Construction, BuildingInfo, ElementInfo }

    /// <summary>전장 오른쪽 영역에 표시할 창 하나를 관리한다.</summary>
    public sealed partial class CombatHud
    {
        private const float ManagementWidth = 348;
        private const float ManagementHeight = 468;
        private readonly Button[] managementButtons = new Button[3];
        private TMP_Text quickTransferFeedback;
        private byte quickTransferSource, quickTransferTarget;
        private double quickTransferFeedbackUntil;
        [SerializeField, Range(1, 8), Tooltip("빠른 전송 요청과 서버 결과를 표시할 시간(초)입니다.")]
        private float quickTransferFeedbackSeconds = 3;

        internal HudPanelKind CurrentPanel { get; private set; }

        private void BuildManagementPanels(ObservatoryUi ui, ElementalLegion.Shared.Data.GameRules gameRules)
        {
            commands = ObservatoryUi.Panel(content.transform, "Observatory commands", 0, 0, 232, 36, ObservatoryUi.Surface, true);
            string[] captions = { "양성", "병력", "건설" };
            for (int index = 0; index < managementButtons.Length; index++)
            {
                HudPanelKind panel = (HudPanelKind)(index + 1);
                managementButtons[index] = ui.Button(commands, captions[index], 80 * index, 0, 72, 36,
                    () => SetPanel(CurrentPanel == panel ? HudPanelKind.None : panel));
                managementButtons[index].GetComponentInChildren<TMP_Text>().fontSize = 15;
            }

            BuildTrainingPanel(ui, gameRules);
            BuildForcesPanel(ui, gameRules);
            construction = new ConstructionPanel(ui, content.transform, gameRules, names,
                (kind, x, z, element) => BuildRequested?.Invoke(kind, x, z, element), () => UpgradeRequested?.Invoke(),
                () => ClosePanel());
            SetPanel(HudPanelKind.None);
        }

        private void LayoutManagementPanels(float height, float margin)
        {
            float panelHeight = Mathf.Min(ManagementHeight, height - 156);
            float bottom = margin + squadPanel.rect.height + 8;
            Vector2 position = new Vector2(-margin, bottom + panelHeight - height);
            Vector2 size = new Vector2(ManagementWidth, panelHeight);
            ObservatoryUi.Place(trainingPanel, Vector2.one, position, size);
            ObservatoryUi.Place(forcesPanel, Vector2.one, position, size);
            construction.Layout(size.x, size.y, position);
            LayoutTrainingQueue(panelHeight);
        }

        private void BuildManagementHeading(ObservatoryUi ui, RectTransform panel, string heading)
        {
            ui.Label(panel, "Heading", heading, 20, 12, 5, ManagementWidth - 62, 29, ObservatoryUi.Gold);
            Button close = ui.Button(panel, "닫기", ManagementWidth - 62, 5, 50, 29, () => ClosePanel());
            close.GetComponentInChildren<TMP_Text>().fontSize = 12;
        }

        internal void SetPanel(HudPanelKind panel)
        {
            bool changed = CurrentPanel != panel;
            CurrentPanel = panel;
            if (trainingPanel == null || forcesPanel == null) return;
            if (panel != HudPanelKind.Training) hoveredTrainingElement = -1;
            trainingPanel.gameObject.SetActive(panel == HudPanelKind.Training);
            forcesPanel.gameObject.SetActive(panel == HudPanelKind.Forces);
            bool constructionOpen = panel == HudPanelKind.Construction;
            if (construction.IsOpen != constructionOpen) construction.SetOpen(constructionOpen);
            if (!CanShowWorldInfo(panel)) construction.HideWorldInfo();
            for (int index = 0; index < managementButtons.Length; index++)
                SetButtonAccent(managementButtons[index], (int)panel == index + 1);
            if (panel == HudPanelKind.Training) RefreshTraining();
            if (panel == HudPanelKind.Forces) RefreshSquads();
            if (changed) RefreshGuidance();
        }

        internal bool ClosePanel()
        {
            if (CurrentPanel == HudPanelKind.None) return false;
            if (CanShowWorldInfo(CurrentPanel)) construction?.CloseWorldInfo();
            SetPanel(HudPanelKind.None);
            return true;
        }

        private static bool CanShowWorldInfo(HudPanelKind panel) =>
            panel == HudPanelKind.None || panel == HudPanelKind.BuildingInfo || panel == HudPanelKind.ElementInfo;

        private void SetButtonAccent(Button button, bool selected, Color? outline = null)
        {
            var frame = (LegionUiFrame)button.targetGraphic;
            frame.color = selected ? new Color32(12, 66, 77, 255) : ObservatoryUi.Raised;
            frame.Style(skin.Art.CornerCut, skin.Art.LineWidth, outline ?? (selected ? ObservatoryUi.Accent : ObservatoryUi.Gold));
        }

        internal void SetQuickTransferSource(byte squad)
        {
            if (quickTransferSource == squad) return;
            quickTransferSource = squad;
            RefreshSquadButtonAccents();
        }

        internal void SetQuickTransferFeedback(byte from, byte to, string message)
        {
            quickTransferTarget = string.IsNullOrEmpty(message) ? (byte)0 : to;
            quickTransferFeedbackUntil = Time.unscaledTimeAsDouble + Mathf.Clamp(quickTransferFeedbackSeconds, 1, 8);
            if (quickTransferFeedback != null)
            {
                quickTransferFeedback.text = quickTransferTarget == 0 ? string.Empty : $"{from} → {to} · {message}";
                quickTransferFeedback.transform.parent.gameObject.SetActive(quickTransferTarget != 0);
            }
            RefreshSquadButtonAccents();
        }

        private void TickQuickTransferFeedback(double now)
        {
            if (quickTransferTarget != 0 && now >= quickTransferFeedbackUntil)
                SetQuickTransferFeedback(0, 0, string.Empty);
        }
    }
}
