using System;
using ElementalLegion.Client.Input;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    public sealed partial class CombatHud
    {
        internal event Action<byte> SquadSelected;
        internal event Action<byte, int> TransferRequested;
        private readonly Button[] squadButtons = new Button[5];
        private readonly TMP_Text[] squadLabels = new TMP_Text[5];
        private TMP_Text transferStatus;
        private TMP_Dropdown transferTarget;
        private TMP_InputField transferCount;
        private Button transfer;
        private Button guardReturn;
        private TMP_Text guardReturnLabel;
        private byte selectedSquadMask = 1;
        private readonly string[] squadHotkeys = { "1", "2", "3", "4", "5" };
        [Header("양자 이동 표시")]
        [SerializeField, Min(24), Tooltip("귀환 버튼과 상태 표시의 행 높이(화면 단위)입니다.")] private float recallRowHeight = 40;
        [SerializeField, Min(0), Tooltip("귀환 버튼과 상태 표시 사이의 간격(화면 단위)입니다.")] private float recallRowSpacing = 6;
        [SerializeField, Min(8), Tooltip("귀환 버튼과 상태 표시의 글자 크기입니다.")] private float recallFontSize = 11;
        private Button recall;
        private TMP_Text recallLabel, recallStatus;
        private string recallKey = "R";
        private string moveDestinationFeedback;
        internal event Action RecallRequested;

        private void BuildSquadControls(ObservatoryUi ui)
        {
            float rowHeight = Mathf.Clamp(recallRowHeight, 34, 44);
            float statusTop = rowHeight + Mathf.Clamp(recallRowSpacing, 0, 8);
            const float statusHeight = 34;
            RectTransform panel = squadPanel = ObservatoryUi.Panel(content.transform, "Squad commands", 0, 0, 526,
                statusTop + statusHeight + 3, ObservatoryUi.Surface, true);
            for (int index = 0; index < 5; index++)
            {
                byte mask = index == 4 ? (byte)7 : (byte)(1 << index);
                squadButtons[index] = ui.Button(panel, "", 8 + index * 68, 7, 64, rowHeight + 15,
                    () => SquadSelected?.Invoke(mask));
                squadLabels[index] = squadButtons[index].GetComponentInChildren<TMP_Text>();
                ObservatoryUi.Fill(squadLabels[index].rectTransform);
                squadLabels[index].enableAutoSizing = true;
                squadLabels[index].fontSizeMin = 9;
                squadLabels[index].fontSizeMax = 12;
            }

            recall = ui.Button(panel, "", 352, 7, 166, rowHeight - 7, () =>
            {
                if (TryGetRecall(out _)) RecallRequested?.Invoke();
            });
            recallLabel = recall.GetComponentInChildren<TMP_Text>();
            recallLabel.fontSize = Mathf.Max(11, recallFontSize);
            recallLabel.enableAutoSizing = true;
            recallLabel.fontSizeMin = 9;
            recallLabel.fontSizeMax = recallLabel.fontSize;
            recallStatus = ui.Label(panel, "Recall status", "", Mathf.Max(11, recallFontSize), 352,
                statusTop, 166, statusHeight, ObservatoryUi.Accent);
            recallStatus.alignment = TextAlignmentOptions.Center;
            recallStatus.enableAutoSizing = true;
            recallStatus.fontSizeMin = 10;
            recallStatus.fontSizeMax = recallStatus.fontSize;
            RectTransform quick = ObservatoryUi.Panel(panel, "Quick transfer", 0, 0, 380, 32, ObservatoryUi.Surface);
            ObservatoryUi.Place(quick, new Vector2(.5f, 1), new Vector2(0, 39), quick.sizeDelta);
            quickTransferFeedback = ui.Label(quick, "Quick transfer feedback", "", 14, 8, 0, 364, 32, ObservatoryUi.Accent);
            quickTransferFeedback.alignment = TextAlignmentOptions.Center;
            quick.gameObject.SetActive(false);
            RefreshSquads();
            RefreshSquadButtonAccents();
        }

        private void BuildTransferControls(ObservatoryUi ui)
        {
            transferTarget = ui.Dropdown(rearrangement, new[] { "1번으로", "2번으로", "3번으로", "수비대로" }, 1, 0, 0, 114);
            transferCount = ui.Input(rearrangement, "1", 120, 0, 46);
            transferCount.contentType = TMP_InputField.ContentType.IntegerNumber;
            transferCount.characterLimit = 5;
            ((TMP_Text)transferCount.placeholder).text = "수량";
            transfer = ui.Button(rearrangement, "지정 수량 재편", 172, 0, 152, 40, () =>
            {
                if (transfer.interactable && int.TryParse(transferCount.text, out int count))
                    TransferRequested?.Invoke((byte)(transferTarget.value + 1), count);
            });
            transfer.GetComponentInChildren<TMP_Text>().fontSize = 13;
            transferStatus = ui.Label(rearrangement, "Transfer status", "", 12, 0, 43, 175, 43, ObservatoryUi.Muted);
            guardReturn = ui.Button(rearrangement, "수비대로 복귀", 182, 44, 142, 40, () =>
            {
                if (!TryGetGuardReturn(out int count)) return;
                TransferRequested?.Invoke(4, count);
                feedback.SetText($"수비대로 복귀 {count}기 요청");
            });
            guardReturn.name = "Guard return";
            guardReturnLabel = guardReturn.GetComponentInChildren<TMP_Text>();
            guardReturnLabel.fontSize = 12;
            transferTarget.onValueChanged.AddListener(_ => RefreshSquads());
            transferCount.onValueChanged.AddListener(_ => RefreshSquads());
        }

        private void ApplySquadBindings(ControlBindings controls)
        {
            for (int index = 0; index < 5; index++) squadHotkeys[index] = controls.Label(ControlBindings.Squad1 + index);
            recallKey = controls.Label(ControlBindings.Recall);
            RefreshSquads();
        }

        /// <summary>입력은 선택한 야전 분대 하나만 허용하고 실제 판정은 서버에 맡긴다.</summary>
        internal bool TryGetRecall(out byte squad)
        {
            squad = selectedSquadMask == 1 ? (byte)1 : selectedSquadMask == 2 ? (byte)2 : selectedSquadMask == 4 ? (byte)3 : (byte)0;
            return squad != 0 && observation != null && observation.Tick >= 0 && observation.Squads.Length == 4 &&
                observation.Squads[squad - 1].JoinedCount > 0 && observation.RecallSquad == 0 && observation.RecallReadyTick <= observation.Tick;
        }

        private void RefreshRecall()
        {
            if (recall == null) return;
            recallLabel.text = $"양자 이동 · 본진 귀환 [{recallKey}]";
            recall.interactable = TryGetRecall(out _);
            if (observation == null || observation.Tick < 0) recallStatus.text = "";
            else if (observation.RecallSquad != 0)
                recallStatus.text = $"{observation.RecallSquad}번 귀환 시전 · {Math.Max(0, observation.RecallEndTick - observation.Tick) * rules.DeltaTime:0.0}초\n이동 입력·피격 시 취소";
            else if (observation.RecallReadyTick > observation.Tick)
                recallStatus.text = $"공유 대기 · {(observation.RecallReadyTick - observation.Tick) * rules.DeltaTime:0.0}초";
            else if (selectedSquadMask != 1 && selectedSquadMask != 2 && selectedSquadMask != 4)
                recallStatus.text = "귀환할 야전 분대 하나를 선택하세요.";
            else recallStatus.text = recall.interactable ? "선택 분대의 합류 원소가 본진으로 귀환" : "합류한 원소가 없습니다.";
        }

        internal void RecallFailed(CommandFailure failure) => feedback.SetText(failure switch
        {
            CommandFailure.Busy => "양자 이동 시전 또는 공유 대기 중입니다.",
            CommandFailure.InvalidPlacement => "귀환 공간이 부족해 양자 이동을 취소했습니다.",
            _ => FailureMessage(failure)
        });

        internal void SelectSquad(byte mask)
        {
            selectedSquadMask = mask;
            Map?.SetSelectedSquads(mask);
            RefreshSquads();
            RefreshSquadButtonAccents();
        }

        internal void ShowMoveDestination(byte mask, System.Numerics.Vector2 destination)
        {
            moveDestinationFeedback = mask == 7 ? $"야전 전체 이동 → {destination.X:0}, {destination.Y:0}" :
                $"선택 분대 이동 → {destination.X:0}, {destination.Y:0}";
            if (feedback.text == moveDestinationFeedback) return;
            feedback.SetText(moveDestinationFeedback);
            RefreshGuidance();
        }

        internal void HideMoveDestination()
        {
            if (moveDestinationFeedback == null) return;
            if (feedback.text == moveDestinationFeedback)
            {
                feedback.SetText(string.Empty);
                RefreshGuidance();
            }
            moveDestinationFeedback = null;
        }

        private void RefreshSquads()
        {
            RefreshRecall();
            if (guardReturn != null)
            {
                guardReturn.interactable = TryGetGuardReturn(out int count);
                guardReturnLabel.text = $"수비대로 복귀 · {count}기";
            }
            if (squadButtons[0] == null) return;
            int total = 0;
            for (int index = 0; index < 4; index++)
            {
                SquadView squad = observation != null && observation.Squads.Length == 4 ? observation.Squads[index] : default;
                string title = index == 3 ? "수비" : (index + 1) + "번";
                squadLabels[index].text = $"[{squadHotkeys[index]}]\n{title} {squad.JoinedCount}기";
                if (CurrentPanel == HudPanelKind.Forces)
                    forceRows[index].text = $"{title}  ·  합류 {squad.JoinedCount}기  ·  증원 {squad.ReinforcingCount}기  ·  예약 {squad.ReservedPopulation}기\n" +
                        SquadOrderText(squad);
                if (index < 3) total += squad.JoinedCount;
            }
            squadLabels[4].text = $"[{squadHotkeys[4]}]\n전체 {total}기";
            bool single = selectedSquadMask != 7;
            int selected = selectedSquadMask == 8 ? 3 : selectedSquadMask == 4 ? 2 : selectedSquadMask == 2 ? 1 : 0;
            bool ready = observation != null && observation.Squads.Length == 4;
            SquadView current = ready ? observation.Squads[selected] : default;
            bool nearBase = ready && System.Numerics.Vector2.DistanceSquared(current.Position, observation.BasePosition) <= rules.GarrisonRadius * rules.GarrisonRadius;
            bool validCount = int.TryParse(transferCount.text, out int amount) && amount > 0 && amount <= current.JoinedCount;
            transfer.interactable = ready && single && (nearBase || transferTarget.value == 3) && validCount && transferTarget.value != selected;
            transferStatus.text = !single ? "재편할 분대 하나를 선택하세요." : !nearBase ? "재편은 본진 근처에서\n수비대 복귀는 본진으로 이동" :
                "선택 분대 → 대상 분대 · 수량 지정";
            RefreshRoster();
        }

        private static string SquadOrderText(SquadView squad) => squad.Order switch
        {
            SquadOrderKind.MoveTo => $"목적지 {squad.Destination.X:0}, {squad.Destination.Y:0}",
            SquadOrderKind.MoveDir => "직접 이동 중",
            _ => "대기 중"
        };

        private void RefreshSquadButtonAccents()
        {
            for (int index = 0; index < squadButtons.Length; index++)
            {
                if (squadButtons[index] == null) break;
                bool selected = index == 4 ? selectedSquadMask == 7 : selectedSquadMask == 1 << index;
                Color? outline = null;
                if (quickTransferSource == index + 1) outline = ObservatoryUi.Gold;
                else if (quickTransferTarget == index + 1) outline = new Color32(175, 139, 255, 255);
                SetButtonAccent(squadButtons[index], selected, outline);
                squadLabels[index].color = quickTransferSource == index + 1 ? ObservatoryUi.Gold : ObservatoryUi.TextColor;
                if (index < forceRowButtons.Length && forceRowButtons[index] != null)
                    SetButtonAccent(forceRowButtons[index], (selectedSquadMask & 1 << index) != 0);
            }
        }

        private bool TryGetGuardReturn(out int count)
        {
            count = 0;
            int selected = selectedSquadMask == 1 ? 0 : selectedSquadMask == 2 ? 1 : selectedSquadMask == 4 ? 2 : -1;
            if (selected < 0 || rules == null || observation == null || observation.Tick < 0 || observation.Squads.Length != 4 ||
                observation.BaseLevel < 1 || observation.BaseLevel > rules.MaxBaseLevel) return false;
            SquadView guards = observation.Squads[3];
            int free = Math.Max(0, rules.GarrisonCap(observation.BaseLevel) - guards.JoinedCount - guards.ReinforcingCount - guards.ReservedPopulation);
            count = Math.Min(observation.Squads[selected].JoinedCount, free);
            return count > 0;
        }
    }
}
