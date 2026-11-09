using System;
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
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    /// <summary>귀환 입력이 선택한 야전 분대만 보내고 소유자 상태를 표시하는지 확인한다.</summary>
    public sealed class RecallHudTests
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
            Set("Tick", 100L);
            var squads = (SquadView[])typeof(GameplayFrame).GetField("SquadBuffer", Hidden).GetValue(frame);
            for (byte id = 1; id <= 4; id++)
                squads[id - 1] = new SquadView(id, new WorldPoint(100, 100), default, default, default,
                    SquadOrderKind.Hold, 0, 3, 0, 0);
            hud.Apply(frame);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void RecallAcceptsOneFieldOnlyAndShowsCastAndSharedCooldown()
        {
            foreach (byte mask in new byte[] { 1, 2, 4 })
            {
                hud.SelectSquad(mask);
                Assert.That(hud.TryGetRecall(out byte squad), Is.True);
                Assert.That(squad, Is.EqualTo(mask == 4 ? 3 : mask));
                Assert.That(Field<Button>("recall").interactable, Is.True);
            }
            foreach (byte mask in new byte[] { 7, 8 })
            {
                hud.SelectSquad(mask);
                Assert.That(hud.TryGetRecall(out _), Is.False);
                Assert.That(Field<Button>("recall").interactable, Is.False);
            }
            hud.SelectSquad(2);
            Set("RecallSquad", (byte)2);
            Set("RecallEndTick", 160L);
            hud.Apply(frame);
            Assert.That(Field<TMP_Text>("recallStatus").text, Does.Contain("2번").And.Contain("3.0"));
            Assert.That(hud.TryGetRecall(out _), Is.False);
            hud.SelectSquad(4);
            Assert.That(Field<TMP_Text>("recallStatus").text, Does.Contain("2번"));
            Set("RecallSquad", (byte)0);
            Set("RecallEndTick", 0L);
            Set("RecallReadyTick", 1300L);
            hud.Apply(frame);
            Assert.That(Field<TMP_Text>("recallStatus").text, Does.Contain("60.0"));
            Assert.That(hud.TryGetRecall(out _), Is.False);
            Set("Tick", 1300L);
            hud.Apply(frame);
            Assert.That(hud.TryGetRecall(out _), Is.True);
            var controls = new ControlBindings();
            controls.TrySet(ControlBindings.Recall, UnityEngine.InputSystem.Key.F3, out _);
            hud.ApplyPreferences(controls, 100);
            Assert.That(Field<TMP_Text>("recallLabel").text, Does.Contain("F3"));
            hud.RecallFailed(CommandFailure.RecallInterrupted);
            Assert.That(Field<TMP_Text>("feedback").text, Does.Contain("취소"));
            hud.RecallFailed(CommandFailure.InvalidPlacement);
            Assert.That(Field<TMP_Text>("feedback").text, Does.Contain("귀환").And.Contain("공간"));
            hud.ResetHud();
            Assert.That(Field<Button>("recall").interactable, Is.False);
        }

        [Test]
        public void KeyboardAndButtonRecallSendTheSameSelectedSquadAndRejectAllAndGuard()
        {
            var bootstrap = root.GetComponent<ClientBootstrap>();
            using var connection = new ClientConnection(rules, new NetworkSettings(document));
            typeof(ClientConnection).GetProperty(nameof(ClientConnection.State)).SetValue(connection, ConnectionState.Connected);
            typeof(ClientBootstrap).GetField("connection", Hidden).SetValue(bootstrap, connection);
            var selection = (SquadControlState)typeof(ClientBootstrap).GetField("squadControl", Hidden).GetValue(bootstrap);
            var send = typeof(ClientBootstrap).GetMethod("Recall", Hidden);
            Action request = () => send.Invoke(bootstrap, null);
            hud.RecallRequested += request;
            try
            {
                ulong expected = 0;
                foreach (byte mask in new byte[] { 1, 2, 4 })
                    foreach (bool button in new[] { false, true })
                    {
                        selection.Select(mask);
                        hud.SelectSquad(mask);
                        if (button) Field<Button>("recall").onClick.Invoke(); else request();
                        var writer = (NetDataWriter)typeof(ClientConnection).GetField("writer", Hidden).GetValue(connection);
                        Assert.That(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var command), Is.True);
                        Assert.That(command.Kind, Is.EqualTo(CommandKind.Recall));
                        Assert.That(command.Sequence, Is.EqualTo(++expected));
                        Assert.That(command.TargetSquad, Is.EqualTo(selection.Representative));
                    }
                foreach (byte mask in new byte[] { 7, 8 })
                {
                    selection.Select(mask);
                    hud.SelectSquad(mask);
                    request();
                    Field<Button>("recall").onClick.Invoke();
                    Assert.That(typeof(ClientBootstrap).GetField("sequence", Hidden).GetValue(bootstrap), Is.EqualTo(expected));
                }
            }
            finally
            {
                hud.RecallRequested -= request;
                typeof(ClientBootstrap).GetField("connection", Hidden).SetValue(bootstrap, null);
            }
        }

        [Test]
        public void ConstructionPreviewRejectsOwnReservedSlotAndAllowsClearSpaceAndDome()
        {
            var slots = new[] { new WorldPoint(20, 20) };
            Assert.That(ConstructionPanel.OverlapsRecallSlots(BuildingKind.Collector, new WorldPoint(20, 20), 2, slots), Is.True);
            Assert.That(ConstructionPanel.OverlapsRecallSlots(BuildingKind.Collector, new WorldPoint(22, 20), 2, slots), Is.False);
            Assert.That(ConstructionPanel.OverlapsRecallSlots(BuildingKind.Collector, new WorldPoint(30, 20), 2, slots), Is.False);
            Assert.That(ConstructionPanel.OverlapsRecallSlots(BuildingKind.Dome, new WorldPoint(20, 20), 2, slots), Is.False);
        }

        [Test]
        public void AuthoritativeCastKeepsItsFailureReasonAfterAnEarlyRepeatedRequest()
        {
            var bootstrap=root.GetComponent<ClientBootstrap>();
            using var connection=new ClientConnection(rules,new NetworkSettings(document));
            typeof(ClientBootstrap).GetField("connection",Hidden).SetValue(bootstrap,connection);
            typeof(ClientBootstrap).GetField("prediction",Hidden).SetValue(bootstrap,new MovementPrediction(rules));
            typeof(ClientBootstrap).GetField("timeline",Hidden).SetValue(bootstrap,new SnapshotTimeline(4));
            typeof(ClientBootstrap).GetField("recallCommand",Hidden).SetValue(bootstrap,2UL);
            var events=(NetworkEvent[])typeof(GameplayFrame).GetField("EventBuffer",Hidden).GetValue(frame);
            events[0]=Event(1,SimEventKind.CommandApplied,(int)CommandKind.Recall);
            events[1]=Event(2,SimEventKind.CommandRejected,(int)CommandFailure.InvalidPlacement);
            typeof(GameplayFrame).GetField("EventCount",Hidden).SetValue(frame,2);
            try
            {
                typeof(ClientBootstrap).GetMethod("OnGameplay",Hidden).Invoke(bootstrap,new object[]{frame});
                Assert.That(Field<TMP_Text>("feedback").text,Does.Contain("귀환 공간"));
            }
            finally{typeof(ClientBootstrap).GetField("connection",Hidden).SetValue(bootstrap,null);}
        }

        private static NetworkEvent Event(ulong serial,SimEventKind kind,int detail)
        {
            var data=(SimEvent)Activator.CreateInstance(typeof(SimEvent),Hidden,null,new object[]{kind,1,0,0,WorldPoint.Zero,
                0f,detail,1UL,-1,WorldPoint.Zero,false,0,ObservedEntityKind.Unit,ObservedEntityKind.Unit,ObservedEntityKind.Unit},null);
            return (NetworkEvent)Activator.CreateInstance(typeof(NetworkEvent),Hidden,null,new object[]{serial,100L,data},null);
        }

        private T Field<T>(string name) => (T)typeof(CombatHud).GetField(name, Hidden).GetValue(hud);
        private void Set(string name, object value) => typeof(GameplayFrame).GetProperty(name).SetValue(frame, value);
    }
}
