using System;
using System.Collections.Generic;
using System.Reflection;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class BuildingMemoryTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameRules rules;

        [OneTimeSetUp]
        public void LoadRules()
        {
            var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
            rules = new GameRules(new GameDataDocument(data.text));
        }

        [Test]
        public void HiddenBuildingKeepsTheLastCopiedStateUntilTheServerConfirmsAnEmptySite()
        {
            var memory = new BuildingMemory();
            var frame = Frame(4, 1, Enemy(8, 13, 21), new BuildingView(9, 1, BuildingKind.Base, Point.Zero, 4));
            Assert.That(memory.Observe(frame), Is.True);
            Assert.That(memory.IsVisible(8), Is.True);
            Assert.That(memory.TryGet(9, out _), Is.False);

            Assert.That(memory.Forget(8, 5), Is.True);
            Assert.That(memory.ConfirmAbsent(ReadOnlySpan<int>.Empty, 5, 2), Is.False);
            Assert.That(memory.Observe(Frame(5, 2)), Is.True);
            // 수신기는 같은 프레임 버퍼를 다음 관측에 재사용한다.
            Buildings(frame)[0] = Enemy(8, 99, 0);
            Assert.That(memory.IsVisible(8), Is.False);
            Assert.That(memory.TryGet(8, out var lastSeen), Is.True);
            Assert.That(lastSeen.Building.Health, Is.EqualTo(13));
            Assert.That(lastSeen.Building.IsUnderConstruction, Is.True);
            Assert.That(lastSeen.LastSeenTick, Is.EqualTo(4));
            Assert.That(lastSeen.Color, Is.EqualTo((Color32)PlayerColors.Get(3)));

            Assert.That(memory.ConfirmAbsent(new[] { 8 }, 6, 3), Is.True);
            Assert.That(memory.TryGet(8, out _), Is.False);
        }

        [Test]
        public void DelayedVisibilityCannotReplaceOrClearNewerObservations()
        {
            var memory = new BuildingMemory();
            Assert.That(memory.Observe(Frame(4, 1, Enemy(8, 13, 21))), Is.True);
            memory.Forget(8, 5);
            memory.ConfirmAbsent(new[] { 8 }, 6, 3);
            Assert.That(memory.Observe(Frame(5, 2, Enemy(8, 99, 0))), Is.False);
            Assert.That(memory.TryGet(8, out _), Is.False);

            Assert.That(memory.Observe(Frame(8, 4, Enemy(8, 57, 0))), Is.True);
            Assert.That(memory.Forget(8, 7), Is.False);
            Assert.That(memory.ConfirmAbsent(new[] { 8 }, 6, 3), Is.False);
            GameplayFrame unpaired = Frame(9, 5, Enemy(8, 99, 0));
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.SpatialReady)).SetValue(unpaired, false);
            Assert.That(memory.Observe(unpaired), Is.False);
            Assert.That(memory.IsVisible(8), Is.True);
            Assert.That(memory.TryGet(8, out var record), Is.True);
            Assert.That(record.Building.Health, Is.EqualTo(57));
            Assert.That(record.Building.IsUnderConstruction, Is.False);
        }

        [Test]
        public void CapacityDropsTheOldestHiddenIdAndResetAllowsAnOlderWorldBaseline()
        {
            var memory = new BuildingMemory();
            int limit = FogProtocol.MaxRememberedBuildings;
            var full = new BuildingView[limit];
            for (int index = 0; index < full.Length; index++) full[index] = Enemy(index + 1, 40, 0);
            memory.Observe(Frame(18, 7, full));
            int next = limit + 1;
            memory.Observe(Frame(19, 8, Enemy(next, 40, 0), Enemy(1, 40, 0)));
            Assert.That(memory.Records.Count, Is.EqualTo(limit));
            Assert.That(memory.TryGet(2, out _), Is.False);
            Assert.That(memory.IsVisible(1), Is.True);
            Assert.That(memory.IsVisible(next), Is.True);

            memory.Clear();
            Assert.That(memory.Records, Is.Empty);
            Assert.That(memory.Observe(Frame(3, 1, Enemy(2, 40, 0))), Is.True);
            Assert.That(memory.IsVisible(2), Is.True);
        }

        private GameplayFrame Frame(long tick, uint revision, params BuildingView[] buildings)
        {
            var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), Hidden, null, new object[] { rules }, null);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId)).SetValue(frame, 1);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.Tick)).SetValue(frame, tick);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.Revision)).SetValue(frame, revision);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.SpatialReady)).SetValue(frame, true);
            typeof(GameplayFrame).GetField("BuildingCount", Hidden).SetValue(frame, buildings.Length);
            buildings.CopyTo(Buildings(frame), 0);
            var colors = (List<OwnerColorView>)typeof(GameplayFrame).GetField("OwnerColorBuffer", Hidden).GetValue(frame);
            colors.Add(new OwnerColorView(2, 3));
            return frame;
        }

        private static BuildingView[] Buildings(GameplayFrame frame) =>
            (BuildingView[])typeof(GameplayFrame).GetField("BuildingBuffer", Hidden).GetValue(frame);

        private static BuildingView Enemy(int id, float health, long constructionEndTick) =>
            new BuildingView(id, 2, BuildingKind.Collector, new Point(32, 48), 2, health, 100, -1, 0, constructionEndTick);
    }
}
