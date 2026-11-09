using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using NUnit.Framework;
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    public sealed class SquadPresentationTests
    {
        [Test]
        public void SelectionMaskIsIndependentOfPredictionAndClampedToFourSquads()
        {
            var go = new GameObject("selection test");
            try
            {
                var visuals = go.AddComponent<ElementalVisuals>();
                visuals.SetSelectedSquads(0xff);
                Assert.That(visuals.SelectedSquads, Is.EqualTo(15));
                visuals.SetSelectedSquads(4);
                Assert.That(visuals.SelectedSquads, Is.EqualTo(4));
                visuals.Clear();
                Assert.That(visuals.SelectedSquads, Is.EqualTo(4));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [TestCase(10, 20, 0, 21, 50, true)]
        [TestCase(10, 20, 0, 19, 50, false)]
        [TestCase(10, 20, 0, 21, 0, false)]
        [TestCase(21, 20, 0, 10, 50, false)]
        [TestCase(10, 20, 1, 21, 50, false)]
        public void RecallSuccessRequiresObservedCastCompletionAndNewCooldown(long previousTick, long endTick, byte squad, long tick, long readyTick, bool expected)
        {
            Assert.That(ElementalVisuals.IsRecallSuccess(previousTick, 1, endTick, 0, tick, squad, readyTick), Is.EqualTo(expected));
        }

        [TestCase(128, 32, true)]
        [TestCase(32, 32, false)]
        [TestCase(0, 32, false)]
        public void DefenseAlertOnlyReplacesLowerPriorityVoice(int current, int alert, bool expected)
        {
            Assert.That(CombatAudio.CanReplaceForAlert(current, alert), Is.EqualTo(expected));
        }

        [Test]
        public void DefenseAlertClampsPriorityAboveCombatAndCoalescesWithinItsExistingPool()
        {
            var go = new GameObject("defense pool test");
            AudioClip clip = AudioClip.Create("test defense", 44100 * 5, 1, 44100, false);
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var audio = go.AddComponent<CombatAudio>();
                typeof(CombatAudio).GetField("clips", hidden).SetValue(audio, new[] { clip, clip, clip, clip, clip, clip, clip });
                typeof(CombatAudio).GetField("maxVoices", hidden).SetValue(audio, 1);
                typeof(CombatAudio).GetField("defensePriority", hidden).SetValue(audio, 256);
                typeof(CombatAudio).GetMethod("OnValidate", hidden).Invoke(audio, null);
                audio.Initialize();
                audio.Volumes(.5f, .4f, .6f, false, false, false);
                audio.Play(ElementalLegion.Shared.Sim.SimEventKind.Attack, Vector3.one);
                var sources = go.GetComponentsInChildren<AudioSource>();
                Assert.That(sources.Length, Is.EqualTo(1));
                Assert.That(sources[0].isPlaying, Is.True, "가득 찬 실제 음성 풀을 검사합니다.");
                audio.DefenseAlert();
                Assert.That(audio.DroppedVoices, Is.Zero);
                Assert.That(sources[0].priority, Is.LessThan(128));
                Assert.That(sources[0].spatialBlend, Is.Zero);
                Assert.That(sources[0].transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(sources[0].volume, Is.EqualTo(.3f).Within(.0001f));
                var deadline = typeof(CombatAudio).GetField("nextDefenseAlert", hidden);
                double firstDeadline = (double)deadline.GetValue(audio);
                audio.DefenseAlert();
                Assert.That((double)deadline.GetValue(audio), Is.EqualTo(firstDeadline));
                Assert.That(audio.DroppedVoices, Is.Zero);
                Assert.That(go.GetComponentsInChildren<AudioSource>().Length, Is.EqualTo(1));
                audio.Volumes(.5f, .4f, .6f, false, false, true);
                Assert.That(sources[0].volume, Is.Zero);
                audio.Clear();
                audio.DefenseAlert();
                Assert.That((double)deadline.GetValue(audio), Is.Zero);
                audio.Volumes(.5f, .4f, .6f, false, true, false);
                audio.DefenseAlert();
                Assert.That((double)deadline.GetValue(audio), Is.GreaterThan(0));
                Assert.That(sources[0].volume, Is.EqualTo(.3f).Within(.0001f));
                audio.Clear();
                audio.Volumes(.5f, .4f, .6f, true, false, false);
                audio.DefenseAlert();
                Assert.That((double)deadline.GetValue(audio), Is.Zero);
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(clip); }
        }

        [Test]
        public void MutableFrameSuccessAndRollbackRespectObservationLifetime()
        {
            var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
            var rules = new GameRules(new GameDataDocument(data.text));
            var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { rules }, null);
            var go = new GameObject("recall observation test");
            try
            {
                var visuals = go.AddComponent<ElementalVisuals>();
                SetFrame(frame, 10, 1, 20, 0);
                visuals.Observe(frame);
                SetFrame(frame, 21, 0, 0, 50);
                visuals.Observe(frame);
                var until = typeof(ElementalVisuals).GetField("arrivalUntil", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That((float)until.GetValue(visuals), Is.GreaterThan(0));
                SetFrame(frame, 5, 0, 0, 50);
                visuals.Observe(frame);
                Assert.That((float)until.GetValue(visuals), Is.Zero);
                visuals.Clear();
                SetFrame(frame, 21, 0, 0, 50);
                visuals.Observe(frame);
                Assert.That((float)until.GetValue(visuals), Is.Zero);
            }
            finally { Object.DestroyImmediate(go); }
        }

        private static void SetFrame(GameplayFrame frame, long tick, byte squad, long end, long ready)
        {
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId)).SetValue(frame, 1);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.Tick)).SetValue(frame, tick);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.RecallSquad)).SetValue(frame, squad);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.RecallEndTick)).SetValue(frame, end);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.RecallReadyTick)).SetValue(frame, ready);
        }
    }
}
