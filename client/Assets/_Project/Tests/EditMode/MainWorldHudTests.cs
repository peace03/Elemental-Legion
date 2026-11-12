using System;
using System.Collections.Generic;
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
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    /// <summary>권위 HUD와 선택을 보존하며 안내와 밀집 타이머를 제한하는지 검증한다.</summary>
    public sealed class MainWorldHudTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root;
        private CombatHud hud;
        private GameplayFrame frame;
        private GameRules rules;

        [SetUp]
        public void SetUp()
        {
            root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            hud = root.GetComponentInChildren<CombatHud>(true);
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            rules = new GameRules(document);
            hud.Configure(document, rules);
            hud.BuildObservatory(new ObservatoryUi(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/_Project/Settings/UI/LegionUiSDF.asset")), rules, root.GetComponent<ElementalVisuals>());
            frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), Hidden, null, new object[] { rules }, null);
            Set("Tick", 100L); Set("PlayerId", 1); Set("BaseLevel", 1); Set("PopulationCap", rules.PopulationCap(1));
            Set("SpatialReady", true);
            var squads = Buffer<SquadView>("SquadBuffer");
            for (byte id = 1; id <= 4; id++) squads[id - 1] = new SquadView(id, new Point(100), default,
                default, default, SquadOrderKind.Hold, 0, 3, 0, 0);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void CorePickupFeedbackGroupsOwnReceiptsAndUpdatesTheAuthoritativeBalanceImmediately()
        {
            var label = Field<TMP_Text>("corePickupLabel");
            var icon = Field<RectTransform>("corePickupIcon");
            var glow = Field<LegionUiFrame>("corePickupGlow");
            var events = Buffer<NetworkEvent>("EventBuffer");
            events[0] = Pickup(1, 2, 50);
            Count("EventCount", 1);
            Set("CoreBalance", 128L);
            hud.Apply(frame, 10);
            Assert.That(label.gameObject.activeSelf, Is.False);

            events[0] = Pickup(2, 1, 5);
            events[1] = Pickup(3, 1, 7, collector: 8);
            Count("EventCount", 2);
            Set("CoreBalance", 131L);
            hud.Apply(frame, 10.1);
            Assert.That(Field<TMP_Text>("economy").text, Does.StartWith("핵 131"));
            Assert.That(label.text, Is.EqualTo("+12"));
            Assert.That(label.gameObject.activeSelf, Is.True);
            label.ForceMeshUpdate();
            Assert.That(label.textInfo.characterCount, Is.EqualTo(3), "획득량이 글자 높이 때문에 잘리지 않아야 합니다.");
            Assert.That(label.textInfo.characterInfo[0].isVisible, Is.True);
            Vector2 start = label.rectTransform.anchoredPosition;
            hud.TickCorePickupFeedback(10.18);
            Assert.That(icon.localScale.x, Is.GreaterThan(1.1f));
            Assert.That(glow.enabled, Is.True);
            Assert.That(glow.rectTransform.localScale.x, Is.GreaterThan(1));
            Assert.That(label.rectTransform.anchoredPosition.y, Is.GreaterThan(start.y));

            hud.Apply(frame, 10.19);
            Assert.That(label.text, Is.EqualTo("+12"), "이미 표시한 사건은 합산하지 않습니다.");
            events[0] = Pickup(4, 1, 3, spatial: false);
            Count("EventCount", 1);
            Set("SpatialReady", false);
            Set("CoreBalance", 134L);
            hud.Apply(frame, 10.28);
            Assert.That(label.text, Is.EqualTo("+15"));
            Assert.DoesNotThrow(() => root.GetComponent<ElementalVisuals>().Play(events[0]),
                "수량만 받은 사건은 전장 효과의 초기화나 위치에 의존하지 않습니다.");
            hud.TickCorePickupFeedback(10.46);
            Assert.That(icon.localScale, Is.EqualTo(Vector3.one));
            Assert.That(glow.enabled, Is.False);
            Assert.That(label.gameObject.activeSelf, Is.True);

            events[0] = Pickup(5, 1, 2);
            hud.Apply(frame, 10.75);
            Assert.That(label.text, Is.EqualTo("+2"), "연속 획득 간격을 벗어나면 새 묶음입니다.");
            hud.TickCorePickupFeedback(11.8);
            Assert.That(label.gameObject.activeSelf, Is.False);
            Count("EventCount", 0);
            Set("CoreBalance", 160L);
            hud.Apply(frame, 11.9);
            Assert.That(Field<TMP_Text>("economy").text, Does.StartWith("핵 160"));
            Assert.That(label.gameObject.activeSelf, Is.False, "잔고 변화만으로 획득 효과를 재생하지 않습니다.");
        }

        [Test]
        public void CorePickupFeedbackStaysStillInReducedMotionAndAcceptsTheNewSessionsSerial()
        {
            Field<ObservatoryUi>("skin").ReduceMotion = true;
            var label = Field<TMP_Text>("corePickupLabel");
            var icon = Field<RectTransform>("corePickupIcon");
            var glow = Field<LegionUiFrame>("corePickupGlow");
            Buffer<NetworkEvent>("EventBuffer")[0] = Pickup(1, 1, 4);
            Count("EventCount", 1);
            hud.Apply(frame, 10);
            Vector2 position = label.rectTransform.anchoredPosition;
            hud.TickCorePickupFeedback(10.08);
            Assert.That(label.rectTransform.anchoredPosition, Is.EqualTo(position));
            Assert.That(label.text, Is.EqualTo("+4"));
            Assert.That(icon.localScale, Is.EqualTo(Vector3.one));
            Assert.That(glow.enabled, Is.True);
            Assert.That(glow.rectTransform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(label.raycastTarget || glow.raycastTarget, Is.False);

            hud.ResetHud();
            Assert.That(label.gameObject.activeSelf || glow.enabled, Is.False);
            hud.Apply(frame, 20);
            Assert.That(label.gameObject.activeSelf, Is.True);
            Assert.That(label.text, Is.EqualTo("+4"));
        }

        [Test]
        public void RecallSupersedesThePointQueuedBeforeIt()
        {
            var bootstrap = root.GetComponent<ClientBootstrap>();
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            using var connection = new ClientConnection(rules, new NetworkSettings(document));
            var connectionField = typeof(ClientBootstrap).GetField("connection", Hidden);
            try
            {
                typeof(ClientConnection).GetProperty(nameof(ClientConnection.State)).SetValue(connection, ConnectionState.Connected);
                connectionField.SetValue(bootstrap, connection);
                typeof(ClientBootstrap).GetField("rules", Hidden).SetValue(bootstrap, rules);
                var controls = (SquadControlState)typeof(ClientBootstrap).GetField("squadControl", Hidden).GetValue(bootstrap);
                var writer = (NetDataWriter)typeof(ClientConnection).GetField("writer", Hidden).GetValue(connection);
                hud.SelectSquad(1);
                hud.Apply(frame);
                controls.ObservePoint(false, true, true, false, new Point(90, 100));

                typeof(ClientBootstrap).GetMethod("Recall", Hidden).Invoke(bootstrap, null);
                Assert.That(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var recall), Is.True);
                Assert.That(recall.Kind, Is.EqualTo(CommandKind.Recall));
                byte[] sent = writer.CopyData();

                typeof(ClientBootstrap).GetMethod("TickMovement", Hidden).Invoke(bootstrap, new object[] { Point.Zero, rules.DeltaTime });
                Assert.That(writer.CopyData(), Is.EqualTo(sent), "이미 놓은 우클릭이 새 귀환 명령을 취소하면 안 됩니다.");
            }
            finally { connectionField.SetValue(bootstrap, null); }
        }

        [Test]
        public void ManagementButtonsOpenOnePanelAndResetClosesIt()
        {
            hud.Apply(frame);
            var buttons = hud.GetComponentsInChildren<UnityEngine.UI.Button>(true);
            var training = Field<RectTransform>("trainingPanel").gameObject;
            var forces = Field<RectTransform>("forcesPanel").gameObject;
            Assert.That(training.activeSelf || forces.activeSelf, Is.False);
            Array.Find(buttons, button => button.name == "양성").onClick.Invoke();
            Assert.That(training.activeSelf, Is.True);
            Array.Find(buttons, button => button.name == "병력").onClick.Invoke();
            Assert.That(training.activeSelf, Is.False);
            Assert.That(forces.activeSelf, Is.True);
            Array.Find(buttons, button => button.name == "병력").onClick.Invoke();
            Assert.That(forces.activeSelf, Is.False);
            Array.Find(buttons, button => button.name == "양성").onClick.Invoke();
            hud.ResetHud();
            hud.Apply(frame);
            Assert.That(training.activeSelf || forces.activeSelf, Is.False);
        }

        [Test]
        public void GuardReturnSendsSelectedMembersAndCountsIncomingReservations()
        {
            hud.Apply(frame);
            var button = Array.Find(hud.GetComponentsInChildren<UnityEngine.UI.Button>(true), item => item.name == "Guard return");
            Assert.That(button, Is.Not.Null, "선택한 야전 병력을 본진으로 보내는 버튼이 있어야 합니다.");
            byte target = 0;
            int requested = 0;
            hud.TransferRequested += (squad, count) => { target = squad; requested = count; };
            Assert.That(button.interactable, Is.True);
            button.onClick.Invoke();
            Assert.That(target, Is.EqualTo(4));
            Assert.That(requested, Is.EqualTo(3));

            int cap = rules.GarrisonCap(1);
            Buffer<SquadView>("SquadBuffer")[3] = new SquadView(4, new Point(100), default, default,
                default, SquadOrderKind.Hold, 0, cap - 2, 1, 0);
            hud.Apply(frame);
            button.onClick.Invoke();
            Assert.That(requested, Is.EqualTo(1), "복귀 중인 병력도 수비대 자리를 차지합니다.");
            Buffer<SquadView>("SquadBuffer")[3] = new SquadView(4, new Point(100), default, default,
                default, SquadOrderKind.Hold, 0, cap - 2, 1, 1);
            hud.Apply(frame);
            Assert.That(button.interactable, Is.False);
            hud.SelectSquad(7);
            Assert.That(button.interactable, Is.False);
            hud.SelectSquad(8);
            Assert.That(button.interactable, Is.False);
        }

        [Test]
        public void GuardReturnWaitsForAnInitializedBaseLevel()
        {
            Set("BaseLevel", 0);
            Assert.DoesNotThrow(() => hud.Apply(frame));
            var button = Array.Find(hud.GetComponentsInChildren<UnityEngine.UI.Button>(true), item => item.name == "Guard return");
            Assert.That(button.interactable, Is.False);
        }

        [Test]
        public void MainWorldHudPreservesSelectionAndExplainsFailures()
        {
            Buffer<BuildingView>("BuildingBuffer")[0] = Base(1, 250, 1000);
            Buffer<BuildingView>("BuildingBuffer")[1] = Base(2, 1, 9000);
            Count("BuildingCount", 2);
            Buffer<SquadView>("SquadBuffer")[1] = new SquadView(2, new Point(100), default, default,
                new Point(140, 150), SquadOrderKind.MoveTo, 0, 4, 2, 3);
            hud.SelectSquad(2);
            hud.Apply(frame);
            hud.SetPanel(HudPanelKind.Forces);
            Assert.That(Field<TMP_Text>("mapStatus").text, Does.Contain("250/1,000").And.Not.Contain("9,000"));
            Assert.That(Field<TMP_Text[]>("forceRows")[1].text, Does.Contain("합류 4").And.Contain("증원 2").And.Contain("예약 3").And.Contain("140"));
            hud.RecallFailed(CommandFailure.InvalidPlacement);
            Assert.That(Field<TMP_Text>("feedback").text, Does.Contain("귀환 공간"));
            Set("RecallSquad", (byte)2); Set("RecallEndTick", 160L); hud.Apply(frame);
            Assert.That(Field<TMP_Text>("recallStatus").text, Does.Contain("2번").And.Contain("3.0"));
            Set("RecallSquad", (byte)0); Set("RecallEndTick", 0L); Set("RecallReadyTick", 1300L); hud.Apply(frame);
            Assert.That(Field<TMP_Text>("recallStatus").text, Does.Contain("60.0"));
            Assert.That(Field<byte>("selectedSquadMask"), Is.EqualTo(2));
            hud.ShowRally(2, 3, null, true);
            Assert.That(Field<TMP_Text>("mapStatus").text, Does.Contain("250/1,000"));
            Assert.That(Field<TMP_Text>("rallyStatus").text, Does.Contain("이동 중 증원 2기").And.Contain("본진 대기 3기"));
        }

        [Test]
        public void GuidanceFollowsConfirmedRewardForgeTrainingAndDestinationThenUsesConfiguredKeys()
        {
            hud.Apply(frame);
            var guidance = Field<TMP_Text>("guidance");
            Assert.That(guidance.text, Does.Contain("첫 보상"));
            Set("Journey", new JourneyResultView(1, new JourneyTotals(0, 0, 0, rules.StartingCores + 1L, 0, 0, 0), default));
            hud.Apply(frame);
            Assert.That(guidance.text, Does.Contain("자연 양성소").And.Contain(rules.BuildingCost(BuildingKind.NatureForge).ToString("N0")));
            Buffer<BuildingView>("BuildingBuffer")[0] = new BuildingView(3, 1, BuildingKind.NatureForge, new Point(100), 2);
            Count("BuildingCount", 1);
            hud.Apply(frame);
            Assert.That(guidance.text, Does.Contain("양성").And.Contain(rules.TrainingCost(0, 0).ToString("N0")));
            Set("TrainingElement", 0); Set("TrainingTargetSquad", (byte)3); Set("TrainingEndTick", 160L); hud.Apply(frame);
            Assert.That(guidance.text, Does.Contain("목적 분대 3"));
            Set("TrainingElement", -1); Set("TrainedCount", 1L);
            var controls = new ControlBindings();
            Assert.That(controls.TrySet(ControlBindings.BaseFocus, UnityEngine.InputSystem.Key.F6, out _), Is.True);
            Assert.That(controls.TrySet(ControlBindings.Recall, UnityEngine.InputSystem.Key.F7, out _), Is.True);
            hud.ApplyPreferences(controls, 100); hud.Apply(frame);
            Assert.That(guidance.text, Does.Contain("F6").And.Contain("F7").And.Contain("연습"));
            hud.SelectSquad(2);
            hud.SetGuidanceEnabled(false);
            Assert.That(guidance.gameObject.activeSelf, Is.False);
            Assert.That(hud.TryGetRecall(out byte squad), Is.True);
            Assert.That(squad, Is.EqualTo(2));
            hud.ShowBaseUnderAttack(Time.unscaledTimeAsDouble);
            Assert.That(Field<TMP_Text>("defenseAlert").gameObject.activeSelf, Is.True);
            hud.SetGuidanceEnabled(true);
            Assert.That(guidance.text, Does.Contain("실제 경보").And.Contain("F6"));
        }

        [Test]
        public void DenseWorldTimersAreBoundedAndOwnCandidateProgressHasPriority()
        {
            typeof(CombatHud).GetField("maxWorldTimers", Hidden).SetValue(hud, 4);
            var camera = root.GetComponent<ElementalVisuals>().TargetCamera;
            camera.transform.SetPositionAndRotation(new Vector3(100, 30, 100), Quaternion.Euler(90, 0, 0));
            var candidates = Buffer<ConversionCandidateView>("CandidateBuffer");
            for (int index = 0; index < candidates.Length; index++)
                candidates[index] = new ConversionCandidateView(index + 1, 0, new Point(100), 300,
                    index == candidates.Length - 1 ? RecoveryRole.Rescue : RecoveryRole.Observer, index == candidates.Length - 1 ? 30 : 0);
            Count("CandidateCount", candidates.Length);
            hud.Apply(frame);
            Tick();
            var pool = Field<List<TMP_Text>>("buildingTimers");
            Assert.That(pool.Count, Is.EqualTo(4));
            Assert.That(pool[0].text, Does.Contain("구출").And.Contain("1.5"));
            Assert.That(pool[1].text, Does.Contain("후보").And.Not.Contain("/"));
            Assert.That(Field<TMP_Text>("worldTimerSummary").text, Does.Contain((candidates.Length - 4).ToString("N0")));
            Tick();
            Assert.That(pool.Count, Is.EqualTo(4));
            typeof(CombatHud).GetField("maxWorldTimers", Hidden).SetValue(hud, 2);
            Tick();
            Assert.That(pool.Count, Is.EqualTo(2));
            Count("CandidateCount", 0); Tick();
            Assert.That(pool.TrueForAll(label => !label.gameObject.activeSelf), Is.True);
            Assert.That(Field<TMP_Text>("worldTimerSummary").gameObject.activeSelf, Is.False);
        }

        [Test]
        public void BootstrapSynchronizesDefaultSelectionAndKeepsThePredictionRepresentative()
        {
            var bootstrap = root.GetComponent<ClientBootstrap>();
            typeof(ClientBootstrap).GetField("rules", Hidden).SetValue(bootstrap, rules);
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            typeof(ClientBootstrap).GetField("settings", Hidden).SetValue(bootstrap, new NetworkSettings(document));
            using var connection = new ClientConnection(rules, new NetworkSettings(document));
            typeof(ClientConnection).GetProperty("State").SetValue(connection, ConnectionState.Connected);
            try
            {
                typeof(ClientBootstrap).GetMethod("SetupTransport", Hidden).Invoke(bootstrap, new object[] { connection });
                var visuals = root.GetComponent<ElementalVisuals>();
                Assert.That(visuals.SelectedSquads, Is.EqualTo(1));
                Assert.That(Field<byte>("selectedSquadMask"), Is.EqualTo(1));
                typeof(ClientBootstrap).GetMethod("SelectSquad", Hidden).Invoke(bootstrap, new object[] { (byte)4 });
                var control = (SquadControlState)typeof(ClientBootstrap).GetField("squadControl", Hidden).GetValue(bootstrap);
                Assert.That(visuals.SelectedSquads, Is.EqualTo(4));
                Assert.That(control.Representative, Is.EqualTo(3));
                hud.Apply(frame);
                Assert.That(visuals.SelectedSquads, Is.EqualTo(4));
                Assert.That(Field<byte>("selectedSquadMask"), Is.EqualTo(4));
            }
            finally { typeof(ClientBootstrap).GetField("connection", Hidden).SetValue(bootstrap, null); }
        }

        [Test]
        public void PresentationSettingsExposeGuidanceWithoutRemovingAnyControlBinding()
        {
            var holder = new GameObject("Guidance settings");
            holder.transform.SetParent(root.transform, false);
            holder.SetActive(false);
            var screen = holder.AddComponent<ClientSettingsScreen>();
            var preferences = new ClientPreferences();
            var controls = new ControlBindings();
            var ui = new ObservatoryUi(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset"));
            screen.Build(ui, preferences, controls, () => { }, () => { }, _ => { });
            var choice = (TMP_Dropdown)typeof(ClientSettingsScreen).GetField("guidance", Hidden).GetValue(screen);
            Assert.That(choice.options[1].text, Is.EqualTo("켜기"));
            Assert.That(choice.value, Is.EqualTo(preferences.Guidance ? 1 : 0));
            var keys = (TMP_Text[])typeof(ClientSettingsScreen).GetField("keyLabels", Hidden).GetValue(screen);
            Assert.That(keys.Length, Is.EqualTo(18));
            bool previous = preferences.Guidance;
            choice.value = previous ? 0 : 1;
            Assert.That(preferences.Guidance, Is.EqualTo(previous), "기존 표현 설정처럼 적용할 때 저장합니다.");
        }

        [Test]
        public void GuidanceDefaultsOnAndLoadsAnOffPreferenceWithoutChangingExistingKeys()
        {
            const string key = "ElementalLegion.Guidance";
            bool existed = PlayerPrefs.HasKey(key);
            int previous = PlayerPrefs.GetInt(key);
            string controls = PlayerPrefs.GetString("ElementalLegion.Controls");
            try
            {
                PlayerPrefs.DeleteKey(key);
                Assert.That(new ClientPreferences().Guidance, Is.True);
                PlayerPrefs.SetInt(key, 0);
                Assert.That(new ClientPreferences().Guidance, Is.False);
                Assert.That(PlayerPrefs.GetString("ElementalLegion.Controls"), Is.EqualTo(controls));
                Assert.That(ControlBindings.Count, Is.EqualTo(18));
            }
            finally
            {
                if (existed) PlayerPrefs.SetInt(key, previous); else PlayerPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void DenseHudTextDoesNotTruncateCountsHealthOrOverlapTrainingLines()
        {
            Set("BaseLevel", 4); Set("CoreBalance", 10000L);
            hud.SetPanel(HudPanelKind.Training);
            Buffer<BuildingView>("BuildingBuffer")[0] = Base(1, 2000, 2000);
            Count("BuildingCount", 1);
            hud.Apply(frame);
            hud.ShowRally(20, 20, null, true);
            AssertFits(Field<TMP_Text>("mapStatus"));
            foreach (TMP_Text label in Field<TMP_Text[]>("squadLabels")) AssertFits(label);
            for (int element = 0; element < rules.Elements.Count; element++)
            {
                Set("MainElement", element);
                hud.Apply(frame);
                foreach (TMP_Text label in Field<TMP_Text[]>("labels"))
                {
                    AssertFits(label);
                    for (int line = 1; line < label.textInfo.lineCount; line++)
                        Assert.That(label.textInfo.lineInfo[line - 1].descender,
                            Is.GreaterThanOrEqualTo(label.textInfo.lineInfo[line].ascender), label.text);
                }
            }
        }

        private static void AssertFits(TMP_Text label)
        {
            label.ForceMeshUpdate(true, true);
            Assert.That(label.isTextTruncated, Is.False, label.text);
        }

        [Test]
        public void StaticFontContainsTheMainWorldControlsAndFeedback()
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
            Assert.That(font.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Static));
            Assert.That(font.HasCharacters("분대 지휘 현황 예약 증원 양자 이동 본진 귀환 건설 경보 확보 구출 목적지 조작 안내 수비대 제외"), Is.True);
            Assert.That(font.HasCharacter('꾼', searchFallbacks: true, tryAddCharacter: true), Is.True);
        }

        [Test]
        public void EnlargedHudKeepsHeaderSquadsAndMapApartAndShowsTheEntireRecallStatus()
        {
            var canvas = hud.GetComponentInParent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.GetComponent<UnityEngine.UI.CanvasScaler>().enabled = false;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(1280, 720) / 1.1f;
            Canvas.ForceUpdateCanvases();
            hud.ApplyPreferences(new ControlBindings(), 110);
            hud.Apply(frame);
            hud.SetPanel(HudPanelKind.Training);
            RectTransform content = Field<RectTransform>("hudRect");
            Bounds header = RectTransformUtility.CalculateRelativeRectTransformBounds(content, Field<RectTransform>("hudHeader"));
            Bounds squads = RectTransformUtility.CalculateRelativeRectTransformBounds(content, content.Find("Squad commands"));
            Bounds map = RectTransformUtility.CalculateRelativeRectTransformBounds(content, Field<RectTransform>("mapPanel"));
            Bounds basePanel = RectTransformUtility.CalculateRelativeRectTransformBounds(content, Field<RectTransform>("basePanel"));
            Bounds management = RectTransformUtility.CalculateRelativeRectTransformBounds(content, Field<RectTransform>("trainingPanel"));
            Assert.That(squads.max.y, Is.LessThan(header.min.y));
            Assert.That(map.max.x, Is.LessThan(squads.min.x));
            Assert.That(management.max.y, Is.LessThan(basePanel.min.y));
            Assert.That(management.min.y, Is.GreaterThan(squads.max.y));
            hud.SelectSquad(2);
            Set("RecallSquad", (byte)2); Set("RecallEndTick", 115L);
            hud.Apply(frame);
            AssertFits(Field<TMP_Text>("recallStatus"));
        }

        private static BuildingView Base(int owner, float health, float maximum) =>
            new BuildingView(owner, owner, BuildingKind.Base, new Point(100), 6, health, maximum, -1, 0);

        private static NetworkEvent Pickup(ulong serial, int owner, int amount, int collector = 0, bool spatial = true)
        {
            var sourceKind = collector > 0 ? ObservedEntityKind.Building : ObservedEntityKind.Unit;
            var data = (SimEvent)Activator.CreateInstance(typeof(SimEvent), Hidden, null, new object[]
            {
                SimEventKind.CorePickedUp, owner, collector, 0, spatial ? new Point(100) : Point.Zero, (float)amount,
                0, 0UL, -1, Point.Zero, false, spatial ? (int)serial : 0, ObservedEntityKind.Core, sourceKind, ObservedEntityKind.Unit
            }, null);
            return (NetworkEvent)Activator.CreateInstance(typeof(NetworkEvent), Hidden, null, new object[] { serial, 100L, data }, null);
        }

        private T Field<T>(string name) => (T)typeof(CombatHud).GetField(name, Hidden).GetValue(hud);
        private T[] Buffer<T>(string name) => (T[])typeof(GameplayFrame).GetField(name, Hidden).GetValue(frame);
        private void Set(string name, object value) => typeof(GameplayFrame).GetProperty(name).SetValue(frame, value);
        private void Count(string name, int value) => typeof(GameplayFrame).GetField(name, Hidden).SetValue(frame, value);
        private void Tick() => typeof(CombatHud).GetMethod("LateUpdate", Hidden).Invoke(hud, null);
    }
}
