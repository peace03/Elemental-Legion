using ElementalLegion.Shared.Data;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>병력 창에 분대별 관측과 기존 재편·집결 조작을 배치한다.</summary>
    public sealed partial class CombatHud
    {
        private RectTransform forcesPanel, forceList, roster, rearrangement;
        private TMP_Text[] rosterLabels;
        private string[] rosterFormats;
        private TMP_Text rosterTotal;
        private readonly Button[] forcesTabs = new Button[2];
        private readonly TMP_Text[] forceRows = new TMP_Text[4];
        private readonly Button[] forceRowButtons = new Button[4];
        private TMP_Text rearrangementLabel;
        internal bool EditingQuantity => rallyCount?.isFocused == true || transferCount?.isFocused == true;

        private void BuildForcesPanel(ObservatoryUi ui, GameRules gameRules)
        {
            forcesPanel = ObservatoryUi.Panel(content.transform, "Forces", 0, 0, ManagementWidth, ManagementHeight, ObservatoryUi.Surface, true);
            BuildManagementHeading(ui, forcesPanel, "병력");
            forcesTabs[0] = ui.Button(forcesPanel, "분대", 12, 39, 160, 30, () => SetForcesTab(false));
            forcesTabs[1] = ui.Button(forcesPanel, "원소 구성", 176, 39, 160, 30, () => SetForcesTab(true));
            forceList = ObservatoryUi.Rect(forcesPanel, "Squad observation", 12, 77, 324, 371);
            for (int index = 0; index < forceRows.Length; index++)
            {
                byte mask = (byte)(1 << index);
                forceRowButtons[index] = ui.Button(forceList, "", 0, index * 43, 324, 40, () => SquadSelected?.Invoke(mask));
                forceRows[index] = forceRowButtons[index].GetComponentInChildren<TMP_Text>();
                forceRows[index].alignment = TextAlignmentOptions.MidlineLeft;
                forceRows[index].fontSize = 12;
            }
            rearrangementLabel = ui.Button(forceList, "재편", 230, 176, 94, 30, () => SetRearrangement(!rearrangement.gameObject.activeSelf))
                .GetComponentInChildren<TMP_Text>();
            rearrangementLabel.fontSize = 13;
            rearrangement = ObservatoryUi.Rect(forceList, "Rearrangement", 0, 212, 324, 159);

            BuildTransferControls(ui);
            rallyTarget = ui.Dropdown(rearrangement, new[] { "1번에 집결", "2번에 집결", "3번에 집결" }, 0, 0, 94, 114);
            rallyCount = ui.Input(rearrangement, "1", 120, 94, 46);
            rallyCount.contentType = TMP_InputField.ContentType.IntegerNumber;
            rallyCount.characterLimit = 5;
            ((TMP_Text)rallyCount.placeholder).text = "수량";
            rallyTarget.onValueChanged.AddListener(_ => RefreshRallyInput());
            rallyCount.onValueChanged.AddListener(_ => RefreshRallyInput());
            rally.transform.SetParent(rearrangement, false);
            ObservatoryUi.Place((RectTransform)rally.transform, Vector2.up, new Vector2(172, -94), new Vector2(152, 40));
            ui.StyleButton(rally, true);
            rallyKey = rally.GetComponentInChildren<TMP_Text>();
            rallyKey.text = "야전 집결   [G]";
            rallyKey.fontSize = 13;
            rallyKey.alignment = TextAlignmentOptions.Center;
            ObservatoryUi.Fill(rallyKey.rectTransform);
            rallyStatus = ui.Label(rearrangement, "Rally status", "", 12, 0, 136, 324, 23, ObservatoryUi.Accent);

            roster = ObservatoryUi.Rect(forcesPanel, "Element composition", 12, 77, 324, 371);
            rosterTotal = ui.Label(roster, "Composition total", "", 16, 0, 0, 324, 32, ObservatoryUi.Gold);
            ui.Label(roster, "Nature group", "자연", 14, 0, 42, 324, 24, ObservatoryUi.Muted);
            ui.Label(roster, "Energy group", "에너지", 14, 0, 200, 324, 24, ObservatoryUi.Muted);
            rosterLabels = new TMP_Text[train.Length];
            rosterFormats = new string[train.Length];
            int[] nextCell = new int[2];
            for (int index = 0; index < train.Length; index++)
            {
                var element = gameRules.Elements[index];
                int cell = nextCell[(int)element.Group]++;
                float x = cell % 4 * 82;
                float y = (element.Group == ElementGroup.Nature ? 71 : 229) + cell / 4 * 64;
                ui.Icon(roster, "Composition icon", ui.Art.Element(element.Id), x + 23, y, 33, 33);
                rosterLabels[index] = ui.Label(roster, "Composition count", "", 12, x, y + 33, 78, 24);
                rosterLabels[index].alignment = TextAlignmentOptions.Center;
                rosterFormats[index] = names[index] + " {0}";
            }
            SetForcesTab(false);
            SetRearrangement(false);
        }

        private void SetForcesTab(bool showRoster)
        {
            roster.gameObject.SetActive(showRoster);
            forceList.gameObject.SetActive(!showRoster);
            SetButtonAccent(forcesTabs[0], !showRoster);
            SetButtonAccent(forcesTabs[1], showRoster);
            if (showRoster) RefreshRoster();
        }

        private void SetRearrangement(bool open)
        {
            rearrangement.gameObject.SetActive(open);
            rearrangementLabel.text = open ? "접기" : "재편";
        }

        private void RefreshRoster()
        {
            if (CurrentPanel != HudPanelKind.Forces || !roster.gameObject.activeSelf || observation == null) return;
            rosterTotal.SetText("소유 원소 {0}기", observation.Population);
            for (int index = 0; index < rosterLabels.Length; index++)
            {
                int amount = observation.ElementCounts[index];
                rosterLabels[index].SetText(rosterFormats[index], amount);
                rosterLabels[index].color = amount > 0 ? ObservatoryUi.TextColor : ObservatoryUi.Muted;
            }
        }
    }
}
