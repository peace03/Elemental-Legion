using System;
using System.Collections.Generic;
using System.Reflection;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>묶음 양성의 입력·비용·목적지와 개별 취소가 관측값을 보존하는지 확인한다.</summary>
    public sealed class TrainingBatchHudTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root;
        private CombatHud hud;
        private GameRules rules;
        private GameDataDocument document;
        private GameplayFrame frame;

        [SetUp]
        public void SetUp()
        {
            root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            hud = root.GetComponentInChildren<CombatHud>(true);
            document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            rules = new GameRules(document);
            hud.Configure(document, rules);
            hud.BuildObservatory(new ObservatoryUi(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/_Project/Settings/UI/LegionUiSDF.asset")), rules, root.GetComponent<ElementalVisuals>());
            frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), Hidden, null, new object[] { rules }, null);
            Set("PlayerId", 1); Set("MainElement", 0); Set("BaseLevel", 4);
            Set("CoreBalance", 999); Set("Population", 12); Set("PopulationCap", 100);
            var buildings = (BuildingView[])typeof(GameplayFrame).GetField("BuildingBuffer", Hidden).GetValue(frame);
            buildings[0] = new BuildingView(1, 1, BuildingKind.NatureForge, default, 1);
            buildings[1] = new BuildingView(2, 1, BuildingKind.EnergyForge, default, 1);
            typeof(GameplayFrame).GetField("BuildingCount", Hidden).SetValue(frame, 2);
            hud.Apply(frame);
            hud.SetPanel(HudPanelKind.Training);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void DefaultAndEveryBatchTargetReachTheNetworkWithoutChangingPopulation()
        {
            var bootstrap = root.GetComponent<ClientBootstrap>();
            using var connection = new ClientConnection(rules, new NetworkSettings(document));
            typeof(ClientConnection).GetProperty(nameof(ClientConnection.State)).SetValue(connection, ConnectionState.Connected);
            typeof(ClientBootstrap).GetField("connection", Hidden).SetValue(bootstrap, connection);
            var send = typeof(ClientBootstrap).GetMethod("Train", Hidden);
            Action<int, int, byte> request = (element, count, target) => send.Invoke(bootstrap, new object[] { element, count, target });
            hud.TrainRequested += request;
            try
            {
                Assert.That(Field<int>("trainingQuantity"), Is.EqualTo(1));
                Assert.That(Field<byte>("trainingDestination"), Is.EqualTo(4));
                Field<Button[]>("train")[0].onClick.Invoke();
                var initialWriter = (NetDataWriter)typeof(ClientConnection).GetField("writer", Hidden).GetValue(connection);
                Assert.That(initialWriter.Length, Is.Zero, "원소를 고르는 동안에는 주문을 보내지 않습니다.");
                Field<Button>("trainingConfirm").onClick.Invoke();
                Assert.That(Protocol.TryReadCommand(new NetDataReader(initialWriter.CopyData()), rules, out var initial), Is.True);
                Assert.That(initial.Count, Is.EqualTo(1));
                Assert.That(initial.TargetSquad, Is.EqualTo(4));
                ulong sequence = initial.Sequence;
                for (int count = 1; count <= 4; count++)
                    for (byte target = 1; target <= 4; target++)
                    {
                        Field<Button[]>("trainingQuantities")[count - 1].onClick.Invoke();
                        Field<Button[]>("trainingDestinations")[target - 1].onClick.Invoke();
                        Field<Button>("trainingConfirm").onClick.Invoke();
                        var writer = (NetDataWriter)typeof(ClientConnection).GetField("writer", Hidden).GetValue(connection);
                        Assert.That(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var command), Is.True);
                        Assert.That(command.Sequence, Is.EqualTo(++sequence));
                        Assert.That(command.Kind, Is.EqualTo(CommandKind.Train));
                        Assert.That(command.Count, Is.EqualTo(count));
                        Assert.That(command.TargetSquad, Is.EqualTo(target));
                        Assert.That(frame.Population, Is.EqualTo(12));
                        Assert.That(frame.ReservedPopulation, Is.Zero);
                    }
                hud.ResetHud();
                Assert.That(Field<int>("trainingQuantity"), Is.EqualTo(1));
                Assert.That(Field<byte>("trainingDestination"), Is.EqualTo(4));
            }
            finally
            {
                hud.TrainRequested -= request;
                typeof(ClientBootstrap).GetField("connection", Hidden).SetValue(bootstrap, null);
            }
        }

        [Test]
        public void BatchShowsTotalCostPerUnitTimeAndChecksWholePopulationAndGuardRoom()
        {
            Field<Button[]>("train")[0].onClick.Invoke();
            Field<Button[]>("trainingQuantities")[3].onClick.Invoke();
            int cost = rules.TrainingCost(0, frame.MainElement) * 4;
            string text = Field<TMP_Text>("trainingDetail").text;
            Assert.That(text, Does.Contain($"{cost}핵").And.Contain($"{rules.TrainingSeconds(0, frame.MainElement):0.#}초/기"));
            Assert.That(Field<TMP_Text[]>("labels")[0].text, Does.Contain("주력"));
            Assert.That(text, Does.Contain("근접"));
            Set("CoreBalance", cost - 1); hud.Apply(frame);
            Assert.That(Field<Button>("trainingConfirm").interactable, Is.False);
            Set("CoreBalance", cost); Set("Population", 97); hud.Apply(frame);
            Assert.That(Field<Button>("trainingConfirm").interactable, Is.False);
            Set("Population", 96); hud.Apply(frame);
            Assert.That(Field<Button>("trainingConfirm").interactable, Is.True);
            var squads = (SquadView[])typeof(GameplayFrame).GetField("SquadBuffer", Hidden).GetValue(frame);
            squads[3] = new SquadView(4, default, default, default, default, SquadOrderKind.Hold, 0, rules.GarrisonCap(4) - 3, 0, 0);
            hud.Apply(frame);
            Assert.That(Field<Button>("trainingConfirm").interactable, Is.False);
            Field<Button[]>("trainingDestinations")[0].onClick.Invoke();
            Assert.That(Field<Button>("trainingConfirm").interactable, Is.True);
            Assert.That(frame.Population, Is.EqualTo(96));
        }

        [Test]
        public void BatchUsesTheObservedForgeUpgradePrice()
        {
            Field<Button[]>("train")[0].onClick.Invoke();
            Field<Button[]>("trainingQuantities")[1].onClick.Invoke();
            var buildings = (BuildingView[])typeof(GameplayFrame).GetField("BuildingBuffer", Hidden).GetValue(frame);
            buildings[0] = new BuildingView(1, 1, BuildingKind.NatureForge, default, 1, 100, 100,
                -1, 0, 0, FacilityUpgradeBranch.A, 3);
            int discounted = rules.TrainingCost(0, frame.MainElement, FacilityUpgradeBranch.A, 3) * 2;
            Set("CoreBalance", discounted);
            hud.Apply(frame);
            Assert.That(Field<TMP_Text>("trainingConfirmLabel").text, Does.Contain($"{discounted}핵"));
            Assert.That(Field<Button>("trainingConfirm").interactable, Is.True);
        }

        [Test]
        public void QuantityDestinationAndSelectedElementSurviveClosingAndSquadSelection()
        {
            Field<Button[]>("train")[0].onClick.Invoke();
            Field<Button[]>("trainingQuantities")[2].onClick.Invoke();
            Field<Button[]>("trainingDestinations")[1].onClick.Invoke();
            Assert.That(hud.ClosePanel(), Is.True);
            hud.SelectSquad(4);
            hud.SetPanel(HudPanelKind.Training);
            Assert.That(Field<int>("trainingQuantity"), Is.EqualTo(3));
            Assert.That(Field<byte>("trainingDestination"), Is.EqualTo(2));
            Assert.That(Field<TMP_Text>("trainingConfirmLabel").text, Does.Contain("3기 양성"));
        }

        [Test]
        public void ActiveAndWaitingDestinationsRemainSeparateAndEachWaitingOrderCancelsById()
        {
            Set("TrainingElement", 0); Set("TrainingTargetSquad", (byte)2);
            Set("TrainingEndTick", 120L);
            var orders = (List<TrainingOrderView>)typeof(GameplayFrame).GetField("TrainingQueueBuffer", Hidden).GetValue(frame);
            orders.Add(new TrainingOrderView(91, 0, 1)); orders.Add(new TrainingOrderView(92, 1, 4));
            var squads = (SquadView[])typeof(GameplayFrame).GetField("SquadBuffer", Hidden).GetValue(frame);
            squads[0] = new SquadView(1, default, default, default, default, SquadOrderKind.Hold, 0, 8, 2, 1);
            hud.Apply(frame);
            hud.SelectSquad(1);
            Assert.That(Field<TMP_Text>("composition").text, Does.Contain("2번"));
            hud.SetPanel(HudPanelKind.Forces);
            Assert.That(Field<TMP_Text[]>("forceRows")[0].text, Does.Contain("합류 8기").And.Contain("증원 2기").And.Contain("예약 1기"));
            hud.SetPanel(HudPanelKind.Training);
            var rows = Field<Dictionary<ulong, (Button Button, TMP_Text Label)>>("trainingOrders");
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows[91].Label.text, Does.Contain("1번"));
            Assert.That(rows[92].Label.text, Does.Contain("수비"));
            var canceled = new List<ulong>();
            hud.TrainingCancelRequested += canceled.Add;
            rows[92].Button.onClick.Invoke(); rows[91].Button.onClick.Invoke();
            Assert.That(canceled, Is.EqualTo(new ulong[] { 92, 91 }));
            Assert.That(orders.Count, Is.EqualTo(2), "클라이언트는 서버 승인 전에 예약을 제거하지 않습니다.");
            orders.RemoveAt(0);
            hud.Apply(frame);
            Assert.That(rows.ContainsKey(91), Is.False);
            Assert.That(rows.Count, Is.EqualTo(1));
        }

        [Test]
        public void BatchLabelsStillFitAt110PercentWithoutTakingHeightFromTheQueue()
        {
            var canvas = hud.GetComponentInParent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.GetComponent<CanvasScaler>().enabled = false;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(1280, 720) / 1.1f;
            Canvas.ForceUpdateCanvases();
            hud.ApplyPreferences(new ElementalLegion.Client.Input.ControlBindings(), 110);
            Field<Button[]>("trainingQuantities")[3].onClick.Invoke();
            foreach (TMP_Text label in Field<TMP_Text[]>("labels"))
                Assert.That(label.GetPreferredValues(label.text, label.rectTransform.rect.width, float.PositiveInfinity).y,
                    Is.LessThanOrEqualTo(label.rectTransform.rect.height), label.text);
            Assert.That(((RectTransform)Field<ScrollRect>("trainingQueueScroll").transform).rect.height, Is.GreaterThan(28));
        }

        private T Field<T>(string name) => (T)typeof(CombatHud).GetField(name, Hidden).GetValue(hud);
        private void Set(string name, object value) => typeof(GameplayFrame).GetProperty(name).SetValue(frame, value);
    }
}
