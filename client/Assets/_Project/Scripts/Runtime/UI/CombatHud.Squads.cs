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
        private byte selectedSquadMask = 1;
        private readonly string[] squadHotkeys = { "1", "2", "3", "4", "5" };

        private void BuildSquadControls(ObservatoryUi ui)
        {
            RectTransform panel = ObservatoryUi.Panel(content.transform, "Squad commands", 0, 0, 238, 244, ObservatoryUi.Surface, true);
            ObservatoryUi.Place(panel, Vector2.zero, new Vector2(16, 318), panel.sizeDelta);
            ui.Label(panel, "Squad title", "분대 지휘", 15, 12, 4, 214, 24, ObservatoryUi.Gold);
            for (int index = 0; index < 5; index++)
            {
                byte mask = index == 4 ? (byte)7 : (byte)(1 << index);
                squadButtons[index] = ui.Button(panel, "", 12 + index % 3 * 74, 32 + index / 3 * 34, 68, 30,
                    () => SquadSelected?.Invoke(mask));
                squadLabels[index] = squadButtons[index].GetComponentInChildren<TMP_Text>();
                squadLabels[index].fontSize = 11;
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
            transferTarget.onValueChanged.AddListener(_ => RefreshSquads());
            transferCount.onValueChanged.AddListener(_ => RefreshSquads());
            RefreshSquads();
        }

        private void ApplySquadBindings(ControlBindings controls)
        {
            for (int index = 0; index < 5; index++) squadHotkeys[index] = controls.Label(ControlBindings.Squad1 + index);
            RefreshSquads();
        }

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
            if (squadStatus == null) return;
            int total = 0;
            for (int index = 0; index < 4; index++)
            {
                SquadView squad = observation != null && observation.Squads.Length == 4 ? observation.Squads[index] : default;
                string title = index == 3 ? "수비" : (index + 1) + "번";
                squadLabels[index].text = $"[{squadHotkeys[index]}] {title} {squad.JoinedCount}";
                squadLabels[index].color = selectedSquadMask == 1 << index ? ObservatoryUi.Gold : ObservatoryUi.TextColor;
                if (index < 3) total += squad.JoinedCount;
            }
            squadLabels[4].text = $"[{squadHotkeys[4]}] 전체 {total}";
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
            transfer.interactable = ready && single && nearBase && validCount && transferTarget.value != selected;
            transferStatus.text = !single ? "재편할 분대 하나를 선택하세요." : !nearBase ? "본진 근처에서 재편할 수 있습니다." :
                "선택 분대 → 대상 분대 · 수량 지정";
        }
    }
}
