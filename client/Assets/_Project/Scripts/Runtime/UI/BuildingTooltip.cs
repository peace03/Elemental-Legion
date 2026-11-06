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
        private readonly GameRules rules;
        private readonly string[] elements;
        private readonly StringBuilder text = new StringBuilder();
        private BuildingKind lastKind;
        private int lastEntity, lastElement, lastRemaining;
        private string lastAvailability;

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
            Show(building.Kind, building.TowerElement, building.Id, remaining, null, cursor);
        }

        internal void Hide() => root.gameObject.SetActive(false);

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
                detail.text = kind switch
                {
                    BuildingKind.NatureForge => "자연 원소를 양성해 본진 수비대로 보냅니다.",
                    BuildingKind.EnergyForge => "에너지 원소를 양성해 본진 수비대로 보냅니다.",
                    BuildingKind.Collector => $"반경 {rules.CollectRadius:0.#}m 안에 떨어진 핵을 자동으로 수집합니다.",
                    BuildingKind.NatureTower or BuildingKind.EnergyTower => $"타워 중심 {rules.TowerRange:0.#}m 안의 가장 가까운 적 또는 중립 원소를 공격합니다.",
                    BuildingKind.Dome => "본진에 들어오는 공격을 먼저 받아 본진을 보호합니다.",
                    _ => "시설을 지을 수 있는 중심입니다. 파괴되면 패배합니다."
                };
                if (entity == 0)
                {
                    text.Clear().Append(rules.BuildingCost(kind)).Append("핵   ·   건설 ");
                    text.Append(rules.BuildingConstructionSeconds(kind)).Append("초");
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

        internal static string Name(BuildingKind kind) => kind switch
        {
            BuildingKind.NatureForge => "응집로",
            BuildingKind.EnergyForge => "점화로",
            BuildingKind.Collector => "포집기",
            BuildingKind.NatureTower => "자연 타워",
            BuildingKind.EnergyTower => "에너지 타워",
            BuildingKind.Dome => "자기권",
            _ => "본진"
        };
    }
}
