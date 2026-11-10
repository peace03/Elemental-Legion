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
        private TMP_Text squadStatus, transferStatus;
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
        internal event Action RecallRequested;

        private void BuildSquadControls(ObservatoryUi ui)
        {
            RectTransform panel = squadPanel = ObservatoryUi.Panel(content.transform, "Squad commands", 0, 0, 238,
                244 + recallRowHeight * 3 + recallRowSpacing * 2, ObservatoryUi.Surface, true);
            ObservatoryUi.Place(panel, Vector2.up, new Vector2(16, -76), panel.sizeDelta);
            ui.Label(panel, "Squad title", "분대 지휘", 15, 12, 4, 214, 24, ObservatoryUi.Gold);
            for (int index = 0; index < 5; index++)
            {
                byte mask = index == 4 ? (byte)7 : (byte)(1 << index);
                squadButtons[index] = ui.Button(panel, "", 12 + index % 3 * 74, 32 + index / 3 * 34, 68, 30,
                    () => SquadSelected?.Invoke(mask));
                squadLabels[index] = squadButtons[index].GetComponentInChildren<TMP_Text>();
                squadLabels[index].fontSize = 10;
            }
            squadStatus = ui.Label(panel, "Squad status", "", 12, 12, 101, 214, 42, ObservatoryUi.Accent);
            transferTarget = ui.Dropdown(panel, new[] { "1번으로", "2번으로", "3번으로", "수비대로" }, 1, 12, 149, 106);
            transferCount = ui.Input(panel, "1", 124, 149, 48);
            transferCount.contentType = TMP_InputField.ContentType.IntegerNumber;
            transferCount.characterLimit = 5;
            ((TMP_Text)transferCount.placeholder).text = "수량";
            transfer = ui.Button(panel, "재편", 178, 149, 48, 40, () =>
            {
                if (int.TryParse(transferCount.text, out int count))
                    TransferRequested?.Invoke((byte)(transferTarget.value + 1), count);
            });
            transfer.GetComponentInChildren<TMP_Text>().fontSize = 12;
            transferStatus = ui.Label(panel, "Transfer status", "본진 근처에서 재편", 11, 12, 196, 214, 36, ObservatoryUi.Muted);
            guardReturn = ui.Button(panel, "수비대 집결", 12, 244 - recallRowSpacing, 214, recallRowHeight, () =>
            {
                if (!TryGetGuardReturn(out int count)) return;
                TransferRequested?.Invoke(4, count);
                feedback.SetText($"수비대 집결 {count}기 요청");
            });
            guardReturn.name = "Guard return";
            guardReturnLabel = guardReturn.GetComponentInChildren<TMP_Text>();
            guardReturnLabel.fontSize = recallFontSize;
            float recallTop = 244 + recallRowHeight;
            recall = ui.Button(panel, "", 12, recallTop, 214, recallRowHeight, () =>
            {
                if (TryGetRecall(out _)) RecallRequested?.Invoke();
            });
            recallLabel = recall.GetComponentInChildren<TMP_Text>();
            recallLabel.fontSize = recallFontSize;
            recallStatus = ui.Label(panel, "Recall status", "", recallFontSize, 12,
                recallTop + recallRowHeight + recallRowSpacing, 214, recallRowHeight, ObservatoryUi.Accent);
            transferTarget.onValueChanged.AddListener(_ => RefreshSquads());
            transferCount.onValueChanged.AddListener(_ => RefreshSquads());
            RefreshSquads();
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
                recallStatus.text = $"{observation.RecallSquad}번 귀환 시전 · {Math.Max(0, observation.RecallEndTick - observation.Tick) * rules.DeltaTime:0.0}초\n이동·피격 시 취소";
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
            RefreshSquads();
        }

        internal void ShowMoveDestination(byte mask, System.Numerics.Vector2 destination)
        {
            feedback.SetText(mask == 7 ? $"야전 전체 이동 → {destination.X:0}, {destination.Y:0}" :
                $"선택 분대 이동 → {destination.X:0}, {destination.Y:0}");
        }

        private void RefreshSquads()
        {
            RefreshRecall();
            if (guardReturn != null)
            {
                guardReturn.interactable = TryGetGuardReturn(out int count);
                guardReturnLabel.text = $"수비대로 집결 · {count}기";
            }
            if (squadStatus == null) return;
            int total = 0;
            for (int index = 0; index < 4; index++)
            {
                SquadView squad = observation != null && observation.Squads.Length == 4 ? observation.Squads[index] : default;
                string title = index == 3 ? "수비" : (index + 1) + "번";
                squadLabels[index].text = $"[{squadHotkeys[index]}] {title}\n{squad.JoinedCount}기";
                squadLabels[index].color = selectedSquadMask == 1 << index ? ObservatoryUi.Gold : ObservatoryUi.TextColor;
                if (index < 3) total += squad.JoinedCount;
            }
            squadLabels[4].text = $"[{squadHotkeys[4]}] 전체\n{total}기";
            squadLabels[4].color = selectedSquadMask == 7 ? ObservatoryUi.Gold : ObservatoryUi.TextColor;
            bool single = selectedSquadMask != 7;
            int selected = selectedSquadMask == 8 ? 3 : selectedSquadMask == 4 ? 2 : selectedSquadMask == 2 ? 1 : 0;
            bool ready = observation != null && observation.Squads.Length == 4;
            SquadView current = ready ? observation.Squads[selected] : default;
            squadStatus.text = !single ? "야전 1·2·3 지휘 · 수비대 제외" :
                $"합류 {current.JoinedCount} · 증원 {current.ReinforcingCount} · 예약 {current.ReservedPopulation}\n" +
                (current.Order == SquadOrderKind.MoveTo ? $"목적지 {current.Destination.X:0}, {current.Destination.Y:0}" :
                    current.Order == SquadOrderKind.MoveDir ? "직접 이동 중" : "대기 중");
            bool nearBase = ready && System.Numerics.Vector2.DistanceSquared(current.Position, observation.BasePosition) <= rules.GarrisonRadius * rules.GarrisonRadius;
            bool validCount = int.TryParse(transferCount.text, out int amount) && amount > 0 && amount <= current.JoinedCount;
            transfer.interactable = ready && single && (nearBase || transferTarget.value == 3) && validCount && transferTarget.value != selected;
            transferStatus.text = !single ? "재편할 분대 하나를 선택하세요." : !nearBase ? "재편은 본진 근처에서\n수비대 집결은 본진으로 이동" :
                "선택 분대 → 대상 분대 · 수량 지정";
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
