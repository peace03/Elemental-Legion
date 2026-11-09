using System;
using System.Collections.Generic;
using System.Reflection;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    /// <summary>후보의 현재 관측과 자기 진행만 표시하고 동일 ID의 권위 복귀를 보존하는지 확인한다.</summary>
    public sealed class RecoveryPresentationTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void CandidateTimersShowOnlyOwnProgressAndReuseThePoolAfterVisibilityLoss()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                    "Packages/com.elementallegion.shared/Data/gamedata.json").text);
                var rules = new GameRules(document);
                var visuals = root.GetComponent<ElementalVisuals>();
                var hud = root.GetComponentInChildren<CombatHud>(true);
                hud.Configure(document, rules);
                hud.BuildObservatory(new ObservatoryUi(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                    "Assets/_Project/Settings/UI/LegionUiSDF.asset")), rules, visuals);
                var camera = visuals.TargetCamera;
                camera.transform.SetPositionAndRotation(new Vector3(100, 30, 100), Quaternion.Euler(90, 0, 0));
                var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), Hidden, null, new object[] { rules }, null);
                typeof(GameplayFrame).GetProperty("Tick").SetValue(frame, 100L);
                typeof(GameplayFrame).GetProperty("SpatialReady").SetValue(frame, true);
                var buffer = (ConversionCandidateView[])typeof(GameplayFrame).GetField("CandidateBuffer", Hidden).GetValue(frame);
                typeof(GameplayFrame).GetField("CandidateCount", Hidden).SetValue(frame, 1);
                var tick = typeof(CombatHud).GetMethod("LateUpdate", Hidden);
                foreach (RecoveryRole role in new[] { RecoveryRole.Secure, RecoveryRole.Rescue, RecoveryRole.Observer })
                {
                    buffer[0] = new ConversionCandidateView(7, 0, new Point(100, 100), 300, role,
                        role == RecoveryRole.Observer ? 0 : 30);
                    hud.Apply(frame);
                    tick.Invoke(hud, null);
                    var timers = (List<TMP_Text>)typeof(CombatHud).GetField("buildingTimers", Hidden).GetValue(hud);
                    Assert.That(timers.Count, Is.EqualTo(1));
                    Assert.That(timers[0].gameObject.activeSelf, Is.True);
                    Assert.That(timers[0].text, Does.Contain("10.0"));
                    if (role == RecoveryRole.Observer)
                        Assert.That(timers[0].text, Does.Contain("후보").And.Not.Contain("확보").And.Not.Contain("구출").And.Not.Contain("/"));
                    else
                        Assert.That(timers[0].text, Does.Contain(role == RecoveryRole.Secure ? "확보" : "구출").And.Contain("1.5").And.Contain("3.0"));
                }
                typeof(GameplayFrame).GetField("CandidateCount", Hidden).SetValue(frame, 0);
                tick.Invoke(hud, null);
                var pool = (List<TMP_Text>)typeof(CombatHud).GetField("buildingTimers", Hidden).GetValue(hud);
                Assert.That(pool[0].gameObject.activeSelf, Is.False);
                typeof(GameplayFrame).GetField("CandidateCount", Hidden).SetValue(frame, 1);
                tick.Invoke(hud, null);
                Assert.That(pool.Count, Is.EqualTo(1));
                Assert.That(pool[0].gameObject.activeSelf, Is.True);
                var fog = new FogStateReplica(rules);
                typeof(FogStateReplica).GetProperty("Revision").SetValue(fog, 1U);
                byte[] current = (byte[])typeof(FogStateReplica).GetField("current", Hidden).GetValue(fog);
                for (int index = 0; index < current.Length; index++) current[index] = 255;
                hud.Map.ObserveFog(fog);
                hud.Map.ObserveGameplay(frame);
                int withCandidate = MapVertices(hud.Map);
                typeof(GameplayFrame).GetField("CandidateCount", Hidden).SetValue(frame, 0);
                Assert.That(MapVertices(hud.Map), Is.EqualTo(withCandidate - 5), "현재 목록에서 빠진 후보 표식은 남지 않습니다.");
                typeof(GameplayFrame).GetField("CandidateCount", Hidden).SetValue(frame, 1);
                Array.Clear(current, 0, current.Length);
                hud.Map.ObserveFog(fog);
                Assert.That(MapVertices(hud.Map), Is.EqualTo(withCandidate - 5), "현재 시야 밖에 후보 표식을 그리지 않습니다.");
                typeof(GameplayFrame).GetProperty("Tick").SetValue(frame, 300L);
                tick.Invoke(hud, null);
                Assert.That(pool[0].gameObject.activeSelf, Is.False, "만료 틱에는 후보를 표시하지 않습니다.");
                typeof(GameplayFrame).GetProperty("Tick").SetValue(frame, 100L);
                tick.Invoke(hud, null);
                hud.ResetHud();
                Assert.That(pool[0].gameObject.activeSelf, Is.False);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OnlyANewerAuthoritativeUnitRevivesTheSameIdWithoutChangingConversionHistory(bool converted)
        {
            var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text));
            var snapshot = new UnitSnapshotAssembler(rules);
            var timeline = new SnapshotTimeline(4);
            typeof(UnitSnapshotAssembler).GetProperty("Count").SetValue(snapshot, 1);
            snapshot.Units[0] = new NetworkUnit(7, 1, 0, new Point(100, 100), false, 10, 0, converted, 0);
            typeof(UnitSnapshotAssembler).GetProperty("Tick").SetValue(snapshot, 9L);
            timeline.Push(snapshot);
            timeline.ResolveDeath(7, 10);
            typeof(UnitSnapshotAssembler).GetProperty("Tick").SetValue(snapshot, 10L);
            timeline.Push(snapshot);
            Assert.That(timeline.TryGetVisibleLatest(0, out _), Is.False);
            typeof(UnitSnapshotAssembler).GetProperty("Tick").SetValue(snapshot, 11L);
            timeline.Push(snapshot);
            Assert.That(timeline.TryGetVisibleLatest(0, out NetworkUnit returned), Is.True);
            Assert.That(returned.Converted, Is.EqualTo(converted));
            Assert.That(returned.Health, Is.EqualTo(10));
            timeline.ResolveDeath(7, 10);
            Assert.That(timeline.TryGetVisibleLatest(0, out _), Is.True, "늦게 도착한 과거 사망은 복귀 관측을 숨기지 않습니다.");
            var delayed = new SnapshotTimeline(4);
            delayed.Push(snapshot);
            delayed.ResolveDeath(7, 10);
            Assert.That(delayed.TryGetVisibleLatest(0, out _), Is.True, "사망 사건의 첫 도착이 늦어도 최신 복귀 관측을 보존합니다.");
        }

        [Test]
        public void NewerSnapshotsReleaseDeathRecordsWhileSameTickStillHidesTheUnit()
        {
            var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text));
            var snapshot = new UnitSnapshotAssembler(rules);
            var timeline = new SnapshotTimeline(4);
            var deaths = (Dictionary<int, long>)typeof(SnapshotTimeline).GetField("dead", Hidden).GetValue(timeline);
            typeof(UnitSnapshotAssembler).GetProperty("Count").SetValue(snapshot, 1);
            snapshot.Units[0] = new NetworkUnit(7, 1, 0, new Point(100), false, 10, 0, false, 0);
            timeline.ResolveDeath(7, 10);
            typeof(UnitSnapshotAssembler).GetProperty("Tick").SetValue(snapshot, 9L);
            timeline.Push(snapshot);
            Assert.That(timeline.TryGetVisibleLatest(0, out _), Is.False);
            typeof(UnitSnapshotAssembler).GetProperty("Tick").SetValue(snapshot, 10L);
            timeline.Push(snapshot);
            Assert.That(timeline.TryGetVisibleLatest(0, out _), Is.False);
            Assert.That(deaths.Count, Is.EqualTo(1));
            typeof(UnitSnapshotAssembler).GetProperty("Count").SetValue(snapshot, 0);
            typeof(UnitSnapshotAssembler).GetProperty("Tick").SetValue(snapshot, 11L);
            timeline.Push(snapshot);
            Assert.That(deaths, Is.Empty, "최신 관측에 유닛이 없어도 지난 사망 기록을 반환합니다.");
            timeline.ResolveDeath(7, 10);
            Assert.That(deaths, Is.Empty, "늦은 과거 사건으로 사망 기록을 다시 쌓지 않습니다.");
            typeof(UnitSnapshotAssembler).GetProperty("Count").SetValue(snapshot, 1);
            typeof(UnitSnapshotAssembler).GetProperty("Tick").SetValue(snapshot, 12L);
            timeline.Push(snapshot);
            Assert.That(timeline.TryGetVisibleLatest(0, out NetworkUnit returned), Is.True);
            Assert.That(returned.Converted, Is.False);
        }

        [Test]
        public void CandidateBatchAndKindSpecificForgetKeepTheRescueEffect()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                    "Packages/com.elementallegion.shared/Data/gamedata.json").text));
                var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), Hidden, null, new object[] { rules }, null);
                typeof(GameplayFrame).GetProperty("Tick").SetValue(frame, 100L);
                typeof(GameplayFrame).GetProperty("SpatialReady").SetValue(frame, true);
                var buffer = (ConversionCandidateView[])typeof(GameplayFrame).GetField("CandidateBuffer", Hidden).GetValue(frame);
                buffer[0] = new ConversionCandidateView(7, 0, new Point(100), 300, RecoveryRole.Observer, 0);
                typeof(GameplayFrame).GetField("CandidateCount", Hidden).SetValue(frame, 1);
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                visuals.Observe(frame);
                var timeline = new SnapshotTimeline(4);
                visuals.Render(timeline, 1, Point.Zero, 100);
                int group = (int)typeof(ElementalVisuals).GetField("Candidates", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
                var counts = (int[])typeof(ElementalVisuals).GetField("counts", Hidden).GetValue(visuals);
                Assert.That(counts[group], Is.EqualTo(1));
                var data = (SimEvent)Activator.CreateInstance(typeof(SimEvent), Hidden, null,
                    new object[] { SimEventKind.Rescued, 1, 0, 7, new Point(100), 10f, 0, 0UL, 0, Point.Zero,
                        false, 7, ObservedEntityKind.Unit, ObservedEntityKind.Unit, ObservedEntityKind.Unit }, null);
                var entry = (NetworkEvent)Activator.CreateInstance(typeof(NetworkEvent), Hidden, null, new object[] { 1UL, 101L, data }, null);
                visuals.Play(entry);
                Assert.That(visuals.ActiveParticles, Is.GreaterThan(0));
                typeof(GameplayFrame).GetField("CandidateCount", Hidden).SetValue(frame, 0);
                visuals.Forget(new ObservedEntityId(ObservedEntityKind.Candidate, 7));
                visuals.Render(timeline, 1, Point.Zero, 101);
                Assert.That(counts[group], Is.Zero);
                Assert.That(visuals.ActiveParticles, Is.GreaterThan(0), "같은 숫자의 후보 Forget은 복귀 유닛 효과를 제거하지 않습니다.");
                visuals.Forget(new ObservedEntityId(ObservedEntityKind.Unit, 7));
                foreach (ParticleSystem system in visuals.GetComponentsInChildren<ParticleSystem>(true))
                    Assert.That(system.particleCount, Is.Zero);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static int MapVertices(ObservatoryMap map)
        {
            using var mesh = new VertexHelper();
            typeof(ObservatoryMap).GetMethod("OnPopulateMesh", Hidden, null, new[] { typeof(VertexHelper) }, null)
                .Invoke(map, new object[] { mesh });
            return mesh.currentVertCount;
        }
    }
}
