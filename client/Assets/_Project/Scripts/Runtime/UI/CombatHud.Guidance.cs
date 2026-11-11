using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;

namespace ElementalLegion.Client.UI
{
    /// <summary>서버가 관측한 자기 상태에서 현재 조작 안내와 본진 체력을 표시한다.</summary>
    public sealed partial class CombatHud
    {
        [Header("전장 안내와 타이머")]
        [SerializeField, Range(1, 128), Tooltip("건물·후보에 동시에 표시할 세계 타이머 개수 상한입니다. 본인 후보를 우선합니다.")]
        private int maxWorldTimers = 32;
        [SerializeField, Range(300, 900), Tooltip("전장 안내와 생략한 타이머 요약의 너비(Canvas 픽셀)입니다.")]
        private float guidanceWidth = 650;
        [SerializeField, Range(12, 24), Tooltip("전장 조작 안내의 글자 크기입니다.")]
        private float guidanceFontSize = 14;
        [SerializeField, Range(36, 100), Tooltip("전장 조작 안내의 높이(Canvas 픽셀)입니다.")]
        private float guidanceHeight = 64;
        [SerializeField, Range(76, 300), Tooltip("화면 위쪽부터 전장 안내까지의 거리(Canvas 픽셀)입니다. 결과·경보가 있으면 그 아래로 이동합니다.")]
        private float guidanceTop = 76;
        private TMP_Text guidance, worldTimerSummary;
        private bool guidanceEnabled = true;
        private int lastOmittedCandidates = -1;
        private string baseHealthText = "본진 HP 확인 중";

        private void ValidateMainWorldPresentation()
        {
            maxWorldTimers = Mathf.Clamp(maxWorldTimers, 1, 128);
            guidanceWidth = Mathf.Clamp(guidanceWidth, 300, 900);
            guidanceFontSize = Mathf.Clamp(guidanceFontSize, 12, 24);
            guidanceHeight = Mathf.Clamp(guidanceHeight, 36, 100);
            guidanceTop = Mathf.Clamp(guidanceTop, 76, 300);
        }

        internal void SetGuidanceEnabled(bool enabled)
        {
            guidanceEnabled = enabled;
            RefreshGuidance();
        }

        private void BuildMainWorldGuidance(ObservatoryUi ui)
        {
            ValidateMainWorldPresentation();
            guidance = ui.Label(content.transform, "World guidance", "", guidanceFontSize, 0, 0,
                guidanceWidth, guidanceHeight, ObservatoryUi.TextColor);
            guidance.alignment = TextAlignmentOptions.Top;
            ObservatoryUi.Place(guidance.rectTransform, new Vector2(.5f, 1), new Vector2(0, -guidanceTop), new Vector2(guidanceWidth, guidanceHeight));
            worldTimerSummary = ui.Label(content.transform, "World timer summary", "", guidanceFontSize, 0, 0,
                guidanceWidth, guidanceFontSize * 2, ObservatoryUi.Gold);
            worldTimerSummary.alignment = TextAlignmentOptions.Center;
            ObservatoryUi.Place(worldTimerSummary.rectTransform, new Vector2(.5f, 1), new Vector2(0, -guidanceTop - guidanceHeight),
                new Vector2(guidanceWidth, guidanceFontSize * 2));
            worldTimerSummary.gameObject.SetActive(false);
            RefreshGuidance();
        }

        private void RefreshBaseHealth()
        {
            baseHealthText = "본진 HP 확인 중";
            float healthRatio = 0;
            if (observation.SpatialReady)
                foreach (BuildingView building in observation.Buildings)
                    if (building.OwnerId == observation.PlayerId && building.Kind == BuildingKind.Base && building.MaxHealth > 0)
                    {
                        baseHealthText = $"본진 HP {building.Health:N0}/{building.MaxHealth:N0}";
                        healthRatio = Mathf.Clamp01(building.Health / building.MaxHealth);
                        break;
                    }
            RefreshMapStatus();
            if (baseHealthFill != null) baseHealthFill.anchorMax = new Vector2(healthRatio, 1);
        }

        private void RefreshMapStatus()
        {
            if (mapStatus != null) mapStatus.text = baseHealthText;
        }

        private void RefreshGuidance()
        {
            if (guidance == null) return;
            float top = feedback != null && !string.IsNullOrEmpty(feedback.text) ? Mathf.Max(guidanceTop, 110) : guidanceTop;
            if (defenseAlert != null && defenseAlert.gameObject.activeSelf) top = Mathf.Max(top, 150);
            guidance.rectTransform.anchoredPosition = new Vector2(0, -top);
            if (worldTimerSummary != null) worldTimerSummary.rectTransform.anchoredPosition = new Vector2(0, -top - guidanceHeight);
            guidance.gameObject.SetActive(guidanceEnabled && observation != null && observation.Tick >= 0 &&
                CurrentPanel == HudPanelKind.None && (interaction == null || interaction.interactable));
            if (!guidance.gameObject.activeSelf) return;
            if (defenseAlert != null && defenseAlert.gameObject.activeSelf)
            {
                guidance.text = $"실제 경보 · [{baseFocusKey}]로 본진을 확인하세요. 야전 분대를 선택하고 [{recallKey}]로 귀환을 요청할 수 있습니다.";
                return;
            }
            if (observation.TrainingElement >= 0)
            {
                guidance.text = $"목적 분대 {observation.TrainingTargetSquad} · {TrainingDestination(observation.TrainingTargetSquad)}로 양성 중입니다. 완료와 증원 합류를 확인하세요.";
                return;
            }
            if (observation.TrainingQueue.Count > 0)
            {
                byte target = observation.TrainingQueue[0].TargetSquad;
                guidance.text = $"목적 분대 {target} · {TrainingDestination(target)}에 양성이 예약되어 있습니다. 양성 목록에서 확인하거나 취소할 수 있습니다.";
                return;
            }
            if (observation.TrainedCount > 0)
            {
                foreach (SquadView squad in observation.Squads)
                    if (squad.ReinforcingCount > 0)
                    {
                        guidance.text = $"목적 분대 {squad.Id} · 증원 {squad.ReinforcingCount}기가 이동 중입니다. 병력 창에서 합류·증원·예약을 확인하세요.";
                        return;
                    }
                guidance.text = $"본진 방어 연습 · [{baseFocusKey}]로 본진을 확인하세요. 야전 분대 하나를 선택하고 [{recallKey}]로 귀환을 요청하세요. 시전·대기 시간은 귀환 표시를 확인하세요.";
                return;
            }
            bool forge = false;
            long forgeEndTick = 0;
            foreach (BuildingView building in observation.Buildings)
                if (building.OwnerId == observation.PlayerId && building.Kind == BuildingKind.NatureForge)
                {
                    if (building.IsUnderConstruction)
                    {
                        forgeEndTick = building.ConstructionEndTick;
                    }
                    else forge = true;
                }
            int element = rules.Elements[observation.MainElement].Group == ElementGroup.Nature ? observation.MainElement : rules.ElementIndex("water");
            if (forge)
                guidance.text = $"자연 원소 양성 · {names[element]} 1기당 {rules.TrainingCost(element, observation.MainElement):N0}핵 · {rules.TrainingSeconds(element, observation.MainElement):0.#}초. 양성 패널에서 수량과 목적 분대를 고르세요.";
            else if (forgeEndTick != 0)
                guidance.text = $"자연 양성소 건설 중 · 남은 {System.Math.Max(0, forgeEndTick - observation.Tick) * rules.DeltaTime:0.0}초를 기다리세요.";
            else if (observation.CoreBalance > rules.StartingCores || observation.Journey.Total.CoresIn > rules.StartingCores ||
                observation.Journey.Total.Converted > 0 || observation.Journey.Total.Rescued > 0)
                guidance.text = $"자연 양성소 건설 · {rules.BuildingCost(BuildingKind.NatureForge):N0}핵 · {rules.BuildingConstructionSeconds(BuildingKind.NatureForge):0.#}초. 핵을 모아 건설 패널에서 배치하세요.";
            else
                guidance.text = "첫 보상 · 가까이 보이는 핵을 모으거나 중립 원소와 교전해 핵을 확보하세요.";
        }
    }
}
