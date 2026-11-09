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
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ElementalLegion.Client.Tests
{
    public sealed class DefenseHudTests
    {
        private GameObject root;
        private CombatHud hud;
        private GameDataDocument document;
        private GameRules rules;

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
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void RallyUsesExplicitFieldAndPositiveCountWhileOtherUnitsReinforce()
        {
            var target = Field<TMP_Dropdown>("rallyTarget");
            var count = Field<TMP_InputField>("rallyCount");
            var button = Field<Button>("rally");
            Assert.That(target.options.Count, Is.EqualTo(3));
            target.value = 2;
            count.text = "2";
            hud.ShowRally(7, 3, null, true);
            Assert.That(button.interactable, Is.True);
            Assert.That(hud.TryGetRally(out byte squad, out int amount), Is.True);
            Assert.That(squad, Is.EqualTo(3));
            Assert.That(amount, Is.EqualTo(2));
            Assert.That(Field<TMP_Text>("rallyStatus").text, Does.Contain("7").And.Contain("3"));
            foreach (string invalid in new[] { "0", "-1", "", "99999" })
            {
                count.text = invalid;
                Assert.That(hud.TryGetRally(out _, out _), Is.False, invalid);
                Assert.That(button.interactable, Is.False, invalid);
            }
            count.text = "1";
            Assert.That(button.interactable, Is.True);
            int requested = 0;
            hud.RallyRequested += () => { Assert.That(hud.TryGetRally(out _, out int sent), Is.True); requested = sent; };
            button.onClick.Invoke();
            Assert.That(requested, Is.EqualTo(1));
        }

        [Test]
        public void DefenseAlertSurvivesFeedbackThenExpiresAndResetClearsIt()
        {
            var controls = new ControlBindings();
            Assert.That(controls.TrySet(ControlBindings.BaseFocus, Key.F2, out _), Is.True);
            hud.ApplyPreferences(controls, 100);
            hud.ShowBaseUnderAttack(10);
            var alert = Field<TMP_Text>("defenseAlert");
            Assert.That(alert.text, Does.Contain("F2").And.Contain("본진"));
            Assert.That(alert.gameObject.activeSelf, Is.True);
            hud.TrainingCanceled();
            hud.TickDefenseAlert(10.1);
            Assert.That(alert.gameObject.activeSelf, Is.True);
            hud.TickDefenseAlert(1000);
            Assert.That(alert.gameObject.activeSelf, Is.False);
            hud.ShowBaseUnderAttack(1001);
            hud.ResetHud();
            Assert.That(alert.gameObject.activeSelf, Is.False);
            Assert.That(ControlBindings.Count, Is.EqualTo(18));
        }

        [Test]
        public void KeyboardAndButtonRallySendTheSameExplicitCommand()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var bootstrap = root.GetComponent<ClientBootstrap>();
            using var connection = new ClientConnection(rules, new NetworkSettings(document));
            try
            {
                typeof(ClientConnection).GetProperty(nameof(ClientConnection.State)).SetValue(connection, ConnectionState.Connected);
                typeof(ClientBootstrap).GetField("connection", flags).SetValue(bootstrap, connection);
                typeof(ClientBootstrap).GetField("waitingGarrisonCount", flags).SetValue(bootstrap, 3);
                Field<TMP_Dropdown>("rallyTarget").value = 1;
                Field<TMP_InputField>("rallyCount").text = "2";
                hud.ShowRally(7, 3, null, true);
                var rally = typeof(ClientBootstrap).GetMethod("Rally", flags);
                System.Action request = () => rally.Invoke(bootstrap, null);
                hud.RallyRequested += request;
                ulong expectedSequence = 0;
                foreach (bool button in new[] { false, true })
                {
                    typeof(ClientBootstrap).GetField("pendingRally", flags).SetValue(bootstrap, 0UL);
                    if (button) Field<Button>("rally").onClick.Invoke();
                    else rally.Invoke(bootstrap, null);
                    var writer = (NetDataWriter)typeof(ClientConnection).GetField("writer", flags).GetValue(connection);
                    Assert.That(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var command), Is.True);
                    Assert.That(command.Kind, Is.EqualTo(CommandKind.Rally));
                    Assert.That(command.Sequence, Is.EqualTo(++expectedSequence));
                    Assert.That(command.TargetSquad, Is.EqualTo(2));
                    Assert.That(command.Count, Is.EqualTo(2));
                }
                hud.RallyRequested -= request;
            }
            finally { typeof(ClientBootstrap).GetField("connection", flags).SetValue(bootstrap, null); }
        }

        [Test]
        public void DefenseSoundUsesAnExistingClipWithoutSpatialDirection()
        {
            var audio = root.GetComponent<CombatAudio>();
            audio.Initialize();
            audio.DefenseAlert();
            var sources = (AudioSource[])typeof(CombatAudio).GetField("sources",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(audio);
            var clips = (AudioClip[])typeof(CombatAudio).GetField("clips",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(audio);
            Assert.That(sources[0].clip, Is.SameAs(clips[1]));
            Assert.That(sources[0].spatialBlend, Is.Zero);
            Assert.That(sources[0].transform.position, Is.EqualTo(Vector3.zero));
            audio.Clear();
        }

        private T Field<T>(string name) => (T)typeof(CombatHud)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hud);
    }
}
