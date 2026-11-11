using System.Text;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;

namespace ElementalLegion.Client.UI
{
    /// <summary>관측한 원소의 종류에 해당하는 기본 능력과 양성 조건을 표시한다.</summary>
    internal sealed class ElementTooltip
    {
        private readonly ObservatoryUi ui;
        private GameRules rules;
        private readonly string[] names;
        private readonly RectTransform root;
        private readonly UnityEngine.UI.Image icon;
        private readonly UnityEngine.UI.Button close;
        private readonly TMP_Text title, group, stats, effect, reactions, training;
        private readonly StringBuilder text = new StringBuilder();
        private int lastElement = -1, lastMainElement = -1;
        private float width = 348, regionHeight = 465;
        private Vector2 topRightOffset = new Vector2(-16, -70);

        internal ElementTooltip(ObservatoryUi ui, RectTransform parent, GameRules rules, string[] names, System.Action closeRequested)
        {
            this.ui = ui;
            this.rules = rules;
            this.names = names;
            root = ObservatoryUi.Panel(parent, "Element info", 0, 0, width, regionHeight, ObservatoryUi.Surface, true);
            icon = ui.Icon(root, "Element icon", null, 12, 10, 62, 62);
            close = ui.Button(root, "×", 306, 10, 30, 28, closeRequested);
            TMP_Text closeLabel = close.GetComponentInChildren<TMP_Text>();
            ObservatoryUi.Fill(closeLabel.rectTransform);
            closeLabel.overflowMode = TextOverflowModes.Overflow;
            title = ui.Label(root, "Element name", "", 20, 84, 10, 204, 38);
            title.overflowMode = TextOverflowModes.Overflow;
            group = ui.Label(root, "Element group and role", "", 15, 84, 47, 204, 24, ObservatoryUi.Muted);
            stats = ui.Label(root, "Element stats", "", 15, 12, 79, 324, 54);
            effect = ui.Label(root, "Element effect", "", 15, 12, 141, 324, 54);
            reactions = ui.Label(root, "Element synergies", "", 15, 12, 201, 324, 88);
            training = ui.Label(root, "Element training", "", 16, 12, 294, 324, 24, ObservatoryUi.Accent);
            Hide();
        }

        internal void ConfigureWorld(GameRules worldRules)
        {
            rules = worldRules;
            lastElement = -1;
        }

        internal void Layout(float width, float height, Vector2 topRightOffset)
        {
            this.width = width;
            regionHeight = height;
            this.topRightOffset = topRightOffset;
            Reflow();
        }

        internal void Show(int element, int mainElement)
        {
            if (element < 0 || element >= rules.Elements.Count) { Hide(); return; }
            if (lastElement != element || lastMainElement != mainElement)
            {
                lastElement = element;
                lastMainElement = mainElement;
                ElementDefinition definition = rules.Elements[element];
                string role = definition.Role switch
                {
                    UnitRole.Melee => "근접",
                    UnitRole.Mid => "중거리",
                    _ => "원거리"
                };
                icon.sprite = ui.Art.Element(definition.Id);
                title.text = names[element];
                group.text = $"{(definition.Group == ElementGroup.Nature ? "자연" : "에너지")} · {role}";
                stats.text = $"최대 체력 {definition.Hp:0.##}   ·   공격력 {definition.Attack:0.##}\n" +
                    $"공격 속도 {definition.AttacksPerSecond:0.##}회/초   ·   사거리 {definition.Range:0.##}m";
                effect.text = DescribeEffect(definition);
                reactions.text = DescribeReactions(element);
                bool isMain = definition.Group == ElementGroup.Nature && element == mainElement;
                training.text = $"{(isMain ? "주력 양성" : "양성")} {rules.TrainingCost(element, mainElement)}핵 · {rules.TrainingSeconds(element, mainElement):0.##}초";
                Reflow();
            }
            root.gameObject.SetActive(true);
        }

        internal void SetPinned(bool pinned)
        {
            close.gameObject.SetActive(pinned);
        }

        internal bool Contains(Transform item) => item != null && item.IsChildOf(root);

        internal void Hide()
        {
            SetPinned(false);
            root.gameObject.SetActive(false);
        }

        private void Reflow()
        {
            ObservatoryUi.Place((RectTransform)close.transform, Vector2.up, new Vector2(width - 42, -10), new Vector2(30, 28));
            ObservatoryUi.Place(title.rectTransform, Vector2.up, new Vector2(84, -10), new Vector2(width - 136, 38));
            ObservatoryUi.Place(group.rectTransform, Vector2.up, new Vector2(84, -47), new Vector2(width - 96, 24));
            float bottom = Fit(stats, 83);
            bottom = Fit(effect, bottom + 12);
            if (reactions.text.Length > 0) bottom = Fit(reactions, bottom + 12);
            bottom = Fit(training, bottom + 14);
            float height = bottom + 14;
            ObservatoryUi.Place(root, Vector2.one, topRightOffset + Vector2.down * Mathf.Max(0, regionHeight - height), new Vector2(width, height));
        }

        private float Fit(TMP_Text label, float top)
        {
            float contentWidth = width - 24;
            label.textWrappingMode = TextWrappingModes.Normal;
            float height = Mathf.Ceil(label.GetPreferredValues(label.text, contentWidth, float.PositiveInfinity).y);
            ObservatoryUi.Place(label.rectTransform, Vector2.up, new Vector2(12, -top), new Vector2(contentWidth, height));
            return top + height;
        }

        private static string DescribeEffect(ElementDefinition element) => element.Effect switch
        {
            EffectKind.Chill => $"냉기 · 공격한 적의 공격 속도를 {element.Duration:0.##}초 동안 {Mathf.Abs(element.Amount):0.#%} 낮춥니다.",
            EffectKind.Burn => $"화상 · 공격한 적에게 {element.Duration:0.##}초 동안 초당 {element.DamagePerSecond:0.##} 피해를 줍니다.",
            EffectKind.SmokeAura => $"연막 · 반경 {element.Radius:0.##}m 안에 있는 적의 사거리를 {Mathf.Abs(element.Amount):0.#%} 낮춥니다.",
            EffectKind.Wet => $"젖음 · 공격한 적을 {element.Duration:0.##}초 동안 젖게 합니다.",
            EffectKind.Chain => $"연쇄 · 반경 {element.Radius:0.##}m 안의 적을 최대 {element.ExtraTargets}기 더 공격해 기본 피해의 {element.Ratio:0.#%}를 줍니다.",
            EffectKind.Lifesteal => $"흡수 · 준 피해의 {element.Ratio:0.#%}만큼 자신의 체력을 회복합니다.",
            EffectKind.ShieldAura => $"보호막 · {element.Interval:0.##}초마다 반경 {element.Radius:0.##}m 안의 아군에게 {element.Absorb:0.##} 보호막을 줍니다.",
            EffectKind.HasteAura => $"가속 · 반경 {element.Radius:0.##}m 안의 아군 공격 속도를 {element.Amount:0.#%} 높입니다.",
            EffectKind.Splash => $"범위 공격 · 공격 대상 주변 {element.Radius:0.##}m 안의 적에게도 피해를 줍니다.",
            EffectKind.Pierce => $"관통 · 공격 경로에서 폭 {element.Width:0.##}m 안의 적을 최대 {element.MaxTargets}기 공격합니다.",
            EffectKind.Radiation => $"방사선 · 공격한 적에게 {element.Duration:0.##}초 동안 초당 {element.DamagePerSecond:0.##} 피해를 줍니다.",
            EffectKind.Heal => $"회복 · 가장 가까운 부상 아군의 체력을 {element.Heal:0.##} 회복합니다.",
            _ => "특수 효과 없음"
        };

        private string DescribeReactions(int element)
        {
            text.Clear();
            foreach (ReactionDefinition reaction in rules.Reactions)
            {
                if ((reaction.RequiredMask & (1UL << element)) == 0) continue;
                if (text.Length == 0) text.Append("시너지");
                text.Append('\n');
                bool add = false;
                for (int index = 0; index < names.Length; index++)
                {
                    if ((reaction.RequiredMask & (1UL << index)) == 0) continue;
                    if (add) text.Append(" + ");
                    text.Append(names[index]);
                    add = true;
                }
                text.Append(" · ").Append(reaction.Kind switch
                {
                    ReactionKind.Electrocute => "감전",
                    ReactionKind.Freeze => "빙결",
                    ReactionKind.Steam => "증기",
                    ReactionKind.BurnSpread => "화염 확산",
                    ReactionKind.Sandstorm => "모래폭풍",
                    _ => "플라즈마 폭발"
                });
            }
            return text.ToString();
        }
    }

    /// <summary>현재 렌더되는 대상과 마지막 시설 관측을 미리 보거나 카드에 고정한다.</summary>
    internal sealed class WorldInfoCards
    {
        private readonly BuildingTooltip buildings;
        private readonly ElementTooltip elements;
        private ObservedEntityId target, dismissed;
        private bool pinned;

        internal HudPanelKind Kind => target.Id == 0 ? HudPanelKind.None : target.Kind == ObservedEntityKind.Building
            ? HudPanelKind.BuildingInfo : HudPanelKind.ElementInfo;

        internal WorldInfoCards(ObservatoryUi ui, RectTransform parent, GameRules rules, string[] names)
        {
            buildings = new BuildingTooltip(ui, parent, rules, names);
            buildings.ConfigureCard(ui, () => Close());
            elements = new ElementTooltip(ui, parent, rules, names, () => Close());
        }

        internal void ConfigureWorld(GameRules rules)
        {
            Hide();
            buildings.ConfigureWorld(rules);
            elements.ConfigureWorld(rules);
        }

        internal void Layout(float width, float height, Vector2 topRightOffset)
        {
            buildings.Layout(width, height, topRightOffset);
            elements.Layout(width, height, topRightOffset);
        }

        internal bool Close()
        {
            if (target.Id == 0) return false;
            dismissed = target;
            ClearCard();
            return true;
        }

        internal void Hide()
        {
            dismissed = default;
            ClearCard();
        }

        private void ClearCard()
        {
            target = default;
            pinned = false;
            buildings.Hide();
            elements.Hide();
        }

        internal void TickPointer(ElementalVisuals visuals, GameplayFrame observation, Vector2 cursor, Ray? ray, Transform uiTarget,
            bool click, float towerRange)
        {
            var current = observation.SpatialReady ? observation.Buildings : default;
            BuildingView building = default;
            RememberedBuilding remembered = default;
            int element = -1;
            ObservedEntityId candidate = default;
            bool overCard = buildings.Contains(uiTarget) || elements.Contains(uiTarget);
            if (pinned || overCard)
            {
                candidate = target;
                if (target.Kind == ObservedEntityKind.Building)
                {
                    foreach (BuildingView item in current)
                        if (item.Id == target.Id) { building = item; break; }
                    if (building.Id == 0 && !visuals.TryGetRememberedBuilding(target.Id, out remembered)) { Hide(); return; }
                }
                else if (!observation.SpatialReady || !visuals.TryGetRenderedElement(target.Id, out element)) { Hide(); return; }
            }
            else if (ray.HasValue)
            {
                bool hasBuilding = visuals.TryHoverBuilding(current, ray.Value, out building);
                if (observation.SpatialReady && (!hasBuilding || building.Kind == BuildingKind.Dome) &&
                    visuals.TryHoverElement(ray.Value, out int unitId, out element))
                    candidate = new ObservedEntityId(ObservedEntityKind.Unit, unitId);
                else if (hasBuilding)
                    candidate = new ObservedEntityId(ObservedEntityKind.Building, building.Id);
                else if (visuals.TryHoverRememberedBuilding(ray.Value, out remembered))
                    candidate = new ObservedEntityId(ObservedEntityKind.Building, remembered.Building.Id);
            }
            if (candidate.Id == 0) { Hide(); return; }
            if (candidate.Equals(dismissed)) return;
            dismissed = default;
            target = candidate;
            pinned |= click && (ray.HasValue || overCard);
            if (target.Kind == ObservedEntityKind.Building)
            {
                elements.Hide();
                if (remembered.Building.Id != 0) buildings.ShowLastSeen(remembered.Building, remembered.LastSeenTick);
                else buildings.ShowWorld(building, observation.Tick, cursor);
                buildings.SetPinned(pinned);
                if (building.Id != 0 && BuildingTooltip.IsTower(building.Kind)) visuals.RenderTowerRange(building, towerRange);
            }
            else
            {
                buildings.Hide();
                elements.Show(element, observation.MainElement);
                elements.SetPinned(pinned);
            }
        }
    }
}
