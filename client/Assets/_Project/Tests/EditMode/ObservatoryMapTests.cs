using System;
using System.Collections.Generic;
using System.Reflection;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class ObservatoryMapTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private ObservatoryMap map;
        private GameRules rules;

        [SetUp]
        public void CreateMap()
        {
            var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
            rules = new GameRules(new GameDataDocument(data.text));
            var root = new GameObject("Test map", typeof(RectTransform), typeof(ObservatoryMap));
            map = root.GetComponent<ObservatoryMap>();
            map.rectTransform.sizeDelta = new Vector2(200, 200);
            var backdrop = new GameObject("Terrain", typeof(RectTransform), typeof(RawImage));
            backdrop.transform.SetParent(root.transform, false);
            map.Initialize(backdrop.GetComponent<RawImage>(), rules);
        }

        [TearDown]
        public void DestroyMap() => Object.DestroyImmediate(map.gameObject);

        [Test]
        public void OverlappingSquadsStayReadableWithoutConsumingTheMapClick()
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
            map.ConfigureMarkers(new ObservatoryUi(font), 13, .2f, 2.4f);
            var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), InstanceFields, null, new object[] { rules }, null);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId)).SetValue(frame, 1);
            var squads = (SquadView[])typeof(GameplayFrame).GetField("SquadBuffer", InstanceFields).GetValue(frame);
            var center = new Point(rules.MapSize * .5f);
            squads[0] = Squad(1, center);
            squads[1] = Squad(2, center);
            squads[2] = Squad(3, center);
            squads[3] = Squad(4, center);
            map.ObserveGameplay(frame);

            var digits = map.GetComponentsInChildren<TMP_Text>();
            Assert.That(digits.Length, Is.EqualTo(4));
            for (int index = 0; index < digits.Length; index++)
            {
                Assert.That(digits[index].text, Is.EqualTo((index + 1).ToString()));
                Assert.That(digits[index].raycastTarget, Is.False);
                Assert.That(digits[index].GetComponentInParent<Image>().raycastTarget, Is.False);
                if (index > 0) Assert.That(digits[index].transform.position.x, Is.GreaterThan(digits[index - 1].transform.position.x));
            }

            map.ResetMap();
            Assert.That(map.GetComponentsInChildren<TMP_Text>(), Is.Empty);
            map.ObserveGameplay(frame);
            Assert.That(map.GetComponentsInChildren<TMP_Text>()[0], Is.SameAs(digits[0]));
        }

        [Test]
        public void OnlyConsecutiveVisiblePositionsWithTheSameRevisionShowMovement()
        {
            var timeline = new SnapshotTimeline(4);
            var snapshot = new UnitSnapshotAssembler(rules);
            typeof(UnitSnapshotAssembler).GetProperty(nameof(UnitSnapshotAssembler.Count)).SetValue(snapshot, 1);

            Publish(timeline, snapshot, 1, 100, 0);
            int stationaryVertices = VertexCount();
            Assert.That(stationaryVertices, Is.GreaterThan(0), "첫 관측도 일반 표식으로 표시합니다.");
            Publish(timeline, snapshot, 2, 101, 0);
            int movingVertices = VertexCount();
            Assert.That(movingVertices, Is.Not.EqualTo(stationaryVertices), "색 변화 없이도 이동 표식 형태를 구분할 수 있습니다.");
            map.ObserveUnits(timeline, 1);
            Assert.That(VertexCount(), Is.EqualTo(movingVertices), "같은 관측을 다시 처리해도 정지로 오인하지 않습니다.");
            Publish(timeline, snapshot, 3, 101, 0);
            Assert.That(VertexCount(), Is.EqualTo(stationaryVertices));
            Publish(timeline, snapshot, 4, 150, 1);
            Assert.That(VertexCount(), Is.EqualTo(stationaryVertices), "양자 이동 이후 첫 관측은 이동 점을 만들지 않습니다.");

            timeline.Forget(7, 4);
            map.ObserveUnits(timeline, 1);
            Assert.That(VertexCount(), Is.Zero);
            Publish(timeline, snapshot, 5, 200, 1);
            Assert.That(VertexCount(), Is.EqualTo(stationaryVertices), "같은 ID의 재관측에 이전 위치를 사용하지 않습니다.");
        }

        [Test]
        public void RevealingAFogCellAgainDoesNotBringBackTheOldEnemyPosition()
        {
            var timeline = new SnapshotTimeline(4);
            var snapshot = new UnitSnapshotAssembler(rules);
            typeof(UnitSnapshotAssembler).GetProperty(nameof(UnitSnapshotAssembler.Count)).SetValue(snapshot, 1);
            var fog = new FogStateReplica(rules);
            typeof(FogStateReplica).GetProperty(nameof(FogStateReplica.Revision)).SetValue(fog, 1u);
            var fogBits = (byte[])typeof(FogStateReplica).GetField("current", InstanceFields).GetValue(fog);
            int coordinate = (int)(100 / rules.CellSize);
            int cell = coordinate + coordinate * fog.Width;
            byte mask = (byte)(1 << (cell & 7));

            fogBits[cell >> 3] = mask;
            map.ObserveFog(fog);
            Publish(timeline, snapshot, 1, 100, 0, owner: 2);
            Assert.That(VertexCount(), Is.GreaterThan(0));
            fogBits[cell >> 3] = 0;
            map.ObserveFog(fog);
            Assert.That(VertexCount(), Is.Zero);

            fogBits[cell >> 3] = mask;
            map.ObserveFog(fog);
            Assert.That(VertexCount(), Is.Zero);
            Publish(timeline, snapshot, 2, 100, 0, owner: 2);
            Assert.That(VertexCount(), Is.GreaterThan(0));
        }

        [Test]
        public void EnemyBuildingChangesFromFilledToOutlineAndDisappearsAfterConfirmation()
        {
            var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), InstanceFields, null, new object[] { rules }, null);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId)).SetValue(frame, 1);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.Tick)).SetValue(frame, 10L);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.Revision)).SetValue(frame, 1u);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.SpatialReady)).SetValue(frame, true);
            var buildings = (BuildingView[])typeof(GameplayFrame).GetField("BuildingBuffer", InstanceFields).GetValue(frame);
            buildings[0] = new BuildingView(2, 2, BuildingKind.Base, new Point(rules.MapSize * .5f), 4);
            typeof(GameplayFrame).GetField("BuildingCount", InstanceFields).SetValue(frame, 1);

            var memory = new BuildingMemory();
            map.SetBuildingMemory(memory);
            map.ObserveGameplay(frame);
            int baseline = VertexCount();

            Assert.That(memory.Observe(frame), Is.True);
            Assert.That(MarkerCoversMapCenter(), Is.True);

            Assert.That(memory.Forget(2, 11), Is.True);
            Assert.That(VertexCount(), Is.GreaterThan(baseline));
            Assert.That(MarkerCoversMapCenter(), Is.False);

            Assert.That(memory.ConfirmAbsent(new[] { 2 }, 12, 2), Is.True);
            Assert.That(VertexCount(), Is.EqualTo(baseline));
        }

        [Test]
        public void MoveRouteTracksTheServerDestinationAndDisappearsOnArrival()
        {
            var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), InstanceFields, null, new object[] { rules }, null);
            typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.PlayerId)).SetValue(frame, 1);
            var squads = (SquadView[])typeof(GameplayFrame).GetField("SquadBuffer", InstanceFields).GetValue(frame);
            float size = rules.MapSize;
            var start = new Point(size * .2f, size * .5f);
            var destination = new Point(size * .8f, size * .5f);
            squads[0] = new SquadView(1, start, default, default, destination, SquadOrderKind.MoveTo, 1, 1, 0, 0);
            map.ObserveGameplay(frame);

            Assert.That(MarkerCoversPoint(new Vector3(-59, 0)), Is.True);
            Assert.That(MarkerCoversPoint(new Vector3(-55, 0)), Is.False);
            Assert.That(MarkerCoversPoint(new Vector3(59, 0)), Is.True);

            var newStart = new Point(size * .3f, size * .5f);
            var newDestination = new Point(size * .7f, size * .5f);
            squads[0] = new SquadView(1, newStart, default, default, newDestination, SquadOrderKind.MoveTo, 2, 1, 0, 0);
            map.ObserveGameplay(frame);

            Assert.That(MarkerCoversPoint(new Vector3(-59, 0)), Is.False);
            Assert.That(MarkerCoversPoint(new Vector3(-39, 0)), Is.True);
            Assert.That(MarkerCoversPoint(new Vector3(39, 0)), Is.True);
            Assert.That(MarkerCoversPoint(new Vector3(59, 0)), Is.False);

            squads[0] = Squad(1, newDestination);
            map.ObserveGameplay(frame);
            Assert.That(MarkerCoversPoint(new Vector3(-39, 0)), Is.False);
            Assert.That(MarkerCoversPoint(new Vector3(39, 0)), Is.False);
        }

        [Test]
        public void BlockingPointerDiscardsTheOldDragBeforeInputReturns()
        {
            var pointer = new PointerEventData(null)
            {
                button = PointerEventData.InputButton.Right,
                position = RectTransformUtility.WorldToScreenPoint(null, map.rectTransform.position)
            };
            int destinations = 0;
            map.MovePointerChanged += (point, held, pressed) => { if (point.HasValue) destinations++; };

            ExecuteEvents.Execute(map.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            Assert.That(destinations, Is.EqualTo(1));
            map.SetPointerBlocked(true);
            map.SetPointerBlocked(false);
            ExecuteEvents.Execute(map.gameObject, pointer, ExecuteEvents.dragHandler);
            ExecuteEvents.Execute(map.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            Assert.That(destinations, Is.EqualTo(1));
            ExecuteEvents.Execute(map.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            Assert.That(destinations, Is.EqualTo(2));
        }

        private static SquadView Squad(byte id, Point center) => new SquadView(id, center, default, default, default, default, 0, 1, 0, 0);

        private void Publish(SnapshotTimeline timeline, UnitSnapshotAssembler snapshot, long tick, float x, uint revision, int owner = 1)
        {
            typeof(UnitSnapshotAssembler).GetProperty(nameof(UnitSnapshotAssembler.Tick)).SetValue(snapshot, tick);
            snapshot.Units[0] = new NetworkUnit(7, owner, 0, new Point(x, 100), false, 10, 0, false, 0,
                squadId: 0, isReinforcing: true, movementRevision: revision);
            timeline.Push(snapshot);
            map.ObserveUnits(timeline, 1);
        }

        private int VertexCount()
        {
            using (var mesh = new VertexHelper())
            {
                Populate(mesh);
                return mesh.currentVertCount;
            }
        }

        private bool MarkerCoversMapCenter() => MarkerCoversPoint(map.rectTransform.rect.center);

        private bool MarkerCoversPoint(Vector3 point)
        {
            var vertices = new List<UIVertex>();
            using (var mesh = new VertexHelper())
            {
                Populate(mesh);
                mesh.GetUIVertexStream(vertices);
            }
            for (int index = 0; index < vertices.Count; index += 3)
            {
                Vector3 a = vertices[index].position - point;
                Vector3 b = vertices[index + 1].position - point;
                Vector3 c = vertices[index + 2].position - point;
                float ab = Vector3.Cross(a, b).z, bc = Vector3.Cross(b, c).z, ca = Vector3.Cross(c, a).z;
                if ((ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0)) return true;
            }
            return false;
        }

        private void Populate(VertexHelper mesh) =>
            typeof(ObservatoryMap).GetMethod("OnPopulateMesh", InstanceFields, null, new[] { typeof(VertexHelper) }, null)
                .Invoke(map, new object[] { mesh });
    }
}
