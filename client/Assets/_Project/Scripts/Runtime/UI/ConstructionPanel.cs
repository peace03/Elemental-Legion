using System;
using System.Collections.Generic;
using System.Text;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>시설 선택과 배치 미리보기를 관리하고 건설 의도를 전달한다.</summary>
    internal sealed class ConstructionPanel
    {
        private static readonly BuildingKind[] Kinds =
        {
            BuildingKind.NatureForge, BuildingKind.Collector, BuildingKind.NatureTower,
            BuildingKind.EnergyForge, BuildingKind.EnergyTower, BuildingKind.Dome
        };

        private GameRules rules;
        private readonly RectTransform panel;
        private readonly Button[] build = new Button[Kinds.Length];
        private readonly TMP_Text[] buildLabels = new TMP_Text[Kinds.Length];
        private readonly int[] counts = new int[Kinds.Length];
        private readonly int[][] towerElements = new int[2][];
        private readonly TMP_Dropdown[] towerOptions = new TMP_Dropdown[2];
        private readonly string[] availability = new string[Kinds.Length];
        private readonly BuildingTooltip tooltip;
        private readonly Button upgrade;
        private readonly TMP_Text upgradeLabel, heading, status;
        private readonly Action<BuildingKind, int, int, int> request;
        private readonly List<RaycastResult> pointerHits = new List<RaycastResult>();
        private readonly StringBuilder upgradeBenefits = new StringBuilder();
        private EventSystem pointerSystem;
        private PointerEventData pointer;
        private BuildingKind? selected;
        private GameplayFrame observation;
        internal bool IsOpen => panel.gameObject.activeSelf;

        internal void ConfigureWorld(GameRules worldRules) => rules = worldRules;

        internal ConstructionPanel(ObservatoryUi ui, Transform parent, GameRules rules, string[] elements,
            Action<BuildingKind, int, int, int> request, Action upgradeRequested)
        {
            this.rules = rules;
            this.request = request;
            panel = ObservatoryUi.Panel(parent, "Construction", 0, 0, 460, 314, ObservatoryUi.Surface, true);
            ObservatoryUi.Place(panel, Vector2.right, new Vector2(-16, 212), new Vector2(460, 314));
            heading = ui.Label(panel, "Heading", "시설 건설", 19, 12, 7, 320, 29, ObservatoryUi.Gold);
            ui.Button(panel, "닫기", 348, 5, 100, 32, () => SetOpen(false));
            for (int index = 0; index < Kinds.Length; index++)
            {
                BuildingKind kind = Kinds[index];
                build[index] = ui.Button(panel, BuildingTooltip.Name(kind), 12 + index % 2 * 222, 42 + index / 2 * 40, 214, 36,
                    () => Select(kind));
                buildLabels[index] = build[index].GetComponentInChildren<TMP_Text>();
                buildLabels[index].fontSize = 14;
            }
            AddTowerOptions(ui, elements, ElementGroup.Nature, "자연 타워 원소", 12);
            AddTowerOptions(ui, elements, ElementGroup.Energy, "에너지 타워 원소", 234);
            upgrade = ui.Button(panel, "기지 업그레이드", 12, 235, 436, 36, upgradeRequested, true);
            upgradeLabel = upgrade.GetComponentInChildren<TMP_Text>();
            upgradeLabel.fontSize = 15;
            status = ui.Label(panel, "Construction status", "", 14, 14, 274, 430, 34, ObservatoryUi.Accent);
            tooltip = new BuildingTooltip(ui, (RectTransform)parent, rules, elements);
            panel.gameObject.SetActive(false);
        }

        private void AddTowerOptions(ObservatoryUi ui, string[] names, ElementGroup group, string label, int x)
        {
            var indices = new List<int>();
            var options = new List<string>();
            for (int index = 0; index < rules.Elements.Count; index++)
            {
                if (rules.Elements[index].Group != group) continue;
                indices.Add(index);
                options.Add(names[index]);
            }
            towerElements[(int)group] = indices.ToArray();
            ui.Label(panel, label, label, 14, x + 2, 164, 208, 24, ObservatoryUi.Muted);
            towerOptions[(int)group] = ui.Dropdown(panel, options.ToArray(), 0, x, 190, 214);
        }

        private int SelectedTowerElement(BuildingKind kind)
        {
            int group = kind == BuildingKind.NatureTower ? (int)ElementGroup.Nature : (int)ElementGroup.Energy;
            return BuildingTooltip.IsTower(kind) ? towerElements[group][towerOptions[group].value] : -1;
        }

        internal void SetOpen(bool open)
        {
            if (!open) { CancelPlacement(); tooltip.Hide(); }
            panel.gameObject.SetActive(open);
            if (open) RefreshControls();
        }

        internal void Observe(GameplayFrame frame)
        {
            if (observation == null && frame.Tick >= 0)
            {
                int[] nature = towerElements[(int)ElementGroup.Nature];
                int main = Array.IndexOf(nature, frame.MainElement);
                if (main >= 0) towerOptions[(int)ElementGroup.Nature].SetValueWithoutNotify(main);
            }
            observation = frame;
            if (IsOpen) RefreshControls();
        }

        internal void Clear()
        {
            observation = null;
            SetOpen(false);
            status.text = string.Empty;
        }

        internal bool CancelPlacement()
        {
            if (!selected.HasValue) return false;
            selected = null;
            status.text = "배치 취소";
            return true;
        }

        private void Select(BuildingKind kind)
        {
            selected = kind;
            status.text = kind == BuildingKind.Dome
                ? "자기권은 본진에 설치됩니다. 좌클릭으로 확정"
                : "좌클릭으로 배치 · 우클릭 또는 메뉴 키로 취소";
        }

        internal void TickPointer(ElementalVisuals visuals, bool inputAllowed)
        {
            Mouse mouse = Mouse.current;
            if (!inputAllowed || mouse == null || observation == null)
            {
                tooltip.Hide();
                return;
            }
            Vector2 cursor = mouse.position.ReadValue();
            if (mouse.rightButton.wasPressedThisFrame && CancelPlacement()) { tooltip.Hide(); return; }
            UnityEngine.Camera camera = visuals.TargetCamera;
            Transform uiTarget = FindUiTarget(cursor);
            if (uiTarget != null)
            {
                if (IsOpen && uiTarget.IsChildOf(panel))
                    for (int index = 0; index < Kinds.Length; index++)
                        if (uiTarget.IsChildOf(build[index].transform))
                        {
                            BuildingKind hovered = Kinds[index];
                            tooltip.ShowMenu(hovered, SelectedTowerElement(hovered), availability[index], cursor);
                            return;
                        }
                tooltip.Hide();
                return;
            }
            if (!observation.SpatialReady || !camera.pixelRect.Contains(cursor)) { tooltip.Hide(); return; }
            Ray ray = camera.ScreenPointToRay(cursor);
            if (!selected.HasValue)
            {
                if (visuals.TryHoverBuilding(observation.Buildings, ray, out BuildingView building))
                {
                    tooltip.ShowWorld(building, observation.Tick, cursor);
                    if (BuildingTooltip.IsTower(building.Kind))
                        visuals.RenderTowerRange(building, rules.TowerRange);
                }
                else tooltip.Hide();
                return;
            }
            tooltip.Hide();
            PlaceAtPointer(visuals, selected.Value, ray, mouse.leftButton.wasPressedThisFrame);
        }

        private void PlaceAtPointer(ElementalVisuals visuals, BuildingKind kind, Ray ray, bool confirm)
        {
            int cellX = 0, cellZ = 0;
            Vector3 center;
            if (kind == BuildingKind.Dome)
                center = new Vector3(observation.BasePosition.X, 0, observation.BasePosition.Y);
            else
            {
                if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance)) return;
                center = Snap(ray.GetPoint(distance), rules.BuildGridSize, out cellX, out cellZ);
            }
            float radius = rules.BuildingRadius(kind);
            var point = new System.Numerics.Vector2(center.x, center.z);
            float reach = rules.BuildRadius - radius;
            bool valid = reach >= 0 && center.x >= radius && center.z >= radius &&
                center.x <= rules.MapSize - radius && center.z <= rules.MapSize - radius &&
                System.Numerics.Vector2.DistanceSquared(point, observation.BasePosition) <= reach * reach;
            if (kind != BuildingKind.Dome)
                foreach (BuildingView building in observation.Buildings)
                {
                    float clearance = radius + building.Radius;
                    if (building.BlocksMovement &&
                        System.Numerics.Vector2.DistanceSquared(point, building.Position) < clearance * clearance)
                        valid = false;
                }
            int element = SelectedTowerElement(kind);
            visuals.RenderConstructionPreview(kind, element, center, radius, rules.TowerRange, valid);
            if (!confirm) return;
            if (center.x < 0 || center.z < 0 || center.x > rules.MapSize || center.z > rules.MapSize)
            {
                status.text = "지도 안에서 배치 위치를 고르세요.";
                return;
            }
            selected = null;
            status.text = "건설 명령 확인 중…";
            request(kind, cellX, cellZ, element);
        }

        internal static Vector3 Snap(Vector3 point, float gridSize, out int cellX, out int cellZ)
        {
            cellX = Mathf.RoundToInt(point.x / gridSize);
            cellZ = Mathf.RoundToInt(point.z / gridSize);
            return new Vector3(cellX * gridSize, 0, cellZ * gridSize);
        }

        private Transform FindUiTarget(Vector2 screenPosition)
        {
            EventSystem system = EventSystem.current;
            if (system == null) return null;
            if (pointerSystem != system)
            {
                pointerSystem = system;
                pointer = new PointerEventData(system);
            }
            pointer.position = screenPosition;
            system.RaycastAll(pointer, pointerHits);
            Transform target = null;
            foreach (RaycastResult result in pointerHits)
                if (result.module is GraphicRaycaster) { target = result.gameObject.transform; break; }
            pointerHits.Clear();
            return target;
        }

        internal void SetResult(string message) => status.text = message;

        private void RefreshControls()
        {
            if (observation == null || observation.Tick < 0)
            {
                foreach (Button button in build) button.interactable = false;
                upgrade.interactable = false;
                return;
            }
            Array.Clear(counts, 0, counts.Length);
            int towers = 0;
            foreach (BuildingView building in observation.Buildings)
            {
                if (building.OwnerId != observation.PlayerId) continue;
                int index = Array.IndexOf(Kinds, building.Kind);
                if (index >= 0) counts[index]++;
                if (BuildingTooltip.IsTower(building.Kind)) towers++;
            }
            int slots = rules.TowerSlots(observation.BaseLevel);
            heading.text = $"시설 건설   ·   본진 {observation.BaseLevel}단계   ·   타워 {towers}/{slots}";
            for (int index = 0; index < Kinds.Length; index++)
            {
                BuildingKind kind = Kinds[index];
                bool isTower = BuildingTooltip.IsTower(kind);
                int cost = rules.BuildingCost(kind);
                bool unlocked = rules.IsBuildingUnlocked(observation.BaseLevel, kind);
                bool full = isTower ? towers >= slots : counts[index] > 0;
                build[index].interactable = unlocked && !full && observation.CoreBalance >= cost;
                availability[index] = !unlocked ? "잠김" : full ? (isTower ? "슬롯 제한" : "설치됨") :
                    observation.CoreBalance < cost ? "핵 부족" : null;
                string suffix = availability[index] == null ? string.Empty : $" · {availability[index]}";
                buildLabels[index].text = $"{BuildingTooltip.Name(kind)} {cost}핵{suffix}";
            }
            int next = observation.BaseLevel + 1;
            if (next > rules.MaxBaseLevel)
            {
                upgradeLabel.text = "최고 본진 레벨";
                upgrade.interactable = false;
                return;
            }
            int upgradeCost = rules.UpgradeCost(next);
            upgradeBenefits.Clear().Append(next).Append("단계 업그레이드   ").Append(upgradeCost).Append("핵");
            bool hasUnlock = false;
            foreach (BuildingKind kind in Kinds)
                if (rules.IsBuildingUnlocked(next, kind) && !rules.IsBuildingUnlocked(observation.BaseLevel, kind))
                {
                    upgradeBenefits.Append(hasUnlock ? "·" : "   ·   ").Append(BuildingTooltip.Name(kind));
                    hasUnlock = true;
                }
            if (!hasUnlock) upgradeBenefits.Append("   ·   타워 슬롯 ").Append(rules.TowerSlots(next));
            upgradeLabel.text = upgradeBenefits.ToString();
            upgrade.interactable = observation.CoreBalance >= upgradeCost;
        }

    }
}
