using System;
using System.Collections.Generic;
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
            BuildingKind.Collector, BuildingKind.NatureForge, BuildingKind.NatureTower,
            BuildingKind.EnergyTower, BuildingKind.EnergyForge, BuildingKind.Dome
        };

        private GameRules rules;
        private readonly ObservatoryUi ui;
        private readonly RectTransform panel;
        private readonly Button[] build = new Button[Kinds.Length];
        private readonly TMP_Text[] buildLabels = new TMP_Text[Kinds.Length];
        private readonly int[] counts = new int[Kinds.Length];
        private readonly int[] previousCounts = new int[Kinds.Length];
        private readonly int[][] towerElements = new int[2][];
        private readonly TMP_Dropdown[] towerOptions = new TMP_Dropdown[2];
        private readonly TMP_Text[] towerOptionLabels = new TMP_Text[2];
        private readonly RectTransform[] facilityIcons = new RectTransform[Kinds.Length];
        private readonly Button close;
        private readonly string[] availability = new string[Kinds.Length];
        private readonly WorldInfoCards worldInfo;
        private readonly Button place;
        private readonly TMP_Text placeLabel, heading, detail, status;
        private readonly Action<BuildingKind, int, int, int> request;
        private readonly List<RaycastResult> pointerHits = new List<RaycastResult>();
        private EventSystem pointerSystem;
        private PointerEventData pointer;
        private BuildingKind? chosen, hovered, selected;
        private GameplayFrame observation;
        private int previousLevel = -1;
        private long previousCores;
        internal bool IsOpen => panel.gameObject.activeSelf;
        internal HudPanelKind WorldInfoKind => worldInfo.Kind;

        internal void ConfigureWorld(GameRules worldRules)
        {
            rules = worldRules;
            worldInfo.ConfigureWorld(worldRules);
            Clear();
        }

        internal ConstructionPanel(ObservatoryUi ui, Transform parent, GameRules rules, string[] elements,
            Action<BuildingKind, int, int, int> request, Action upgradeRequested, Action closeRequested = null,
            Action<int, FacilityUpgradeBranch, byte> upgradeFacility = null)
        {
            this.ui = ui;
            this.rules = rules;
            this.request = request;
            panel = ObservatoryUi.Panel(parent, "Construction", 0, 0, 348, 465, ObservatoryUi.Surface, true);
            heading = ui.Label(panel, "Heading", "건설", 18, 12, 7, 280, 29, ObservatoryUi.Gold);
            close = ui.Button(panel, "×", 306, 8, 30, 28, closeRequested ?? (() => SetOpen(false)));
            TMP_Text closeLabel = close.GetComponentInChildren<TMP_Text>();
            ObservatoryUi.Fill(closeLabel.rectTransform);
            closeLabel.overflowMode = TextOverflowModes.Overflow;
            for (int index = 0; index < Kinds.Length; index++)
            {
                BuildingKind kind = Kinds[index];
                build[index] = ui.Button(panel, BuildingTooltip.Name(kind), 0, 0, 158, 50,
                    () => Select(kind));
                buildLabels[index] = build[index].GetComponentInChildren<TMP_Text>();
                buildLabels[index].fontSize = 13;
                buildLabels[index].alignment = TextAlignmentOptions.MidlineLeft;
                facilityIcons[index] = ui.Icon(build[index].transform, "Facility icon", ui.Art.Building(kind), 0, 0, 38, 38).rectTransform;
            }
            AddTowerOptions(ui, elements, ElementGroup.Nature, "자연 원소");
            AddTowerOptions(ui, elements, ElementGroup.Energy, "에너지 원소");
            detail = ui.Label(panel, "Facility description", "", 14, 12, 267, 324, 64);
            detail.textWrappingMode = TextWrappingModes.Normal;
            place = ui.Button(panel, "배치 시작", 12, 380, 324, 34, BeginOrCancelPlacement, true);
            placeLabel = place.GetComponentInChildren<TMP_Text>();
            placeLabel.fontSize = 15;
            status = ui.Label(panel, "Construction status", "", 13, 12, 421, 324, 34, ObservatoryUi.Accent);
            status.textWrappingMode = TextWrappingModes.Normal;
            worldInfo = new WorldInfoCards(ui, (RectTransform)parent, rules, elements, upgradeRequested, upgradeFacility);
            Layout(348, 465, new Vector2(-16, -70));
            panel.gameObject.SetActive(false);
        }

        /// <summary>관리 창과 소개 카드를 HUD의 같은 오른쪽 영역에 맞춘다.</summary>
        internal void Layout(float width, float height, Vector2 topRightOffset, float? cardBottomOffset = null)
        {
            ObservatoryUi.Place(panel, Vector2.one, topRightOffset, new Vector2(width, height));
            ObservatoryUi.Place(heading.rectTransform, Vector2.up, new Vector2(12, -7), new Vector2(width - 64, 29));
            ObservatoryUi.Place((RectTransform)close.transform, Vector2.up, new Vector2(width - 42, -8), new Vector2(30, 28));
            float cellWidth = (width - 32) * 0.5f;
            for (int index = 0; index < build.Length; index++)
            {
                ObservatoryUi.Place((RectTransform)build[index].transform, Vector2.up,
                    new Vector2(12 + index % 2 * (cellWidth + 8), -42 - index / 2 * 55), new Vector2(cellWidth, 49));
                ObservatoryUi.Place(facilityIcons[index], Vector2.up, new Vector2(6, -5), new Vector2(38, 38));
                ObservatoryUi.Place(buildLabels[index].rectTransform, Vector2.up, new Vector2(49, -2), new Vector2(cellWidth - 54, 45));
            }
            for (int index = 0; index < towerOptions.Length; index++)
            {
                ObservatoryUi.Place(towerOptionLabels[index].rectTransform, Vector2.up, new Vector2(12, -333), new Vector2(92, 31));
                ObservatoryUi.Place((RectTransform)towerOptions[index].transform, Vector2.up, new Vector2(108, -333), new Vector2(width - 120, 31));
            }
            ObservatoryUi.Place(detail.rectTransform, Vector2.up, new Vector2(12, -266), new Vector2(width - 24, 64));
            ObservatoryUi.Place((RectTransform)place.transform, Vector2.up, new Vector2(12, 72 - height), new Vector2(width - 24, 33));
            ObservatoryUi.Place(placeLabel.rectTransform, Vector2.up, Vector2.zero, new Vector2(width - 24, 33));
            ObservatoryUi.Place(status.rectTransform, Vector2.up, new Vector2(12, 36 - height), new Vector2(width - 24, 32));
            var cardOffset = topRightOffset;
            if (cardBottomOffset.HasValue) cardOffset.y = cardBottomOffset.Value + height;
            worldInfo.Layout(width, height, cardOffset);
        }

        private void AddTowerOptions(ObservatoryUi ui, string[] names, ElementGroup group, string label)
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
            towerOptionLabels[(int)group] = ui.Label(panel, label, label, 14, 12, 333, 92, 31, ObservatoryUi.Muted);
            towerOptions[(int)group] = ui.Dropdown(panel, options.ToArray(), 0, 108, 333, 228);
        }

        private int SelectedTowerElement(BuildingKind kind)
        {
            int group = kind == BuildingKind.NatureTower ? (int)ElementGroup.Nature : (int)ElementGroup.Energy;
            return BuildingTooltip.IsTower(kind) ? towerElements[group][towerOptions[group].value] : -1;
        }

        internal void SetOpen(bool open)
        {
            if (!open) { CancelPlacement(); hovered = null; }
            else HideWorldInfo();
            panel.gameObject.SetActive(open);
            if (open)
            {
                previousLevel = -1;
                RefreshControls();
            }
        }

        internal bool CloseWorldInfo() => worldInfo.Close();
        internal void HideWorldInfo() => worldInfo.Hide();

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
            previousLevel = -1;
            SetOpen(false);
            HideWorldInfo();
            ClearSelection();
            status.text = string.Empty;
            worldInfo.SetResult(string.Empty, false);
        }

        internal bool CancelPlacement()
        {
            if (!selected.HasValue) return false;
            selected = null;
            placeLabel.text = "배치 시작";
            status.text = "배치 취소";
            return true;
        }

        private void Select(BuildingKind kind)
        {
            if (!IsOpen || observation == null) return;
            if (chosen == kind && selected.HasValue) return;
            if (chosen != kind)
            {
                ClearSelection();
                chosen = kind;
                ui.StyleButton(build[Array.IndexOf(Kinds, kind)], true);
            }
            status.text = string.Empty;
            RefreshDetail();
        }

        private void ClearSelection()
        {
            CancelPlacement();
            if (chosen.HasValue) ui.StyleButton(build[Array.IndexOf(Kinds, chosen.Value)]);
            chosen = null;
            hovered = null;
        }

        private void BeginOrCancelPlacement()
        {
            if (CancelPlacement()) return;
            if (!IsOpen || !chosen.HasValue || !place.interactable) return;
            selected = chosen;
            placeLabel.text = "배치 취소";
            status.text = selected == BuildingKind.Dome
                ? "자기권은 본진에 설치됩니다. 전장을 좌클릭해 확정"
                : "전장 좌클릭으로 배치 · 우클릭 또는 메뉴 키로 취소";
        }

        internal void TickPointer(ElementalVisuals visuals, bool inputAllowed, bool worldInfoAllowed = true)
        {
            Mouse mouse = Mouse.current;
            if (!inputAllowed || mouse == null || observation == null || visuals == null)
            {
                CancelPlacement();
                HideWorldInfo();
                return;
            }
            Vector2 cursor = mouse.position.ReadValue();
            UnityEngine.Camera camera = visuals.TargetCamera;
            Transform uiTarget = FindUiTarget(cursor);
            Ray? ray = uiTarget == null && camera != null && camera.pixelRect.Contains(cursor)
                ? camera.ScreenPointToRay(cursor) : (Ray?)null;
            bool click = mouse.leftButton.wasPressedThisFrame;
            if (IsOpen)
            {
                if (!observation.SpatialReady) { CancelPlacement(); return; }
                visuals.RenderConstructionRange(observation.BasePosition, rules.BuildRadius);
                BuildingKind? hover = null;
                if (!selected.HasValue && uiTarget != null && uiTarget.IsChildOf(panel))
                    for (int index = 0; index < Kinds.Length; index++)
                        if (uiTarget.IsChildOf(build[index].transform))
                        {
                            hover = Kinds[index];
                            break;
                        }
                if (hover != hovered) { hovered = hover; RefreshDetail(); }
                if (selected.HasValue && ray.HasValue) PlaceAtPointer(visuals, selected.Value, ray.Value, click);
            }
            else if (worldInfoAllowed)
                worldInfo.TickPointer(visuals, observation, cursor, ray, uiTarget, click);
            else HideWorldInfo();
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
            string invalid = null;
            if (center.x < radius || center.z < radius || center.x > rules.MapSize - radius || center.z > rules.MapSize - radius)
                invalid = "시설이 지도 안에 들어오도록 위치를 고르세요.";
            else if (reach < 0 || System.Numerics.Vector2.DistanceSquared(point, observation.BasePosition) > reach * reach)
                invalid = "본진의 건설 범위 안에 배치하세요.";
            else if (kind != BuildingKind.Dome)
                foreach (BuildingView building in observation.Buildings)
                {
                    float clearance = radius + building.Radius;
                    if (building.BlocksMovement &&
                        System.Numerics.Vector2.DistanceSquared(point, building.Position) < clearance * clearance)
                    {
                        invalid = "기존 시설과 겹칩니다.";
                        break;
                    }
                }
            if (invalid == null && OverlapsRecallSlots(kind, point, radius + rules.MinimumSeparation * 0.5f, observation.RecallArrivalSlots))
                invalid = "양자 이동의 귀환 구역과 겹칩니다.";
            int element = SelectedTowerElement(kind);
            visuals.RenderConstructionPreview(kind, element, center, radius, rules.TowerRange, invalid == null);
            if (!confirm) return;
            if (invalid != null)
            {
                status.text = invalid;
                return;
            }
            selected = null;
            placeLabel.text = "배치 시작";
            status.text = "건설 명령 확인 중…";
            request(kind, cellX, cellZ, element);
        }

        /// <summary>본인에게 받은 예약 위치만 검사한다. 다른 소유자의 배치 가능 여부는 서버가 판정한다.</summary>
        internal static bool OverlapsRecallSlots(BuildingKind kind, System.Numerics.Vector2 point, float clearance,
            ReadOnlySpan<System.Numerics.Vector2> slots)
        {
            if (kind == BuildingKind.Dome) return false;
            foreach (System.Numerics.Vector2 slot in slots)
                if (System.Numerics.Vector2.DistanceSquared(point, slot) < clearance * clearance) return true;
            return false;
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
            pointer.Reset();
            pointer.position = screenPosition;
            system.RaycastAll(pointer, pointerHits);
            Transform target = null;
            foreach (RaycastResult result in pointerHits)
                if (result.module is GraphicRaycaster) { target = result.gameObject.transform; break; }
            pointerHits.Clear();
            return target;
        }

        internal void SetResult(string message, bool pending)
        {
            status.text = message;
            worldInfo.SetResult(message, pending);
        }

        private void RefreshControls()
        {
            if (observation == null || observation.Tick < 0 || observation.BaseLevel <= 0)
            {
                previousLevel = -1;
                foreach (Button button in build) button.interactable = false;
                RefreshDetail();
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
            if (previousLevel == observation.BaseLevel && previousCores == observation.CoreBalance &&
                counts.AsSpan().SequenceEqual(previousCounts)) return;
            previousLevel = observation.BaseLevel;
            previousCores = observation.CoreBalance;
            Array.Copy(counts, previousCounts, counts.Length);
            int slots = rules.TowerSlots(observation.BaseLevel);
            heading.text = $"건설 · 본진 {observation.BaseLevel}단계 · 위성 {towers}/{slots}";
            for (int index = 0; index < Kinds.Length; index++)
            {
                BuildingKind kind = Kinds[index];
                bool isTower = BuildingTooltip.IsTower(kind);
                int cost = rules.BuildingCost(kind);
                bool unlocked = rules.IsBuildingUnlocked(observation.BaseLevel, kind);
                bool full = isTower ? towers >= slots : counts[index] > 0;
                build[index].interactable = unlocked && !full && observation.CoreBalance >= cost;
                availability[index] = !unlocked ? UnlockReason(kind, observation.BaseLevel) : full ? (isTower ? "위성 슬롯 가득 참" : "설치됨") :
                    observation.CoreBalance < cost ? $"핵 {cost - observation.CoreBalance}개 부족" : null;
                string suffix = !unlocked ? " · 잠김" : full ? " · 한도" : observation.CoreBalance < cost ? " · 부족" : string.Empty;
                buildLabels[index].text = $"{BuildingTooltip.Name(kind)}\n<size=11>{cost}핵{suffix}</size>";
            }
            if (selected.HasValue && !build[Array.IndexOf(Kinds, selected.Value)].interactable) CancelPlacement();
            RefreshDetail();
        }

        private void RefreshDetail()
        {
            place.gameObject.SetActive(chosen.HasValue);
            for (int index = 0; index < towerOptions.Length; index++)
            {
                bool show = chosen == (index == (int)ElementGroup.Nature ? BuildingKind.NatureTower : BuildingKind.EnergyTower);
                towerOptions[index].gameObject.SetActive(show);
                towerOptionLabels[index].gameObject.SetActive(show);
            }
            place.interactable = chosen.HasValue && build[Array.IndexOf(Kinds, chosen.Value)].interactable;
            BuildingKind? item = selected ?? hovered ?? chosen;
            if (!item.HasValue) { detail.text = "시설을 고르면 건설 조건을 확인할 수 있습니다. 원시핵을 클릭하면 레벨을 올릴 수 있습니다."; return; }
            BuildingKind kind = item.Value;
            string reason = availability[Array.IndexOf(Kinds, kind)];
            string terms = BuildingTooltip.BuildTerms(rules, kind);
            detail.text = $"{terms}{(reason == null ? string.Empty : $" · {reason}")}\n{BuildingTooltip.Function(rules, kind)}";
        }

        private string UnlockReason(BuildingKind kind, int level)
        {
            for (int next = level + 1; next <= rules.MaxBaseLevel; next++)
                if (rules.IsBuildingUnlocked(next, kind)) return $"본진 {next}단계 해금";
            return "해금되지 않음";
        }

    }
}
