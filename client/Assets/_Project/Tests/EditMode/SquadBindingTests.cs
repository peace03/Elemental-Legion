using System;
using System.Linq;
using System.Reflection;
using ElementalLegion.Client.Input;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>분대 단축키의 저장 호환성과 설정 화면의 입력 영역을 확인한다.</summary>
    public sealed class SquadBindingTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void OrderedGuardToFirstChordIsRecognizedWithinOneInputUpdate(bool releaseBeforeReading)
        {
            var root = new GameObject("Guard chord input");
            var fixture = new InputTestFixture();
            fixture.Setup();
            try
            {
                var keyboard = InputSystem.AddDevice<Keyboard>();
                var input = root.AddComponent<LegionInput>();
                input.Apply(new ControlBindings());
                input.ReadSquadShortcut(true);

                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit4));
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit4, Key.Digit1));
                if (releaseBeforeReading) InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.Update();

                var result = input.ReadSquadShortcut(true);
                Assert.That(result.FromSquad, Is.EqualTo(4));
                Assert.That(result.TargetSquad, Is.EqualTo(1));
                Assert.That(result.SelectionMask, Is.Zero);
                Assert.That(input.ReadSquadShortcut(true).TargetSquad, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(root);
                fixture.TearDown();
            }
        }

        [Test]
        public void SeparateTapsWithinOneInputUpdateOnlySelectEachSquad()
        {
            var root = new GameObject("Squad taps input");
            var fixture = new InputTestFixture();
            fixture.Setup();
            try
            {
                var keyboard = InputSystem.AddDevice<Keyboard>();
                var input = root.AddComponent<LegionInput>();
                input.Apply(new ControlBindings());
                input.ReadSquadShortcut(true);

                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit4));
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit1));
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.Update();

                var guard = input.ReadSquadShortcut(true);
                var first = input.ReadSquadShortcut(true);
                Assert.That(guard.SelectionMask, Is.EqualTo(8));
                Assert.That(guard.TargetSquad, Is.Zero);
                Assert.That(first.SelectionMask, Is.EqualTo(1));
                Assert.That(first.FromSquad, Is.Zero);
                Assert.That(first.TargetSquad, Is.Zero);
                Assert.That(input.ReadSquadShortcut(true).TargetSquad, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(root);
                fixture.TearDown();
            }
        }

        [Test]
        public void RemappedNumpadChordUsesSquadSlots()
        {
            var root = new GameObject("Squad binding input");
            var fixture = new InputTestFixture();
            fixture.Setup();
            try
            {
                var keyboard = InputSystem.AddDevice<Keyboard>();
                keyboard.MakeCurrent();
                var input = root.AddComponent<LegionInput>();
                var controls = new ControlBindings();
                Assert.That(controls.TrySet(ControlBindings.Squad4, Key.Numpad4, out _), Is.True);
                Assert.That(controls.TrySet(ControlBindings.Squad2, Key.Numpad2, out _), Is.True);
                input.Apply(controls);
                input.ReadSquadShortcut(true);
                fixture.Press(keyboard.digit4Key);
                Assert.That(input.ReadSquadShortcut(true).FromSquad, Is.Zero);
                Assert.That(input.PendingFromSquad, Is.Zero);
                fixture.Release(keyboard.digit4Key);
                fixture.Press(keyboard.numpad4Key);
                input.ReadSquadShortcut(true);
                Assert.That(input.PendingFromSquad, Is.EqualTo(4));
                fixture.Press(keyboard.numpad2Key);
                var chord = input.ReadSquadShortcut(true);
                Assert.That(chord.FromSquad, Is.EqualTo(4));
                Assert.That(chord.TargetSquad, Is.EqualTo(2));
                fixture.Release(keyboard.numpad4Key);
                fixture.Release(keyboard.numpad2Key);
                Assert.That(input.ReadSquadShortcut(true).SelectionMask, Is.Zero);
            }
            finally
            {
                fixture.TearDown();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void NewCommandsAppendAfterLegacySlotsWithDistinctDefaults()
        {
            Assert.That(new[] { ControlBindings.Rally, ControlBindings.Menu, ControlBindings.Dashboard,
                ControlBindings.Squad1, ControlBindings.Squad2, ControlBindings.Squad3, ControlBindings.Squad4,
                ControlBindings.AllField, ControlBindings.BaseFocus, ControlBindings.Recall, ControlBindings.Count },
                Is.EqualTo(new[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 }));
            var controls = new ControlBindings();
            Assert.That(Enumerable.Range(0, ControlBindings.Count).Select(slot => controls[slot]), Is.EqualTo(new[]
            {
                Key.W, Key.S, Key.A, Key.D, Key.UpArrow, Key.DownArrow, Key.LeftArrow, Key.RightArrow,
                Key.G, Key.Escape, Key.Tab, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.B, Key.R
            }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyCustomKeysSurviveWhenNewDefaultsConflict(bool hasDashboard)
        {
            string saved = "Digit1,Digit2,Digit3,Digit4,Digit5,B,R,F1,Tab,F2" + (hasDashboard ? ",H" : "");
            Key[] oldKeys = saved.Split(',').Select(value => (Key)Enum.Parse(typeof(Key), value)).ToArray();
            var controls = new ControlBindings();
            Assert.That(controls.Restore(saved), Is.True);
            Assert.That(Enumerable.Range(0, oldKeys.Length).Select(slot => controls[slot]), Is.EqualTo(oldKeys));
            Assert.That(controls[ControlBindings.Dashboard], Is.EqualTo(hasDashboard ? Key.H : Key.F3));
            Key firstFree = hasDashboard ? Key.F3 : Key.F4;
            for (int slot = ControlBindings.Squad1; slot < ControlBindings.Count; slot++)
                Assert.That(controls[slot], Is.EqualTo(firstFree + slot - ControlBindings.Squad1));
            Assert.That(Enumerable.Range(0, ControlBindings.Count).Select(slot => controls[slot]).Distinct().Count(),
                Is.EqualTo(ControlBindings.Count));
        }

        [Test]
        public void FullBindingSaveRoundTripsWithoutChangingPlayerPreferences()
        {
            const string preference = "ElementalLegion.Controls";
            bool existed = PlayerPrefs.HasKey(preference);
            string previous = PlayerPrefs.GetString(preference);
            const string saved = "I,K,J,L,UpArrow,DownArrow,LeftArrow,RightArrow,G,Escape,H," +
                "Numpad1,Numpad2,Numpad3,Numpad4,Numpad5,F8,F9";
            try
            {
                var controls = new ControlBindings();
                Assert.That(controls.Restore(saved), Is.True);
                Assert.That(controls.TrySet(ControlBindings.Recall, Key.F10, out _), Is.True);
                controls.Save();
                var loaded = ControlBindings.Load();
                Assert.That(Enumerable.Range(0, ControlBindings.Count).Select(slot => loaded[slot]),
                    Is.EqualTo(Enumerable.Range(0, ControlBindings.Count).Select(slot => controls[slot])));
                Assert.That(loaded.TrySet(ControlBindings.BaseFocus, Key.F10, out int conflict), Is.False);
                Assert.That(conflict, Is.EqualTo(ControlBindings.Recall));
            }
            finally
            {
                if (existed) PlayerPrefs.SetString(preference, previous);
                else PlayerPrefs.DeleteKey(preference);
                PlayerPrefs.Save();
            }
        }

        [TestCase("B")]
        [TestCase("Enter")]
        [TestCase("None")]
        [TestCase("999")]
        public void InvalidFullSaveIsRejectedWithoutPartialChanges(string lastKey)
        {
            var controls = new ControlBindings();
            var saved = Enumerable.Range(0, ControlBindings.Count).Select(slot => controls[slot].ToString()).ToArray();
            saved[ControlBindings.Recall] = lastKey;
            Assert.That(controls.Restore(string.Join(",", saved)), Is.False);
            Assert.That(controls[ControlBindings.Recall], Is.EqualTo(Key.R));
            Assert.That(controls[ControlBindings.BaseFocus], Is.EqualTo(Key.B));
            Assert.That(controls.Restore(string.Join(",", saved.Take(17))), Is.False);
        }

        [Test]
        public void SettingsExposeAllBindingsWithoutOverlappingStatusOrButtons()
        {
            Assembly assembly = typeof(ControlBindings).Assembly;
            Type skinType = assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            Type settingsType = assembly.GetType("ElementalLegion.Client.UI.ClientSettingsScreen", true);
            Type preferencesType = assembly.GetType("ElementalLegion.Client.UI.ClientPreferences", true);
            object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset") }, null);
            object preferences = Activator.CreateInstance(preferencesType, true);
            var root = new GameObject("Squad binding settings test");
            root.SetActive(false);
            try
            {
                Component screen = root.AddComponent(settingsType);
                settingsType.GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(screen,
                    new[] { skin, preferences, new ControlBindings(), (Action)(() => { }), (Action)(() => { }), (Action<int>)(_ => { }) });
                Transform panel = root.GetComponentsInChildren<RectTransform>(true).Single(rect => rect.name == "Keyboard bindings");
                var buttons = panel.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                Assert.That(buttons.Length, Is.EqualTo(ControlBindings.Count + 2));
                var status = panel.GetComponentsInChildren<TMP_Text>(true).Single(text => text.name == "Keyboard status").rectTransform;
                var keyButtons = buttons.Take(ControlBindings.Count).Select(button => (RectTransform)button.transform).ToArray();
                foreach (RectTransform button in keyButtons)
                {
                    Assert.That(button.sizeDelta.x, Is.GreaterThan(0));
                    Assert.That(button.sizeDelta.y, Is.GreaterThan(0));
                    Assert.That(-button.anchoredPosition.y + button.sizeDelta.y, Is.LessThan(-status.anchoredPosition.y));
                    Assert.That(button.anchoredPosition.x + button.sizeDelta.x, Is.LessThanOrEqualTo(((RectTransform)panel).sizeDelta.x));
                }
                for (int first = 0; first < keyButtons.Length; first++)
                    for (int second = first + 1; second < keyButtons.Length; second++)
                        Assert.That(new Rect(keyButtons[first].anchoredPosition, keyButtons[first].sizeDelta)
                            .Overlaps(new Rect(keyButtons[second].anchoredPosition, keyButtons[second].sizeDelta)), Is.False);
                Assert.That(-status.anchoredPosition.y + status.sizeDelta.y,
                    Is.LessThan(-((RectTransform)buttons[ControlBindings.Count].transform).anchoredPosition.y));
                Assert.That(panel.GetComponentsInChildren<TMP_Text>(true).Select(label => label.text), Does.Contain("야전 병력 전체 선택"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
