using System;
using System.Linq;
using System.Reflection;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    /// <summary>소개 카드의 값과 클릭 영역을 검증한다.</summary>
    public sealed class ElementTooltipTests
    {
        private GameObject parent;
        private GameRules rules;
        private string[] names;
        private ObservatoryUi ui;

        [SetUp]
        public void SetUp()
        {
            parent = new GameObject("Info card test", typeof(RectTransform));
            ((RectTransform)parent.transform).sizeDelta = new Vector2(1280, 720) / 1.1f;
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            rules = new GameRules(document);
            names = rules.Elements.Select((definition, index) => (string)document.Root["elements"][index]["displayName"]).ToArray();
            ui = new ObservatoryUi(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset"));
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(parent);

        [Test]
        public void AllElementsFitAndWaterShowsItsThreeReactionsAndMainDiscount()
        {
            var tooltip = new ElementTooltip(ui, (RectTransform)parent.transform, rules, names, () => { });
            tooltip.Layout(348, 465, new Vector2(-16, -70));
            int water = rules.ElementIndex("water");
            for (int element = 0; element < rules.Elements.Count; element++)
            {
                tooltip.Show(element, water);
                var card = (RectTransform)parent.transform.Find("Element info");
                Assert.That(card.gameObject.activeSelf, Is.True);
                Assert.That(CardText(card), Does.Contain(names[element]).And.Contain("최대 체력"));
                Assert.That(card.rect.height, Is.LessThanOrEqualTo(465));
            }

            tooltip.Show(water, water);
            var root = (RectTransform)parent.transform.Find("Element info");
            string text = CardText(root);
            Assert.That(text, Does.Contain("감전").And.Contain("빙결").And.Contain("증기")
                .And.Contain($"주력 양성 {rules.TrainingCost(water, water)}핵 · {rules.TrainingSeconds(water, water):0.##}초"));
            tooltip.Show(water, rules.ElementIndex("ice"));
            Assert.That(CardText(root), Does.Contain($"양성 {rules.BaseCost(water)}핵 · {rules.TrainingSeconds(water, -1):0.##}초"));
        }

        [Test]
        public void BuildingCardDisplaysObservedHealthAndConsumesPointerWhilePreviewing()
        {
            var tooltip = new BuildingTooltip(ui, (RectTransform)parent.transform, rules, names);
            tooltip.ConfigureCard(ui, () => { });
            tooltip.Layout(348, 465, new Vector2(-16, -70));
            var building = new BuildingView(8, 1, BuildingKind.Collector, WorldPoint.Zero, 2, 27, 91, -1, 0, 120);
            tooltip.ShowWorld(building, 100, Vector2.zero);
            var card = (RectTransform)parent.transform.Find("Building tooltip");
            Assert.That(CardText(card), Does.Contain("27 / 91").And.Contain("건설 중")
                .And.Contain("생성합니다").And.Contain($"{rules.BuildingCost(building.Kind)}핵"));
            Assert.That(card.GetComponent<UnityEngine.UI.Image>().raycastTarget, Is.True);
            Assert.That(card.Find("×").gameObject.activeSelf, Is.False);
            tooltip.SetPinned(true);
            Assert.That(card.Find("×").gameObject.activeSelf, Is.True);
        }

        [Test]
        public void OwnFacilityUsesNextRankAndEnemyCardHasNoPurchase()
        {
            var tooltip = new BuildingTooltip(ui, (RectTransform)parent.transform, rules, names);
            var requested = (BuildingId: 0, Branch: FacilityUpgradeBranch.None, Rank: (byte)0);
            tooltip.ConfigureCard(ui, () => { }, upgradeFacility: (id, branch, rank) => requested = (id, branch, rank));
            tooltip.Layout(348, 465, new Vector2(-16, -70));
            var building = new BuildingView(8, 1, BuildingKind.Collector, WorldPoint.Zero, 2, 91, 91, -1, 0, 0,
                FacilityUpgradeBranch.A, 1, 160);
            GameplayFrame frame = CreateFrame(building);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId)).SetValue(frame, 1);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.BaseLevel)).SetValue(frame, 2);
            var card = (RectTransform)parent.transform.Find("Building tooltip");
            var action = card.Find("시설 업그레이드 1").GetComponent<UnityEngine.UI.Button>();
            tooltip.ShowWorld(building, 100, Vector2.zero, frame, true);
            Assert.That(CardText(card), Does.Contain("1/4단계").And.Contain("원시핵 Lv3"));
            Assert.That(action.interactable, Is.False);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.BaseLevel)).SetValue(frame, 3);
            tooltip.ShowWorld(building, 100, Vector2.zero, frame, true);
            Assert.That(action.interactable, Is.True);
            action.onClick.Invoke();
            Assert.That(requested, Is.EqualTo((8, FacilityUpgradeBranch.A, (byte)2)));

            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId)).SetValue(frame, 2);
            tooltip.ShowWorld(building, 100, Vector2.zero, frame, true);
            Assert.That(action.gameObject.activeSelf, Is.False);
            Assert.That(CardText(card), Does.Not.Contain("1/4단계"));

            var nucleus = new BuildingView(9, 2, BuildingKind.Base, WorldPoint.Zero, 6);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.BaseLevel)).SetValue(frame, rules.MaxBaseLevel);
            tooltip.ShowWorld(nucleus, 100, Vector2.zero, frame, true);
            Assert.That(CardText(card), Does.Contain("최대 레벨").And.Contain("인구 상한 300기"));
            Assert.That(action.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void TowerCardShowsItsUpgradedRangeOnlyToItsOwner()
        {
            var tooltip = new BuildingTooltip(ui, (RectTransform)parent.transform, rules, names);
            tooltip.ConfigureCard(ui, () => { });
            tooltip.Layout(348, 465, new Vector2(-16, -70));
            var tower = new BuildingView(8, 1, BuildingKind.NatureTower, WorldPoint.Zero, 2, 75, 75, rules.ElementIndex("water"), 0,
                upgradeBranch: FacilityUpgradeBranch.A, upgradeRank: 1);
            Assert.That(rules.TryGetFacilityUpgrade(tower.Kind, tower.UpgradeBranch, tower.UpgradeRank, out var upgrade), Is.True);
            GameplayFrame frame = CreateFrame(tower);
            var playerId = typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId));
            var card = (RectTransform)parent.transform.Find("Building tooltip");

            playerId.SetValue(frame, 1);
            tooltip.ShowWorld(tower, frame.Tick, Vector2.zero, frame);
            Assert.That(CardText(card), Does.Contain($"사거리 {upgrade.Effect:0.##}m"));

            playerId.SetValue(frame, 2);
            tooltip.ShowWorld(tower, frame.Tick, Vector2.zero, frame);
            Assert.That(CardText(card), Does.Contain($"기본 사거리 {rules.TowerRange:0.##}m"));
        }

        [Test]
        public void ClickingAnElementReplacesTheBuildingCardWithoutPinning()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var snapshot = new UnitSnapshotAssembler(rules);
                typeof(UnitSnapshotAssembler).GetProperty(nameof(UnitSnapshotAssembler.Count)).SetValue(snapshot, 1);
                typeof(UnitSnapshotAssembler).GetProperty(nameof(UnitSnapshotAssembler.Tick)).SetValue(snapshot, 1L);
                var elementPosition = new WorldPoint(30, 30);
                int water = rules.ElementIndex("water");
                snapshot.Units[0] = new NetworkUnit(7, 1, water, elementPosition, false, 10, 0, false, 0);
                var timeline = new SnapshotTimeline(4);
                timeline.Push(snapshot);
                timeline.ObservePose(new LegionPose(1, 1, 0, elementPosition, WorldPoint.Zero));
                visuals.Render(timeline, 1, elementPosition, 1);
                var building = new BuildingView(8, 1, BuildingKind.Collector, new WorldPoint(10, 10), 2);
                GameplayFrame frame = CreateFrame(building);
                var cards = new WorldInfoCards(ui, (RectTransform)parent.transform, rules, names);
                var facilityRay = new Ray(new Vector3(10, 15, 10), Vector3.down);
                var elementRay = new Ray(new Vector3(30, 15, 30), Vector3.down);
                var groundRay = new Ray(new Vector3(40, 15, 40), Vector3.down);

                cards.TickPointer(visuals, frame, Vector2.zero, facilityRay, null, true);
                Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.BuildingInfo));
                cards.TickPointer(visuals, frame, Vector2.zero, elementRay, null, true);
                Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.ElementInfo));
                Assert.That(parent.transform.Find("Element info/×").gameObject.activeSelf, Is.False);
                cards.TickPointer(visuals, frame, Vector2.zero, groundRay, null, false);
                Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.None));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ClosingHoverSuppressesItUntilPointerLeavesAndPinnedInfoNeedsCurrentObservation()
        {
            var cards = new WorldInfoCards(ui, (RectTransform)parent.transform, rules, names);
            var visuals = parent.AddComponent<ElementalVisuals>();
            var building = new BuildingView(8, 1, BuildingKind.Collector, new WorldPoint(10, 10), 2);
            GameplayFrame frame = CreateFrame(building);
            var hit = new Ray(new Vector3(10, 15, 10), Vector3.down);
            var miss = new Ray(new Vector3(20, 15, 20), Vector3.down);
            cards.TickPointer(visuals, frame, Vector2.zero, hit, null, false);
            Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.BuildingInfo));
            Assert.That(cards.Close(), Is.True);
            cards.TickPointer(visuals, frame, Vector2.zero, hit, null, true);
            Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.None));
            cards.TickPointer(visuals, frame, Vector2.zero, miss, null, false);
            cards.TickPointer(visuals, frame, Vector2.zero, hit, null, true);
            cards.TickPointer(visuals, frame, Vector2.zero, miss, null, false);
            Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.BuildingInfo));
            cards.TickPointer(visuals, CreateFrame(), Vector2.zero, miss, null, false);
            Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.None));
        }

        [Test]
        public void ClickAnotherBuildingReplacesThePinnedCardAndClickingTheCardKeepsIt()
        {
            var cards = new WorldInfoCards(ui, (RectTransform)parent.transform, rules, names);
            var visuals = parent.AddComponent<ElementalVisuals>();
            var first = new BuildingView(8, 1, BuildingKind.Collector, new WorldPoint(10, 10), 2);
            var second = new BuildingView(9, 1, BuildingKind.NatureForge, new WorldPoint(20, 20), 2);
            GameplayFrame frame = CreateFrame(first, second);
            var card = (RectTransform)parent.transform.Find("Building tooltip");
            cards.TickPointer(visuals, frame, Vector2.zero, new Ray(new Vector3(10, 15, 10), Vector3.down), null, true);
            Assert.That(CardText(card), Does.Contain("포집기"));
            cards.TickPointer(visuals, frame, Vector2.zero, new Ray(new Vector3(20, 15, 20), Vector3.down), null, true);
            Assert.That(CardText(card), Does.Contain("응집로"));
            cards.TickPointer(visuals, frame, Vector2.zero, null, card, true);
            Assert.That(CardText(card), Does.Contain("응집로"));
            cards.TickPointer(visuals, frame, Vector2.zero, new Ray(new Vector3(30, 15, 30), Vector3.down), null, true);
            Assert.That(card.gameObject.activeSelf, Is.True);
            Assert.That(CardText(card), Does.Contain("응집로"));
        }

        [Test]
        public void RememberedBuildingCardFreezesStateAndClosesWhenItsSiteIsCleared()
        {
            var panel = new ConstructionPanel(ui, parent.transform, rules, names, (_, _, _, _) => { }, () => { });
            var cards = (WorldInfoCards)typeof(ConstructionPanel).GetField("worldInfo",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
            var visuals = parent.AddComponent<ElementalVisuals>();
            var memory = new BuildingMemory();
            visuals.SetBuildingMemory(memory);
            var building = new BuildingView(8, 1, BuildingKind.Collector, new WorldPoint(10, 10), 2, 27, 91, -1, 0, 120);
            GameplayFrame frame = CreateFrame(building);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId)).SetValue(frame, 2);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.Revision)).SetValue(frame, 1u);
            memory.Observe(frame);
            var ray = new Ray(new Vector3(10, 15, 10), Vector3.down);
            var card = (RectTransform)parent.transform.Find("Building tooltip");
            cards.TickPointer(visuals, frame, Vector2.zero, ray, null, true);
            Assert.That(CardText(card), Does.Not.Contain("마지막 관측"));

            memory.Forget(building.Id, 101);
            GameplayFrame delayed = CreateFrame(new BuildingView(8, 1, BuildingKind.Collector,
                building.Position, 2, 91, 91, -1, 0, 0));
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.SpatialReady)).SetValue(delayed, false);
            panel.Observe(delayed);
            var fixture = new InputTestFixture();
            fixture.Setup();
            try
            {
                InputSystem.AddDevice<Mouse>().MakeCurrent();
                panel.TickPointer(visuals, true);
                Assert.That(card.gameObject.activeSelf, Is.True);
                Assert.That(CardText(card), Does.Contain("마지막 관측").And.Contain("건설 중").And.Contain("27 / 91"));
                Assert.That(CardText(card), Does.Not.Contain("건설 중 ·"));
                Assert.That(card.Find("×").gameObject.activeSelf, Is.True);

                cards.Hide();
                cards.TickPointer(visuals, delayed, Vector2.zero, ray, null, true);
                Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.BuildingInfo));
                memory.ConfirmAbsent(new[] { building.Id }, 102, 2);
                panel.TickPointer(visuals, true);
                Assert.That(cards.Kind, Is.EqualTo(HudPanelKind.None));
            }
            finally { fixture.TearDown(); }
        }

        [Test]
        public void ClosingOrBlockingConstructionCancelsPlacementWithoutSubmitting()
        {
            int requested = 0;
            var panel = new ConstructionPanel(ui, parent.transform, rules, names, (_, _, _, _) => requested++, () => { });
            Assert.That(panel.IsOpen, Is.False);
            panel.Observe(CreateFrame());
            panel.SetOpen(true);
            var root = parent.transform.Find("Construction");
            var buttons = root.GetComponentsInChildren<UnityEngine.UI.Button>(true);
            var collector = buttons.Single(button => button.name == BuildingTooltip.Name(BuildingKind.Collector));
            var energy = buttons.Single(button => button.name == BuildingTooltip.Name(BuildingKind.EnergyTower));
            var place = buttons.Single(button => button.name == "배치 시작");
            Assert.That(collector.interactable, Is.True);
            Assert.That(energy.interactable, Is.False);
            collector.onClick.Invoke();
            Assert.That(panel.CancelPlacement(), Is.False);
            place.onClick.Invoke();
            panel.SetOpen(false);
            Assert.That(panel.CancelPlacement(), Is.False);
            panel.SetOpen(true);
            place.onClick.Invoke();
            panel.TickPointer(null, false);
            Assert.That(panel.CancelPlacement(), Is.False);
            Assert.That(requested, Is.Zero);
        }

        private GameplayFrame CreateFrame(params BuildingView[] buildings)
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), hidden, null, new object[] { rules }, null);
            var buffer = (BuildingView[])typeof(GameplayFrame).GetField("BuildingBuffer", hidden).GetValue(frame);
            Array.Copy(buildings, buffer, buildings.Length);
            typeof(GameplayFrame).GetField("BuildingCount", hidden).SetValue(frame, buildings.Length);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.Tick)).SetValue(frame, 100L);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.SpatialReady)).SetValue(frame, true);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.BaseLevel)).SetValue(frame, 1);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.CoreBalance)).SetValue(frame, 100L);
            return frame;
        }

        private static string CardText(RectTransform card) => string.Join("\n", card.GetComponentsInChildren<TMP_Text>(true).Select(label => label.text));
    }
}
