using ElementalLegion.Client.Legion;
using NUnit.Framework;
using System.Numerics;
using System.Reflection;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.Editor;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;
using ElementalLegion.Client.UI;
using Vector2 = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class SquadControlTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void DestroyedUiSelectionDoesNotInterruptGameplayInput(bool focused)
        {
            var original = EventSystem.current;
            var events = new GameObject("Input gate events", typeof(EventSystem));
            var selection = new GameObject("Previous screen selection");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var system = events.GetComponent<EventSystem>();
                // EditMode에서는 EventSystem의 런타임 등록을 직접 재현한다.
                typeof(EventSystem).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(system, null);
                EventSystem.current = system;
                system.SetSelectedGameObject(selection);
                Object.DestroyImmediate(selection);
                Assert.That(ReferenceEquals(system.currentSelectedGameObject, selection), Is.True);
                Assert.That(selection == null, Is.True);
                Assert.That(AcceptsInput(root, focused), Is.EqualTo(focused));
            }
            finally
            {
                Object.DestroyImmediate(root);
                typeof(EventSystem).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(events.GetComponent<EventSystem>(), null);
                Object.DestroyImmediate(events);
                if (selection != null) Object.DestroyImmediate(selection);
                if (original != null) EventSystem.current = original;
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void HudTextSelectionDoesNotBlockGameplayWhileEditing(bool editing)
        {
            var original = EventSystem.current;
            var events = new GameObject("Input gate events", typeof(EventSystem));
            var selection = RuntimeUiControls.CreateInputField();
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var system = events.GetComponent<EventSystem>();
                typeof(EventSystem).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(system, null);
                EventSystem.current = system;
                system.SetSelectedGameObject(selection);
                var field = selection.GetComponent<TMP_InputField>();
                field.contentType = TMP_InputField.ContentType.IntegerNumber;
                // 네이티브 키보드를 열지 않고 입력 필드의 포커스 상태만 재현한다.
                typeof(TMP_InputField).GetField("m_AllowInput", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(field, editing);
                Assert.That(field.isFocused, Is.EqualTo(editing));
                Assert.That(AcceptsInput(root, true), Is.True);
                Assert.That(AcceptsInput(root, false), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(selection);
                typeof(EventSystem).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(events.GetComponent<EventSystem>(), null);
                Object.DestroyImmediate(events);
                if (original != null) EventSystem.current = original;
            }
        }

        [Test]
        public void OnlyChatCompositionBlocksMovementDuringHudUse()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                    "Packages/com.elementallegion.shared/Data/gamedata.json").text);
                var chat = new FieldChat(root.transform, new ObservatoryUi(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                    "Assets/_Project/Settings/UI/LegionUiSDF.asset")), new NetworkSettings(document), 5, .3f);
                var bootstrap = root.GetComponent<ClientBootstrap>();
                typeof(ClientBootstrap).GetField("chat", flags).SetValue(bootstrap, chat);
                var composer = (RectTransform)typeof(FieldChat).GetField("composer", flags).GetValue(chat);
                Assert.That(chat.Composing, Is.False);
                Assert.That(AcceptsInput(root, true), Is.True);
                composer.gameObject.SetActive(true);
                Assert.That(chat.Composing, Is.True);
                Assert.That(AcceptsInput(root, true), Is.False);
                composer.gameObject.SetActive(false);
                Assert.That(AcceptsInput(root, true), Is.True);
                bootstrap.SetInputBlocked(true);
                Assert.That(AcceptsInput(root, true), Is.False, "메뉴와 전환 화면의 입력 차단은 유지합니다.");
                bootstrap.SetInputBlocked(false);
                Assert.That(AcceptsInput(root, true), Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static bool AcceptsInput(GameObject root, bool focused) => (bool)typeof(ClientBootstrap)
            .GetMethod("AcceptsGameplayInput", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(root.GetComponent<ClientBootstrap>(), new object[] { focused });

        [TestCase(false)]
        [TestCase(true)]
        public void CommandTickWritesOnlyTheLatestPointAndReleaseHidesOnlyItsFeedback(bool recallFailed)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            var bootstrap = root.GetComponent<ClientBootstrap>();
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            using var connection = new ClientConnection(rules, new NetworkSettings(document));
            try
            {
                typeof(ClientConnection).GetProperty(nameof(ClientConnection.State)).SetValue(connection, ConnectionState.Connected);
                typeof(ClientBootstrap).GetField("rules", flags).SetValue(bootstrap, rules);
                typeof(ClientBootstrap).GetField("connection", flags).SetValue(bootstrap, connection);
                var controls = (SquadControlState)typeof(ClientBootstrap).GetField("squadControl", flags).GetValue(bootstrap);
                var tick = typeof(ClientBootstrap).GetMethod("TickMovement", flags);
                var writer = (NetDataWriter)typeof(ClientConnection).GetField("writer", flags).GetValue(connection);
                var hud = root.GetComponentInChildren<CombatHud>(true);
                var feedback = (TMP_Text)typeof(CombatHud).GetField("feedback", flags).GetValue(hud);
                controls.ObservePoint(true, true, true, false, new Vector2(10, 20));
                tick.Invoke(bootstrap, new object[] { Vector2.Zero, rules.DeltaTime / 2 });
                Assert.That(writer.Length, Is.Zero);
                controls.ObservePoint(true, false, true, false, new Vector2(30, 40));
                tick.Invoke(bootstrap, new object[] { Vector2.Zero, rules.DeltaTime / 2 });
                Assert.That(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var first), Is.True);
                Assert.That(first.Kind, Is.EqualTo(ElementalLegion.Shared.Sim.CommandKind.MoveTo));
                Assert.That(first.Sequence, Is.EqualTo(1));
                Assert.That(first.SquadMask, Is.EqualTo(1));
                Assert.That(first.Direction, Is.EqualTo(new Vector2(30, 40)));
                Assert.That(feedback.text, Does.Contain("30, 40"));
                if (recallFailed) hud.RecallFailed(ElementalLegion.Shared.Sim.CommandFailure.InvalidPlacement);
                byte[] sent = writer.CopyData();
                controls.ObservePoint(false, false, true, false, null);
                tick.Invoke(bootstrap, new object[] { Vector2.Zero, 0.0 });
                if (recallFailed) Assert.That(feedback.text, Does.Contain("귀환 공간"));
                else Assert.That(feedback.text, Is.Empty);
                tick.Invoke(bootstrap, new object[] { Vector2.Zero, rules.DeltaTime });
                Assert.That(writer.CopyData(), Is.EqualTo(sent));
                controls.ObservePoint(false, true, true, false, new Vector2(50, 60));
                tick.Invoke(bootstrap, new object[] { Vector2.Zero, rules.DeltaTime });
                Assert.That(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var second), Is.True);
                Assert.That(second.Sequence, Is.EqualTo(2));
                Assert.That(second.Direction, Is.EqualTo(new Vector2(50, 60)));
                Assert.That(feedback.text, Does.Not.Contain("50, 60"));
            }
            finally
            {
                typeof(ClientBootstrap).GetField("connection", flags).SetValue(bootstrap, null);
                Object.DestroyImmediate(root);
            }
        }
        [Test]
        public void PointHoldKeepsLatestDestinationAndShortClicksSurviveUntilCommandTick()
        {
            var controls = new SquadControlState();
            controls.ObservePoint(true, true, true, false, new Vector2(10, 20));
            controls.ObservePoint(true, false, true, false, new Vector2(30, 40));
            Assert.That(controls.TryTakePoint(out byte mask, out Vector2 destination, out bool pressed), Is.True);
            Assert.That(mask, Is.EqualTo(1));
            Assert.That(destination, Is.EqualTo(new Vector2(30, 40)));
            Assert.That(pressed, Is.True);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.False);
            controls.ObservePoint(true, false, true, false, new Vector2(35, 45));
            Assert.That(controls.TryTakePoint(out _, out destination, out pressed), Is.True);
            Assert.That(destination, Is.EqualTo(new Vector2(35, 45)));
            Assert.That(pressed, Is.False);
            controls.ObservePoint(false, false, true, false, null);
            controls.ObservePoint(false, true, true, false, new Vector2(50, 60));
            controls.ObservePoint(false, false, true, false, null);
            Assert.That(controls.TryTakePoint(out _, out destination, out pressed), Is.True);
            Assert.That(destination, Is.EqualTo(new Vector2(50, 60)));
            Assert.That(pressed, Is.True);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.False);
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void BlockedOrDirectInputCancelsPointHoldUntilRelease(bool allowed, bool direct)
        {
            var controls = new SquadControlState();
            controls.ObservePoint(true, true, true, false, Vector2.One);
            controls.ObservePoint(true, false, allowed, direct, Vector2.One);
            controls.ObservePoint(true, false, true, false, Vector2.One * 2);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.False);
            controls.ObservePoint(false, false, true, false, null);
            controls.ObservePoint(true, true, true, false, Vector2.One * 3);
            Assert.That(controls.TryTakePoint(out _, out Vector2 destination, out _), Is.True);
            Assert.That(destination, Is.EqualTo(Vector2.One * 3));
        }

        [Test]
        public void RejectedPressAndSelectionChangeNeverResumeTheSameHold()
        {
            var controls = new SquadControlState();
            controls.ObservePoint(true, true, false, false, Vector2.One);
            controls.ObservePoint(true, false, true, false, Vector2.One);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.False);
            controls.ObservePoint(false, false, true, false, null);
            controls.ObservePoint(true, true, true, false, Vector2.One);
            controls.Select(2);
            controls.ObservePoint(true, false, true, false, Vector2.One);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.False);
        }

        [Test]
        public void ReleasingOverBlockedUiAllowsTheNextFreshWorldPress()
        {
            var controls = new SquadControlState();
            controls.ObservePoint(true, true, false, false, null);
            controls.ObservePoint(false, false, false, false, null);
            controls.ObservePoint(true, true, true, false, Vector2.One);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.True);
        }

        [Test]
        public void LeavingTheViewportOrReselectingCancelsPendingPointMovement()
        {
            var controls = new SquadControlState();
            controls.ObservePoint(true, true, true, false, Vector2.One);
            controls.ObservePoint(true, false, true, false, null);
            controls.ObservePoint(true, false, true, false, Vector2.One);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.False);
            controls.ObservePoint(false, false, true, false, null);
            controls.ObservePoint(true, true, true, false, Vector2.One);
            controls.Select(1);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.False);
        }

        [Test]
        public void PointOrderClearsDirectStopOnlyWhenTaken()
        {
            var controls = new SquadControlState();
            controls.NextDirect(true, out _);
            controls.ObservePoint(true, true, true, false, Vector2.One);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.True);
            Assert.That(controls.NextDirect(false, out _), Is.False);
            controls.CancelPoint();
            controls.ObservePoint(true, false, true, false, Vector2.One);
            Assert.That(controls.TryTakePoint(out _, out _, out _), Is.False);
        }

        [Test]
        public void SelectionStopsOnlyTheLastDirectMaskAndIdleNeverCancelsPointOrders()
        {
            var controls = new SquadControlState();
            Assert.That(controls.NextDirect(false, out _), Is.False);
            Assert.That(controls.NextDirect(true, out byte mask), Is.True);
            Assert.That(mask, Is.EqualTo(1));
            Assert.That(controls.Select(4), Is.EqualTo(1));
            Assert.That(controls.Representative, Is.EqualTo(3));
            Assert.That(controls.NextDirect(false, out _), Is.False);
            Assert.That(controls.Select(7), Is.Zero);
            Assert.That(controls.Representative, Is.EqualTo(1));
            Assert.That(controls.NextDirect(true, out mask), Is.True);
            Assert.That(mask, Is.EqualTo(7));
            Assert.That(controls.NextDirect(false, out mask), Is.True);
            Assert.That(mask, Is.EqualTo(7));
            Assert.That(controls.NextDirect(false, out _), Is.False);
        }

        [Test]
        public void RightClickPlacementCancellationConsumesTheClickBeforeWorldMovement()
        {
            Assert.That(SquadControlState.ShouldMoveTo(true, false, true), Is.False);
            Assert.That(SquadControlState.ShouldMoveTo(true, true, false), Is.False);
            Assert.That(SquadControlState.ShouldMoveTo(false, false, false), Is.False);
            Assert.That(SquadControlState.ShouldMoveTo(true, false, false), Is.True);
        }
    }
}
