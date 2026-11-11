using System;
using System.Linq;
using System.Reflection;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Input;
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
    /// <summary>자기 여정 표시와 닫기 확인, 승계 보류, 연결 정리 뒤 최종 결과의 수명을 검증한다.</summary>
    public sealed class JourneyPresentationTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root, screenRoot;
        private ClientBootstrap bootstrap;
        private ClientConnection connection;
        private GameRules rules;
        private GameDataDocument document;
        private string[] names;
        private ObservatoryUi ui;

        [SetUp]
        public void SetUp()
        {
            document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            rules = new GameRules(document);
            names = rules.Elements.Select((entry, index) => (string)document.Root["elements"][index]["displayName"]).ToArray();
            root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            bootstrap = root.GetComponent<ClientBootstrap>();
            Set(bootstrap, "rules", rules);
            Set(bootstrap, "settings", new NetworkSettings(document));
            connection = new ClientConnection(rules, new NetworkSettings(document));
            typeof(ClientConnection).GetProperty("State").SetValue(connection, ConnectionState.Connected);
            Set(bootstrap, "connection", connection);
            Set(bootstrap, "displayedState", ConnectionState.Connected);
            ui = new ObservatoryUi(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset"));
            screenRoot = new GameObject("Journey presentation");
            screenRoot.SetActive(false);
            SetFrame(11, Result(11));
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(screenRoot);
            Set(bootstrap, "connection", null);
            connection.Dispose();
            Object.DestroyImmediate(root);
        }

        [Test]
        public void DashboardShowsAuthoritativeTotalsAndSinceLastOnlyInTheOwnPanel()
        {
            var screen = screenRoot.AddComponent<ClientScreen>();
            screen.Initialize(ui, 60, true);
            screen.BuildDashboard(null, null, "Tab");
            ((System.Collections.Generic.List<PlayerStanding>)typeof(GameplayFrame).GetField("StandingBuffer", Hidden)
                .GetValue(connection.Gameplay.State)).Add(new PlayerStanding(42, 1234, "관측자"));
            screen.Observe(connection.Gameplay.State, rules, names);
            TMP_Text total = Text(screen, "Journey total");
            TMP_Text recent = Text(screen, "Journey since last");
            Assert.That(total.text, Does.Contain("전향 101").And.Contain("구출 102").And.Contain("손실 103")
                .And.Contain("핵 유입 104").And.Contain("핵 소비 105").And.Contain("시설 파괴 106").And.Contain("공성 승리 107"));
            Assert.That(recent.text, Does.Contain("전향 1").And.Contain("구출 2").And.Contain("공성 승리 7"));
            Transform panel = screen.transform.Find("Observatory canvas/Player dashboard");
            Assert.That(panel.Find("Own player").GetComponentsInChildren<ScrollRect>(true), Has.Length.EqualTo(1));
            Assert.That(panel.Find("Connected standings").GetComponentsInChildren<TMP_Text>(true)
                .Any(label => label.text.Contains("핵 유입") || label.text.Contains("구출")), Is.False);
            Assert.That(panel.Find("Connected standings").GetComponentsInChildren<TMP_Text>(true)
                .Any(label => label.text == 1234L.ToString("N0")), Is.True);
            Assert.That(screen.transform.Find("Observatory canvas/Backdrop").GetComponent<Image>().color.a, Is.Zero);
            SetFrame(12, new JourneyResultView(11, new JourneyTotals(201, 102, 103, 104, 105, 106, 107), default));
            screen.Observe(connection.Gameplay.State, rules, names);
            Assert.That(total.text, Does.Contain("전향 201"));
            Assert.That(recent.text, Does.Contain("전향 0").And.Contain("구출 0"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CommonDashboardCloseAcknowledgesOnceAfterObservation(bool held)
        {
            var flow = screenRoot.AddComponent<ClientFlow>();
            var screen = screenRoot.AddComponent<ClientScreen>();
            screen.Initialize(ui, 60, false);
            screen.BuildDashboard(held ? null : (Action)(() => Invoke(flow, "CloseDashboard")), null, held ? "Tab" : null);
            Set(flow, "game", bootstrap);
            Set(flow, "dashboard", screen);
            Set(flow, "controls", new ControlBindings());
            Set(flow, "rules", rules);
            Set(flow, "elementNames", names);
            screen.transform.Find("Observatory canvas/Player dashboard/여정 기록").GetComponent<Button>().onClick.Invoke();
            Invoke(flow, "Update");
            if (held) Invoke(flow, "CloseDashboard");
            else screen.transform.Find("Observatory canvas/Player dashboard/돌아가기").GetComponent<Button>().onClick.Invoke();
            Assert.That(Field<ulong>(bootstrap, "sequence"), Is.EqualTo(1UL));
            var writer = Field<NetDataWriter>(connection, "writer");
            Assert.That(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out SimCommand command), Is.True);
            Assert.That(command.Kind, Is.EqualTo(CommandKind.AcknowledgeJourney));
            Assert.That(connection.Gameplay.State.Journey.SinceLast.Converted, Is.EqualTo(1),
                "확인 요청으로 화면의 서버 집계값을 미리 초기화하지 않습니다.");
            Invoke(flow, "CloseDashboard");
            Assert.That(Field<ulong>(bootstrap, "sequence"), Is.EqualTo(1UL));
        }

        [Test]
        public void JourneyAcknowledgmentDoesNotChangePendingConstructionOrItsFeedback()
        {
            var hud = root.GetComponentInChildren<CombatHud>(true);
            hud.Configure(document, rules);
            hud.BuildObservatory(ui, rules, root.GetComponent<ElementalVisuals>());
            Set(bootstrap, "prediction", new MovementPrediction(rules));
            Set(bootstrap, "timeline", new SnapshotTimeline(4));
            Set(bootstrap, "pendingConstruction", 42UL);
            hud.ConstructionResult("시설 건설 명령 확인 중…");
            var construction = Field<ConstructionPanel>(hud, "construction");
            var status = Field<TMP_Text>(construction, "status");
            var data = (SimEvent)Activator.CreateInstance(typeof(SimEvent), Hidden, null,
                new object[] { SimEventKind.CommandApplied, 1, 0, 0, System.Numerics.Vector2.Zero,
                    0f, (int)CommandKind.AcknowledgeJourney, 99UL, -1, System.Numerics.Vector2.Zero,
                    false, 0, ObservedEntityKind.Unit, ObservedEntityKind.Unit, ObservedEntityKind.Unit }, null);
            var entry = (NetworkEvent)Activator.CreateInstance(typeof(NetworkEvent), Hidden, null,
                new object[] { 1UL, 11L, data }, null);
            var frame = connection.Gameplay.State;
            ((NetworkEvent[])typeof(GameplayFrame).GetField("EventBuffer", Hidden).GetValue(frame))[0] = entry;
            typeof(GameplayFrame).GetField("EventCount", Hidden).SetValue(frame, 1);
            Invoke(bootstrap, "OnGameplay", frame);
            Assert.That(Field<ulong>(bootstrap, "pendingConstruction"), Is.EqualTo(42UL));
            Assert.That(status.text, Is.EqualTo("시설 건설 명령 확인 중…"));
        }

        [Test]
        public void DashboardCloseWithoutViewingJourneyDoesNotAcknowledge()
        {
            var flow = screenRoot.AddComponent<ClientFlow>();
            var screen = screenRoot.AddComponent<ClientScreen>();
            screen.Initialize(ui, 60, false);
            screen.BuildDashboard(() => Invoke(flow, "CloseDashboard"), null, null);
            Set(flow, "game", bootstrap);
            Set(flow, "dashboard", screen);
            Set(flow, "controls", new ControlBindings());
            Set(flow, "rules", rules);
            Set(flow, "elementNames", names);
            Invoke(flow, "Update");
            Invoke(flow, "CloseDashboard");
            Assert.That(Field<ulong>(bootstrap, "sequence"), Is.Zero);
        }

        [Test]
        public void UnobservedDashboardDoesNotAcknowledge()
        {
            var flow = screenRoot.AddComponent<ClientFlow>();
            Set(flow, "game", bootstrap);
            Invoke(flow, "CloseDashboard");
            Assert.That(Field<ulong>(bootstrap, "sequence"), Is.Zero);
        }

        [Test]
        public void MigrationCloseDefersOneIntentUntilTheSameJourneyCanSend()
        {
            typeof(ClientConnection).GetProperty("MigrationStatus").SetValue(connection, MigrationPhase.Recovering);
            bootstrap.RequestJourneyAcknowledgment();
            bootstrap.RequestJourneyAcknowledgment();
            Assert.That(Field<ulong>(bootstrap, "sequence"), Is.Zero);
            Assert.That(Field<long>(bootstrap, "pendingJourneyId"), Is.EqualTo(11));
            typeof(ClientConnection).GetProperty("MigrationStatus").SetValue(connection, MigrationPhase.None);
            Invoke(bootstrap, "FlushJourneyAcknowledgment");
            Invoke(bootstrap, "FlushJourneyAcknowledgment");
            Assert.That(Field<ulong>(bootstrap, "sequence"), Is.EqualTo(1UL));
            Assert.That(Field<long>(bootstrap, "pendingJourneyId"), Is.Zero);
            typeof(ClientConnection).GetProperty("MigrationStatus").SetValue(connection, MigrationPhase.Recovering);
            bootstrap.RequestJourneyAcknowledgment();
            SetFrame(13, Result(12));
            typeof(ClientConnection).GetProperty("MigrationStatus").SetValue(connection, MigrationPhase.None);
            Invoke(bootstrap, "FlushJourneyAcknowledgment");
            Assert.That(Field<ulong>(bootstrap, "sequence"), Is.EqualTo(1UL), "이전 여정의 확인을 새 여정에 보내지 않습니다.");
            Assert.That(Field<long>(bootstrap, "pendingJourneyId"), Is.Zero);
        }

        [Test]
        public void FinalResultSurvivesTransportCleanupAndNewConnectionClearsItAndPendingIntent()
        {
            typeof(ClientConnection).GetProperty("FinalJourney").SetValue(connection, Result(11));
            Set(bootstrap, "pendingJourneyId", 11L);
            Invoke(bootstrap, "DisconnectTransport");
            Assert.That(bootstrap.FinalJourney.Id, Is.EqualTo(11));
            Assert.That(bootstrap.FinalJourney.Total.Rescued, Is.EqualTo(102));
            Assert.That(Field<long>(bootstrap, "pendingJourneyId"), Is.Zero);
            Invoke(bootstrap, "DisconnectTransport");
            Assert.That(bootstrap.FinalJourney.Id, Is.EqualTo(11));
            using var next = new ClientConnection(rules, new NetworkSettings(document));
            Set(bootstrap, "pendingJourneyId", 11L);
            Invoke(bootstrap, "SetupTransport", next);
            Assert.That(bootstrap.FinalJourney.Id, Is.Zero);
            Assert.That(Field<long>(bootstrap, "pendingJourneyId"), Is.Zero);
            Set(bootstrap, "connection", null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResultKeepsDefeatAndVictoryDistinctAndReturnsToServerSelection(bool victory)
        {
            var screen = screenRoot.AddComponent<ClientScreen>();
            screen.Initialize(ui, 70, false);
            int selected = 0;
            screen.BuildResult(victory, () => selected++, Result(11));
            Assert.That(Text(screen, "Heading").text, Is.EqualTo(victory ? "전투 승리" : "원시핵이 붕괴했습니다"));
            Assert.That(Text(screen, "Journey total").text, Does.Contain("공성 승리 107"));
            Assert.That(Text(screen, "Journey since last").text, Does.Contain("구출 2"));
            screen.GetComponentsInChildren<Button>(true).Single(button => button.name == "다시 시작").onClick.Invoke();
            Assert.That(selected, Is.EqualTo(1));
        }

        [Test]
        public void MissingFinalJourneyUsesTheExistingResultWithoutInventedZeroHistory()
        {
            var screen = screenRoot.AddComponent<ClientScreen>();
            screen.Initialize(ui, 70, false);
            screen.BuildResult(false, () => { });
            Assert.That(screen.GetComponentsInChildren<TMP_Text>(true).Any(label => label.name == "Journey total"), Is.False);
            Assert.That(Text(screen, "Summary").text, Does.Contain("서버와 시작 원소"));
        }

        [Test]
        public void JourneyCountsKeepTheirInt64PrecisionInTheDisplayedText()
        {
            var screen = screenRoot.AddComponent<ClientScreen>();
            screen.Initialize(ui, 70, false);
            const long value = 9007199254740993;
            screen.BuildResult(false, () => { }, new JourneyResultView(11, new JourneyTotals(value, 0, 0, 0, 0, 0, 0), default));
            Assert.That(Text(screen, "Journey total").text, Does.Contain(value.ToString("N0")));
        }

        private static JourneyResultView Result(long id) => new JourneyResultView(id,
            new JourneyTotals(101, 102, 103, 104, 105, 106, 107), new JourneyTotals(1, 2, 3, 4, 5, 6, 7));
        private void SetFrame(long tick, JourneyResultView journey)
        {
            typeof(GameplayFrame).GetProperty("Tick").SetValue(connection.Gameplay.State, tick);
            typeof(GameplayFrame).GetProperty("Journey").SetValue(connection.Gameplay.State, journey);
        }
        private static TMP_Text Text(Component screen, string name) => screen.GetComponentsInChildren<TMP_Text>(true).Single(label => label.name == name);
        private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden).SetValue(target, value);
        private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden).Invoke(target, args);
    }
}
