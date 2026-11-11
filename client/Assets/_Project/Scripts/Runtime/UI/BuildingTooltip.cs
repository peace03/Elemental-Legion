using System;
using System.Text;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;

namespace ElementalLegion.Client.UI
{
    /// <summary>전장과 건설 메뉴가 공유하는 시설 설명이다.</summary>
    internal sealed class BuildingTooltip
    {
        private readonly RectTransform root, parent;
        private readonly TMP_Text title, detail, status;
        private GameRules rules;
        private readonly string[] elements;
        private readonly StringBuilder text = new StringBuilder();
        private BuildingKind lastKind;
        private int lastEntity, lastElement, lastRemaining;
        private string lastAvailability;
        private UnityEngine.UI.Image icon;
        private UnityEngine.UI.Button close;
        private ObservatoryUi cardUi;
        private float lastHealth, lastMaxHealth;
        private bool lastGhost, lastRemembered, lastUnderConstruction;
        private float cardWidth = 348, regionHeight = 465;
        private Vector2 topRightOffset = new Vector2(-16, -70);

        internal BuildingTooltip(ObservatoryUi ui, RectTransform parent, GameRules rules, string[] elements)
        {
            this.parent = parent;
            this.rules = rules;
            this.elements = elements;
            root = ObservatoryUi.Panel(parent, "Building tooltip", 0, 0, 306, 102, ObservatoryUi.Surface);
            ObservatoryUi.Place(root, parent.pivot, Vector2.zero, root.sizeDelta);
            root.pivot = Vector2.up;
            title = ui.Label(root, "Building name", "", 17, 12, 6, 282, 23, ObservatoryUi.Gold);
            detail = ui.Label(root, "Building function", "", 15, 12, 29, 282, 42);
            status = ui.Label(root, "Building status", "", 14, 12, 73, 282, 22, ObservatoryUi.Accent);
            foreach (TMP_Text label in new[] { title, detail, status })
            {
                label.textWrappingMode = TextWrappingModes.Normal;
                label.overflowMode = TextOverflowModes.Overflow;
            }
            Hide();
        }

        internal void ShowMenu(BuildingKind kind, int element, string availability, Vector2 cursor)
            => Show(kind, element, 0, 0, availability, cursor);

        internal void ShowWorld(BuildingView building, long tick, Vector2 cursor)
        {
            int remaining = building.IsUnderConstruction
                ? (int)Math.Ceiling(Math.Max(0, building.ConstructionEndTick - tick) * rules.DeltaTime) : -1;
            if (close != null)
            {
                ShowWorldCard(building, remaining, building.GhostEndTick > tick, false);
                return;
            }
            Show(building.Kind, building.TowerElement, building.Id, remaining, null, cursor);
        }

        internal void ShowLastSeen(BuildingView building, long lastSeenTick)
            => ShowWorldCard(building, -1, building.GhostEndTick > lastSeenTick, true);

        internal void ConfigureCard(ObservatoryUi ui, Action closeRequested)
        {
            cardUi = ui;
            root.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            icon = ui.Icon(root, "Building icon", null, 12, 9, 60, 60);
            close = ui.Button(root, "×", 306, 10, 30, 28, closeRequested);
            TMP_Text closeLabel = close.GetComponentInChildren<TMP_Text>();
            ObservatoryUi.Fill(closeLabel.rectTransform);
            closeLabel.overflowMode = TextOverflowModes.Overflow;
            title.color = ObservatoryUi.TextColor;
            title.fontSize = 20;
            Hide();
        }

        internal void ConfigureWorld(GameRules worldRules)
        {
            rules = worldRules;
            Hide();
        }

        internal void Layout(float width, float height, Vector2 offset)
        {
            cardWidth = width;
            regionHeight = height;
            topRightOffset = offset;
            ReflowCard();
        }

        internal void SetPinned(bool pinned)
        {
            if (close == null) return;
            close.gameObject.SetActive(pinned);
        }

        internal bool Contains(Transform item) => item != null && item.IsChildOf(root);

        internal void Hide()
        {
            SetPinned(false);
            root.gameObject.SetActive(false);
        }

        private void ShowWorldCard(BuildingView building, int remaining, bool ghost, bool remembered)
        {
            if (building.Id != lastEntity || building.Kind != lastKind || building.TowerElement != lastElement ||
                building.Health != lastHealth || building.MaxHealth != lastMaxHealth || remaining != lastRemaining ||
                ghost != lastGhost || remembered != lastRemembered || building.IsUnderConstruction != lastUnderConstruction ||
                !root.gameObject.activeSelf)
            {
                root.gameObject.SetActive(true);
                lastEntity = building.Id;
                lastKind = building.Kind;
                lastElement = building.TowerElement;
                lastHealth = building.Health;
                lastMaxHealth = building.MaxHealth;
                lastRemaining = remaining;
                lastGhost = ghost;
                lastRemembered = remembered;
                lastUnderConstruction = building.IsUnderConstruction;
                title.text = IsTower(building.Kind) && building.TowerElement >= 0 && building.TowerElement < elements.Length
                    ? $"{Name(building.Kind)}\n<size=14>{elements[building.TowerElement]}</size>" : Name(building.Kind);
                icon.sprite = cardUi.Art.Building(building.Kind);
                detail.text = Function(rules, building.Kind);
                text.Clear();
                if (remembered) text.Append("마지막 관측\n");
                text.Append("체력 ").AppendFormat("{0:0.##} / {1:0.##}", building.Health, building.MaxHealth);
                if (building.Kind != BuildingKind.Base) text.Append('\n').Append(BuildTerms(rules, building.Kind));
                if (remaining >= 0) text.Append("\n건설 중 · ").Append(remaining).Append("초");
                else if (remembered && building.IsUnderConstruction) text.Append("\n건설 중");
                else text.Append(ghost ? "\n유령 상태" : "\n가동 중");
                status.SetText(text);
                ReflowCard();
            }
        }

        private void ReflowCard()
        {
            ObservatoryUi.Place(root, Vector2.one, topRightOffset, new Vector2(cardWidth, regionHeight));
            ObservatoryUi.Place((RectTransform)close.transform, Vector2.up, new Vector2(cardWidth - 42, -10), new Vector2(30, 28));
            ObservatoryUi.Place(title.rectTransform, Vector2.up, new Vector2(82, -9), new Vector2(cardWidth - 134, 60));
            float bottom = FitText(detail, 77);
            bottom = FitText(status, bottom + 12);
            float height = bottom + 14;
            ObservatoryUi.Place(root, Vector2.one, topRightOffset + Vector2.down * Mathf.Max(0, regionHeight - height), new Vector2(cardWidth, height));
        }

        private void Show(BuildingKind kind, int element, int entity, int remaining, string availability, Vector2 cursor)
        {
            if (kind != lastKind || entity != lastEntity || element != lastElement || remaining != lastRemaining ||
                availability != lastAvailability || !root.gameObject.activeSelf)
            {
                lastKind = kind;
                lastEntity = entity;
                lastElement = element;
                lastRemaining = remaining;
                lastAvailability = availability;
                string name = Name(kind);
                title.text = IsTower(kind) && element >= 0 && element < elements.Length
                    ? $"{name} · {elements[element]}" : name;
                detail.text = Function(rules, kind);
                if (entity == 0)
                {
                    text.Clear().Append(BuildTerms(rules, kind));
                    if (availability != null) text.Append("   ·   ").Append(availability);
                    status.SetText(text);
                }
                else if (remaining >= 0) status.SetText("건설 중   ·   {0}초", (float)remaining);
                else status.text = string.Empty;
                float bottom = FitText(detail, FitText(title, 6) + 2);
                bottom = FitText(status, bottom + (status.text.Length > 0 ? 6 : 0));
                root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, bottom + 10);
                root.SetAsLastSibling();
            }
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, cursor, null, out Vector2 local)) return;
            Rect bounds = parent.rect;
            Vector2 size = root.sizeDelta;
            float x = local.x + 14 + size.x <= bounds.xMax ? local.x + 14 : local.x - 14 - size.x;
            float y = local.y - 16 - size.y >= bounds.yMin ? local.y - 16 : local.y + 16 + size.y;
            root.anchoredPosition = new Vector2(Mathf.Clamp(x, bounds.xMin, bounds.xMax - size.x),
                Mathf.Clamp(y, bounds.yMin + size.y, bounds.yMax));
            root.gameObject.SetActive(true);
        }

        private float FitText(TMP_Text label, float top)
        {
            float width = root.rect.width - 24;
            float height = label.text.Length == 0 ? 0 : Mathf.Ceil(label.GetPreferredValues(label.text, width, float.PositiveInfinity).y);
            ObservatoryUi.Place(label.rectTransform, Vector2.up, new Vector2(12, -top), new Vector2(width, height));
            return top + height;
        }

        internal static bool IsTower(BuildingKind kind) => kind == BuildingKind.NatureTower || kind == BuildingKind.EnergyTower;

        internal static string BuildTerms(GameRules rules, BuildingKind kind)
            => $"건설 {rules.BuildingCost(kind)}핵 · {rules.BuildingConstructionSeconds(kind):0.##}초";

        internal static string Function(GameRules rules, BuildingKind kind) => kind switch
        {
            BuildingKind.NatureForge => "자연 원소를 양성합니다.",
            BuildingKind.EnergyForge => "에너지 원소를 양성합니다.",
            BuildingKind.Collector => $"반경 {rules.CollectRadius:0.##}m 안에 떨어진 핵을 자동으로 수집합니다.",
            BuildingKind.NatureTower or BuildingKind.EnergyTower => $"반경 {rules.TowerRange:0.##}m 안의 가장 가까운 적 또는 중립 원소를 공격합니다.",
            BuildingKind.Dome => "원시핵에 들어오는 공격을 먼저 받아 본진을 보호합니다.",
            _ => $"시설을 반경 {rules.BuildRadius:0.##}m 안에 지을 수 있습니다. 원시핵이 파괴되면 패배합니다."
        };

        internal static string Name(BuildingKind kind) => kind switch
        {
            BuildingKind.NatureForge => "응집로",
            BuildingKind.EnergyForge => "점화로",
            BuildingKind.Collector => "포집기",
            BuildingKind.NatureTower => "자연 수호 위성",
            BuildingKind.EnergyTower => "에너지 수호 위성",
            BuildingKind.Dome => "자기권",
            _ => "원시핵"
        };
    }
}
