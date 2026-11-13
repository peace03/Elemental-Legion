using System;
using System.Text;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
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
        private readonly UnityEngine.UI.Button[] actions = new UnityEngine.UI.Button[2];
        private readonly TMP_Text[] actionLabels = new TMP_Text[2];
        private TMP_Text progression, feedback;
        private Action upgradeBase;
        private Action<int, FacilityUpgradeBranch, byte> upgradeFacility;
        private readonly FacilityUpgradeBranch[] actionBranches = new FacilityUpgradeBranch[2];
        private byte nextRank, lastRank;
        private FacilityUpgradeBranch lastBranch;
        private int lastBaseLevel, lastProductionSeconds, requestEntity;
        private long lastCoreBalance;
        private bool lastOwned, lastPinned, waiting;
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

        internal void ShowWorld(BuildingView building, long tick, Vector2 cursor, GameplayFrame observation = null,
            bool pinned = false)
        {
            int remaining = building.IsUnderConstruction
                ? (int)Math.Ceiling(Math.Max(0, building.ConstructionEndTick - tick) * rules.DeltaTime) : -1;
            if (close != null)
            {
                ShowWorldCard(building, remaining, building.GhostEndTick > tick, false, observation, pinned);
                return;
            }
            Show(building.Kind, building.TowerElement, building.Id, remaining, null, cursor);
        }

        internal void ShowLastSeen(BuildingView building, long lastSeenTick)
            => ShowWorldCard(building, -1, building.GhostEndTick > lastSeenTick, true);

        internal void ConfigureCard(ObservatoryUi ui, Action closeRequested, Action upgradeBase = null,
            Action<int, FacilityUpgradeBranch, byte> upgradeFacility = null)
        {
            cardUi = ui;
            this.upgradeBase = upgradeBase;
            this.upgradeFacility = upgradeFacility;
            root.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            icon = ui.Icon(root, "Building icon", null, 12, 9, 60, 60);
            close = ui.Button(root, "×", 306, 10, 30, 28, closeRequested);
            TMP_Text closeLabel = close.GetComponentInChildren<TMP_Text>();
            ObservatoryUi.Fill(closeLabel.rectTransform);
            closeLabel.overflowMode = TextOverflowModes.Overflow;
            title.color = ObservatoryUi.TextColor;
            title.fontSize = 20;
            progression = ui.Label(root, "Facility progression", "", 14, 12, 0, 324, 28, ObservatoryUi.TextColor);
            feedback = ui.Label(root, "Facility response", "", 13, 12, 0, 324, 22, ObservatoryUi.Accent);
            for (int index = 0; index < actions.Length; index++)
            {
                int slot = index;
                actions[index] = ui.Button(root, $"시설 업그레이드 {index + 1}", 12, 0, 324, 40, () => RequestUpgrade(slot));
                actionLabels[index] = actions[index].GetComponentInChildren<TMP_Text>();
                actionLabels[index].fontSize = 13;
                actionLabels[index].textWrappingMode = TextWrappingModes.Normal;
            }
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

        internal void SetResult(string message, bool pending)
        {
            if (feedback == null) return;
            bool openOwnCard = root.gameObject.activeSelf && lastOwned && lastPinned;
            if (pending && !waiting) requestEntity = openOwnCard ? lastEntity : 0;
            feedback.text = openOwnCard && requestEntity == lastEntity ? message : string.Empty;
            if (!pending) requestEntity = 0;
            if (waiting != pending) lastBaseLevel = -1;
            waiting = pending;
            if (root.gameObject.activeSelf) ReflowCard();
        }

        private void RequestUpgrade(int slot)
        {
            if (!root.gameObject.activeSelf || waiting || !actions[slot].gameObject.activeSelf || !actions[slot].interactable) return;
            if (lastKind == BuildingKind.Base) upgradeBase?.Invoke();
            else upgradeFacility?.Invoke(lastEntity, actionBranches[slot], nextRank);
        }

        private void ShowWorldCard(BuildingView building, int remaining, bool ghost, bool remembered,
            GameplayFrame observation = null, bool pinned = false)
        {
            bool own = !remembered && observation != null && building.OwnerId == observation.PlayerId;
            int level = own ? observation.BaseLevel : 0;
            long cores = own ? observation.CoreBalance : 0;
            int productionSeconds = own && !ghost && building.NextProductionTick > 0
                ? (int)Math.Min(int.MaxValue, Math.Ceiling(Math.Max(0, building.NextProductionTick - observation.Tick) * rules.DeltaTime)) : -1;
            if (building.Id != lastEntity || building.Kind != lastKind || building.TowerElement != lastElement ||
                building.Health != lastHealth || building.MaxHealth != lastMaxHealth || remaining != lastRemaining ||
                ghost != lastGhost || remembered != lastRemembered || building.IsUnderConstruction != lastUnderConstruction ||
                own != lastOwned || pinned != lastPinned || level != lastBaseLevel || cores != lastCoreBalance ||
                building.UpgradeRank != lastRank || building.UpgradeBranch != lastBranch || productionSeconds != lastProductionSeconds ||
                !root.gameObject.activeSelf)
            {
                if (!root.gameObject.activeSelf || building.Id != lastEntity || !own || !pinned) feedback.text = string.Empty;
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
                lastOwned = own;
                lastPinned = pinned;
                lastBaseLevel = level;
                lastCoreBalance = cores;
                lastRank = building.UpgradeRank;
                lastBranch = building.UpgradeBranch;
                lastProductionSeconds = productionSeconds;
                title.text = IsTower(building.Kind) && building.TowerElement >= 0 && building.TowerElement < elements.Length
                    ? $"{Name(building.Kind)}\n<size=14>{elements[building.TowerElement]}</size>" : Name(building.Kind);
                icon.sprite = cardUi.Art.Building(building.Kind);
                detail.text = own && building.Kind == BuildingKind.Collector
                    ? $"{rules.CollectorProductionTicks(building.UpgradeBranch, building.UpgradeRank) * rules.DeltaTime:0.##}초마다 " +
                      $"{rules.CollectorProductionAmount(building.UpgradeBranch, building.UpgradeRank)}핵 생성" +
                      (productionSeconds >= 0 ? $" · 다음 생성 {productionSeconds}초" : string.Empty)
                    : IsTower(building.Kind)
                        ? $"{(own ? "사거리" : "기본 사거리")} {TowerRange(rules, building, own):0.##}m · 가장 가까운 적 또는 중립 원소를 공격합니다."
                        : Function(rules, building.Kind);
                text.Clear();
                if (remembered) text.Append("마지막 관측\n");
                text.Append("체력 ").AppendFormat("{0:0.##} / {1:0.##}", building.Health, building.MaxHealth);
                if (building.Kind != BuildingKind.Base) text.Append('\n').Append(BuildTerms(rules, building.Kind));
                if (remaining >= 0) text.Append("\n건설 중 · ").Append(remaining).Append("초");
                else if (remembered && building.IsUnderConstruction) text.Append("\n건설 중");
                else text.Append(ghost ? "\n유령 상태" : "\n가동 중");
                status.SetText(text);
                RefreshProgression(building, level, cores, own, pinned, ghost);
                ReflowCard();
            }
        }

        private void RefreshProgression(BuildingView building, int level, long cores, bool own, bool pinned, bool ghost)
        {
            progression.text = string.Empty;
            if (!own || !pinned || level < 1)
            {
                HideActions();
                return;
            }
            bool operational = !waiting && !ghost && !building.IsUnderConstruction;
            if (building.Kind == BuildingKind.Base)
            {
                int next = level + 1;
                if (next > rules.MaxBaseLevel)
                {
                    HideActions();
                    progression.text = $"최대 레벨 · 인구 상한 {rules.PopulationCap(level)}기";
                    return;
                }
                text.Clear().Append("Lv").Append(level).Append(" → Lv").Append(next)
                    .Append(" · 인구 ").Append(rules.PopulationCap(level)).Append(" → ").Append(rules.PopulationCap(next))
                    .Append("기\n위성 슬롯 ").Append(rules.TowerSlots(next));
                if (next <= GameRules.FacilityUpgradeRequiredLevel(GameRules.MaxFacilityUpgradeRank))
                    text.Append(" · 시설 ").Append(next - 1).Append("단계 해금");
                foreach (BuildingKind kind in (BuildingKind[])Enum.GetValues(typeof(BuildingKind)))
                    if (rules.IsBuildingUnlocked(next, kind) && !rules.IsBuildingUnlocked(level, kind))
                        text.Append(" · ").Append(Name(kind));
                progression.SetText(text);
                int cost = rules.UpgradeCost(next);
                ConfigureAction(0, $"원시핵 Lv{next} 업그레이드 · {cost}핵", operational && cores >= cost,
                    cores < cost ? $"핵 {cost - cores}개 부족" : null);
                actions[1].gameObject.SetActive(false);
                return;
            }
            if (!rules.TryGetFacilityUpgrade(building.Kind, FacilityUpgradeBranch.A, 1, out _))
            {
                HideActions();
                return;
            }

            byte rank = building.UpgradeRank;
            string current = rank == 0 ? "기본" : DescribeEffect(building.Kind, building.UpgradeBranch, rank);
            progression.text = $"{rank}/{GameRules.MaxFacilityUpgradeRank}단계 · {current}" +
                (rank == GameRules.MaxFacilityUpgradeRank ? " · 최대 단계" : string.Empty);
            if (rank == GameRules.MaxFacilityUpgradeRank)
            {
                HideActions();
                return;
            }
            nextRank = (byte)(rank + 1);
            ConfigureFacilityAction(0, building.Kind, rank == 0 ? FacilityUpgradeBranch.A : building.UpgradeBranch, level, cores, operational);
            if (rank == 0) ConfigureFacilityAction(1, building.Kind, FacilityUpgradeBranch.B, level, cores, operational);
            else actions[1].gameObject.SetActive(false);
        }

        private void HideActions()
        {
            foreach (var action in actions) action.gameObject.SetActive(false);
        }

        private void ConfigureFacilityAction(int slot, BuildingKind kind, FacilityUpgradeBranch branch, int level,
            long cores, bool operational)
        {
            if (!rules.TryGetFacilityUpgrade(kind, branch, nextRank, out FacilityUpgrade upgrade)) return;
            actionBranches[slot] = branch;
            int required = GameRules.FacilityUpgradeRequiredLevel(nextRank);
            string condition = level < required ? $"원시핵 Lv{required} 필요" :
                cores < upgrade.Cost ? $"핵 {upgrade.Cost - cores}개 부족" : null;
            ConfigureAction(slot, $"{nextRank}단계 · {DescribeEffect(kind, branch, nextRank)} · {upgrade.Cost}핵",
                operational && condition == null, condition);
        }

        private void ConfigureAction(int slot, string caption, bool enabled, string condition)
        {
            actions[slot].gameObject.SetActive(true);
            actions[slot].interactable = enabled;
            actionLabels[slot].text = condition == null ? caption : $"{caption}\n<size=11>{condition}</size>";
        }

        private string DescribeEffect(BuildingKind kind, FacilityUpgradeBranch branch, int rank)
        {
            if (!rules.TryGetFacilityUpgrade(kind, branch, rank, out FacilityUpgrade upgrade)) return string.Empty;
            bool a = branch == FacilityUpgradeBranch.A;
            if (kind == BuildingKind.Collector) return a ? $"주기 {upgrade.Effect:0.##}초" : $"수량 {upgrade.Effect:0}핵/회";
            if (IsTower(kind)) return a ? $"사거리 {upgrade.Effect:0.##}m" : $"공격력 ×{upgrade.Effect:0.##}";
            return a ? $"양성 비용 {upgrade.Effect:0}% 할인" : $"양성 원소 공격력 +{upgrade.Effect:0}%";
        }

        private void ReflowCard()
        {
            ObservatoryUi.Place(root, Vector2.one, topRightOffset, new Vector2(cardWidth, regionHeight));
            ObservatoryUi.Place((RectTransform)close.transform, Vector2.up, new Vector2(cardWidth - 42, -10), new Vector2(30, 28));
            ObservatoryUi.Place(title.rectTransform, Vector2.up, new Vector2(82, -9), new Vector2(cardWidth - 134, 60));
            float bottom = FitText(detail, 77);
            bottom = FitText(status, bottom + 12);
            if (progression.text.Length > 0) bottom = FitText(progression, bottom + 8);
            foreach (var action in actions)
            {
                if (!action.gameObject.activeSelf) continue;
                bottom += 5;
                ObservatoryUi.Place((RectTransform)action.transform, Vector2.up, new Vector2(12, -bottom),
                    new Vector2(cardWidth - 24, 40));
                bottom += 40;
            }
            if (feedback.text.Length > 0) bottom = FitText(feedback, bottom + 6);
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

        internal static float TowerRange(GameRules rules, BuildingView building, bool own)
        {
            if (own && building.UpgradeBranch == FacilityUpgradeBranch.A &&
                rules.TryGetFacilityUpgrade(building.Kind, building.UpgradeBranch, building.UpgradeRank, out FacilityUpgrade upgrade))
                return (float)upgrade.Effect;
            return rules.TowerRange;
        }

        internal static string Function(GameRules rules, BuildingKind kind) => kind switch
        {
            BuildingKind.NatureForge => "자연 원소를 양성합니다.",
            BuildingKind.EnergyForge => "에너지 원소를 양성합니다.",
            BuildingKind.Collector => $"{rules.CollectorIntervalTicks * rules.DeltaTime:0.##}초마다 {rules.CollectorCoresPerCycle}핵을 생성합니다.",
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
