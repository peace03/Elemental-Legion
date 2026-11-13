using System;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Net;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>양성할 원소·수량·목적지를 선택하고 서버의 진행 상태를 표시한다.</summary>
    public sealed partial class CombatHud
    {
        private Button[] trainingQuantities;
        private readonly Button[] trainingDestinations = new Button[4];
        private readonly Button[] trainingTabs = new Button[2];
        private readonly int[] lastTrainingChoice = { -1, -1 };
        private Button trainingConfirm;
        private TMP_Text trainingConfirmLabel, trainingDetail;
        private RectTransform trainingProgress;
        private int trainingQuantity = 1, hoveredTrainingElement = -1;
        private byte trainingDestination = 4;
        private ElementGroup trainingGroup;

        private void BuildTrainingPanel(ObservatoryUi ui, GameRules gameRules)
        {
            trainingPanel = (RectTransform)train[0].transform.parent;
            var background = trainingPanel.GetComponent<Image>();
            background.color = ObservatoryUi.Surface;
            background.raycastTarget = true;
            BuildManagementHeading(ui, trainingPanel, "양성");
            for (int index = 0; index < trainingTabs.Length; index++)
            {
                var group = (ElementGroup)index;
                trainingTabs[index] = ui.Button(trainingPanel, group == ElementGroup.Nature ? "자연" : "에너지",
                    12 + index * 164, 39, 160, 30, () => SetTrainingGroup(group));
            }

            int[] nextCell = new int[trainingTabs.Length];
            for (int index = 0; index < train.Length; index++)
            {
                Button button = train[index];
                var element = gameRules.Elements[index];
                int cell = nextCell[(int)element.Group]++;
                ObservatoryUi.Place((RectTransform)button.transform, Vector2.up,
                    new Vector2(12 + cell % 4 * 82, -76 - cell / 4 * 65), new Vector2(78, 60));
                ui.StyleButton(button);
                ui.Icon(button.transform, "Element icon", ui.Art.Element(element.Id), 20, 3, 38, 35);
                ObservatoryUi.Place(labels[index].rectTransform, Vector2.up, new Vector2(0, -38), new Vector2(78, 20));
                labels[index].alignment = TextAlignmentOptions.Center;
                labels[index].fontSize = 13;
                labels[index].raycastTarget = false;
                int elementIndex = index;
                var hover = button.gameObject.AddComponent<EventTrigger>();
                var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
                enter.callback.AddListener(_ => { hoveredTrainingElement = elementIndex; RefreshTrainingDetails(); });
                var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
                exit.callback.AddListener(_ => { hoveredTrainingElement = -1; RefreshTrainingDetails(); });
                hover.triggers.Add(enter);
                hover.triggers.Add(exit);
            }

            ui.Label(trainingPanel, "Quantity", "수량", 13, 12, 203, 160, 23, ObservatoryUi.Muted);
            ui.Label(trainingPanel, "Destination", "목적 분대", 13, 176, 203, 160, 23, ObservatoryUi.Muted);
            trainingQuantities = new Button[gameRules.MaxTrainingBatchCount];
            float width = 160f / trainingQuantities.Length;
            for (int index = 0; index < trainingQuantities.Length; index++)
            {
                int count = index + 1;
                trainingQuantities[index] = ui.Button(trainingPanel, count.ToString(), 12 + index * width, 226,
                    width - 4, 30, () => { trainingQuantity = count; RefreshTrainingChoices(); });
                ObservatoryUi.Fill(trainingQuantities[index].GetComponentInChildren<TMP_Text>().rectTransform);
            }
            for (int index = 0; index < trainingDestinations.Length; index++)
            {
                byte squad = (byte)(index + 1);
                trainingDestinations[index] = ui.Button(trainingPanel, squad.ToString(), 176 + index * 41, 226,
                    37, 30, () => { trainingDestination = squad; RefreshTrainingChoices(); });
                ObservatoryUi.Fill(trainingDestinations[index].GetComponentInChildren<TMP_Text>().rectTransform);
            }

            trainingDetail = ui.Label(trainingPanel, "Training detail", "", 12, 12, 257, 324, 38);
            trainingConfirm = ui.Button(trainingPanel, "양성 확정", 12, 296, 324, 35, () =>
            {
                if (CurrentPanel == HudPanelKind.Training) RequestTraining(lastTrainingChoice[(int)trainingGroup]);
            }, true);
            trainingConfirmLabel = trainingConfirm.GetComponentInChildren<TMP_Text>();
            composition.transform.SetParent(trainingPanel, false);
            ObservatoryUi.Place(composition.rectTransform, Vector2.up, new Vector2(12, -335), new Vector2(324, 25));
            composition.fontSize = 12;
            composition.raycastTarget = false;
            RectTransform track = ObservatoryUi.Panel(trainingPanel, "Training progress track", 12, 362, 324, 3, ObservatoryUi.Raised);
            trainingProgress = ObservatoryUi.Panel(track, "Training progress", 0, 0, 0, 0, ObservatoryUi.Accent);
            ObservatoryUi.Fill(trainingProgress);

            GameObject queueRoot = RuntimeUiControls.CreateScrollView();
            queueRoot.name = "Training orders";
            queueRoot.transform.SetParent(trainingPanel, false);
            queueRoot.GetComponent<Image>().color = ObservatoryUi.Background;
            trainingQueueScroll = queueRoot.GetComponent<ScrollRect>();
            trainingQueueScroll.horizontalScrollbar.gameObject.SetActive(false);
            trainingQueueScroll.horizontalScrollbar = null;
            trainingQueueScroll.horizontal = false;
            trainingQueueScroll.movementType = ScrollRect.MovementType.Clamped;
            ObservatoryUi.StyleScroll(trainingQueueScroll);
            trainingQueue = ui.Label(trainingQueueScroll.content, "Training queue", "", 12, 6, 0, 300, 20, ObservatoryUi.Muted);
            ResetTrainingSelection();
        }

        private void LayoutTrainingQueue(float panelHeight) =>
            ObservatoryUi.Place((RectTransform)trainingQueueScroll.transform, Vector2.up, new Vector2(12, -372), new Vector2(324, panelHeight - 384));

        private void ResetTrainingSelection()
        {
            trainingQuantity = 1;
            trainingDestination = 4;
            for (int index = 0; index < lastTrainingChoice.Length; index++) lastTrainingChoice[index] = -1;
            if (trainingPanel != null) SetTrainingGroup(ElementGroup.Nature);
        }

        private void SetTrainingGroup(ElementGroup group)
        {
            trainingGroup = group;
            hoveredTrainingElement = -1;
            for (int index = 0; index < train.Length; index++)
                train[index].gameObject.SetActive(rules.Elements[index].Group == group);
            RefreshTrainingChoices();
        }

        private void SelectTrainingElement(int element)
        {
            if (CurrentPanel != HudPanelKind.Training || rules.Elements[element].Group != trainingGroup) return;
            lastTrainingChoice[(int)trainingGroup] = element;
            hoveredTrainingElement = -1;
            RefreshTrainingChoices();
        }

        private void RefreshTrainingChoices()
        {
            int selected = lastTrainingChoice[(int)trainingGroup];
            for (int index = 0; index < trainingTabs.Length; index++) SetButtonAccent(trainingTabs[index], index == (int)trainingGroup);
            for (int index = 0; index < trainingQuantities.Length; index++) SetButtonAccent(trainingQuantities[index], index + 1 == trainingQuantity);
            for (int index = 0; index < trainingDestinations.Length; index++) SetButtonAccent(trainingDestinations[index], index + 1 == trainingDestination);
            for (int index = 0; index < train.Length; index++) SetButtonAccent(train[index], index == selected);
            RefreshTrainingDetails();
        }

        private void RefreshTrainingDetails()
        {
            int selected = lastTrainingChoice[(int)trainingGroup];
            bool found = TryGetTraining(selected, out long cost, out double seconds, out string reason);
            trainingConfirm.interactable = found && reason == null;
            trainingConfirmLabel.text = selected < 0 ? "양성 확정" : $"{names[selected]} {trainingQuantity}기 양성 · {cost:N0}핵";
            int shown = hoveredTrainingElement >= 0 ? hoveredTrainingElement : selected;
            if (shown != selected) found = TryGetTraining(shown, out cost, out seconds, out reason);
            if (!found)
            {
                trainingDetail.text = "양성할 원소를 선택하세요.";
                return;
            }

            string role = TrainingRole(rules.Elements[shown].Role);
            string detail = $"{names[shown]} · {role} · 총 {cost:N0}핵 · {seconds:0.#}초/기";
            trainingDetail.text = reason == null ? detail : $"{detail}\n{reason}";
        }

        private static string TrainingRole(UnitRole role) => role switch
        {
            UnitRole.Melee => "근접",
            UnitRole.Mid => "중거리",
            UnitRole.Ranged => "원거리",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        private bool TryGetTraining(int element, out long cost, out double seconds, out string reason)
        {
            cost = 0;
            seconds = 0;
            reason = null;
            GameplayFrame frame = observation;
            if (frame == null || element < 0 || element >= rules.Elements.Count ||
                trainingQuantity < 1 || trainingQuantity > rules.MaxTrainingBatchCount) return false;

            bool hasForge = TryGetForgePrice(rules, frame, element, out int unitCost);
            cost = (long)unitCost * trainingQuantity;
            seconds = rules.TrainingSeconds(element, frame.MainElement);
            bool nature = rules.Elements[element].Group == ElementGroup.Nature;
            if (!rules.IsUnlocked(frame.BaseLevel, nature ? "forge_nature" : "forge_energy") || !hasForge)
                reason = "미해금 · 양성소 필요";
            else if (frame.Population + frame.ReservedPopulation + trainingQuantity > frame.PopulationCap)
                reason = "인구 상한";
            else if (trainingDestination == 4 && (frame.BaseLevel < 1 || frame.BaseLevel > rules.MaxBaseLevel || frame.Squads.Length != 4 ||
                frame.Squads[3].JoinedCount + frame.Squads[3].ReinforcingCount + frame.Squads[3].ReservedPopulation +
                trainingQuantity > rules.GarrisonCap(frame.BaseLevel)))
                reason = "수비대 상한";
            else if (frame.CoreBalance < cost)
                reason = "핵 부족";
            return true;
        }

        internal static bool TryGetForgePrice(GameRules rules, GameplayFrame frame, int element, out int price)
        {
            BuildingKind forge = rules.Elements[element].Group == ElementGroup.Nature ? BuildingKind.NatureForge : BuildingKind.EnergyForge;
            foreach (BuildingView building in frame.Buildings)
                if (building.OwnerId == frame.PlayerId && building.Kind == forge && !building.IsUnderConstruction)
                {
                    price = rules.TrainingCost(element, frame.MainElement, building.UpgradeBranch, building.UpgradeRank);
                    return true;
                }
            price = rules.TrainingCost(element, frame.MainElement);
            return false;
        }

        private void RefreshTraining()
        {
            GameplayFrame frame = observation;
            if (frame == null || trainingPanel != null && CurrentPanel != HudPanelKind.Training) return;
            if (frame.TrainingElement >= 0)
            {
                double remaining = Math.Max(0, frame.TrainingEndTick - frame.Tick) * rules.DeltaTime;
                composition.text = $"양성 중 · {names[frame.TrainingElement]} → {TrainingDestination(frame.TrainingTargetSquad)} · {remaining:0.0}초";
            }
            else composition.text = "양성 대기";
            if (trainingPanel == null)
            {
                for (int index = 0; index < train.Length; index++)
                {
                    train[index].interactable = TryGetTraining(index, out long cost, out double seconds, out string reason) && reason == null;
                    string name = index == frame.MainElement ? $"{names[index]} (주력)" : names[index];
                    labels[index].text = $"{name}\n{TrainingRole(rules.Elements[index].Role)} · 총 {cost:N0}핵 · {seconds:0.#}초/기\n{reason ?? "양성"}";
                }
                return;
            }

            float amount = frame.TrainingElement < 0 ? 0 : frame.TrainingEndTick == frame.TrainingStartTick ? 1 :
                Mathf.Clamp01((float)(frame.Tick - frame.TrainingStartTick) / (frame.TrainingEndTick - frame.TrainingStartTick));
            trainingProgress.anchorMax = new Vector2(amount, 1);
            for (int index = 0; index < labels.Length; index++)
                labels[index].text = index == frame.MainElement ? $"{names[index]} (주력)" : names[index];
            UpdateTrainingQueue(frame.TrainingQueue);
            RefreshTrainingDetails();
        }
    }
}
