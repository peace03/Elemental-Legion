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
            Assert.That(Field<TMP_Text>("mapStatus").text, Does.Contain("250/1,000").And.Not.Contain("9,000"));
            Assert.That(Field<TMP_Text>("squadStatus").text, Does.Contain("합류 4").And.Contain("증원 2").And.Contain("예약 3").And.Contain("140"));
            hud.RecallFailed(CommandFailure.InvalidPlacement);
            Assert.That(Field<TMP_Text>("feedback").text, Does.Contain("귀환 공간"));
            Set("RecallSquad", (byte)2); Set("RecallEndTick", 160L); hud.Apply(frame);
            Assert.That(Field<TMP_Text>("recallStatus").text, Does.Contain("2번").And.Contain("3.0"));
            Set("RecallSquad", (byte)0); Set("RecallEndTick", 0L); Set("RecallReadyTick", 1300L); hud.Apply(frame);
            Assert.That(Field<TMP_Text>("recallStatus").text, Does.Contain("60.0"));
            Assert.That(Field<byte>("selectedSquadMask"), Is.EqualTo(2));
            hud.ShowRally(2, 3, null, true);
            Assert.That(Field<TMP_Text>("mapStatus").text, Does.Contain("250/1,000").And.Contain("집결"));
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
            RectTransform content = Field<RectTransform>("hudRect");
            Bounds header = RectTransformUtility.CalculateRelativeRectTransformBounds(content, Field<RectTransform>("hudHeader"));
            Bounds squads = RectTransformUtility.CalculateRelativeRectTransformBounds(content, content.Find("Squad commands"));
            Bounds map = RectTransformUtility.CalculateRelativeRectTransformBounds(content, Field<RectTransform>("mapPanel"));
            Assert.That(squads.max.y, Is.LessThan(header.min.y));
            Assert.That(map.max.y, Is.LessThan(squads.min.y));
            hud.SelectSquad(2);
            Set("RecallSquad", (byte)2); Set("RecallEndTick", 115L);
            hud.Apply(frame);
            AssertFits(Field<TMP_Text>("recallStatus"));
        }

        private static BuildingView Base(int owner, float health, float maximum) =>
            new BuildingView(owner, owner, BuildingKind.Base, new Point(100), 6, health, maximum, -1, 0);
        private T Field<T>(string name) => (T)typeof(CombatHud).GetField(name, Hidden).GetValue(hud);
        private T[] Buffer<T>(string name) => (T[])typeof(GameplayFrame).GetField(name, Hidden).GetValue(frame);
        private void Set(string name, object value) => typeof(GameplayFrame).GetProperty(name).SetValue(frame, value);
        private void Count(string name, int value) => typeof(GameplayFrame).GetField(name, Hidden).SetValue(frame, value);
        private void Tick() => typeof(CombatHud).GetMethod("LateUpdate", Hidden).Invoke(hud, null);
    }
}
