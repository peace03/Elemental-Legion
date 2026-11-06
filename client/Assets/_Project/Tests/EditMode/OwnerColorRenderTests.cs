using System;
using System.Reflection;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    /// <summary>서버 소유 색의 링·건물·미니맵 반영과 관측 색 기억의 수명을 확인한다.</summary>
    public sealed class OwnerColorRenderTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void SameElementUsesDifferentOwnerRingsWhileItsBodyMaterialStaysUnchanged()
        {
            GameObject root = Prefab();
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                GameRules rules = Rules();
                GameplayFrame frame = Frame(rules, out SnapshotTimeline timeline, out _);
                ElementArtProfile water = AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder + "/water.asset");
                Color body = water.Body.GetColor("_BaseColor");
                Color edge = water.Body.GetColor("_SecondColor");
                visuals.Observe(frame);
                visuals.Render(timeline, 21, new Point(110, 100), frame.Tick);

                AssertTint(Properties(visuals, 2, "OwnerUnitRing"), Palette(2));
                AssertTint(Properties(visuals, 9, "OwnerUnitRing"), Palette(9));
                Assert.That(Palette(2), Is.Not.EqualTo(Palette(9)));
                AssertBatchContains(visuals, 2, "OwnerUnitRing", new Vector3(110, .12f, 100));
                AssertBatchContains(visuals, 9, "OwnerUnitRing", new Vector3(112, .12f, 100));
                Assert.That(water.Body.GetColor("_BaseColor"), Is.EqualTo(body));
                Assert.That(water.Body.GetColor("_SecondColor"), Is.EqualTo(edge));
                Assert.That(Properties(visuals, 2, "OwnerBuilding").GetFloat("_Element"),
                    Is.EqualTo(AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder + "/crystal.asset").Body.GetFloat("_Element")));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ColorBatchGrowthRetainsRingsBeyondItsWarmCapacity()
        {
            GameObject root = Prefab();
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                GameplayFrame frame = Frame(Rules(), out SnapshotTimeline timeline, out _, 140);
                visuals.Observe(frame);
                visuals.Render(timeline, 21, new Point(110, 100), frame.Tick);
                AssertBatchContains(visuals, PlayerPalette.Count, "OwnerUnitRing", new Vector3(110, .12f, 100));
                AssertBatchContains(visuals, PlayerPalette.Count, "OwnerUnitRing", new Vector3(116.3f, .12f, 109.1f));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(.06f)]
        [TestCase(.22f)]
        [TestCase(.4f)]
        public void UnitRingThickensInwardWithoutExceedingTheRenderedFootprint(float thickness)
        {
            GameObject root = Prefab();
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                var settings = new SerializedObject(visuals);
                settings.FindProperty("unitRingThickness").floatValue = thickness;
                settings.ApplyModifiedPropertiesWithoutUndo();
                Mesh buildingRing = (Mesh)settings.FindProperty("ring").objectReferenceValue;
                Vector3 buildingBounds = buildingRing.bounds.size;
                visuals.Initialize();
                GameplayFrame frame = Frame(Rules(), out SnapshotTimeline timeline, out _);
                visuals.Observe(frame);
                visuals.Render(timeline, 21, new Point(110, 100), frame.Tick);
                Mesh mesh = UnitRing(visuals);
                Assert.That(mesh, Is.Not.SameAs(buildingRing));
                Assert.That(buildingRing.bounds.size, Is.EqualTo(buildingBounds));
                Assert.That(mesh.bounds.size.x, Is.EqualTo(2).Within(.0001f));
                Assert.That(mesh.bounds.size.y, Is.Zero.Within(.0001f));
                Assert.That(mesh.bounds.size.z, Is.EqualTo(2).Within(.0001f));
                Vector3[] vertices = mesh.vertices;
                float footprint = Mathf.Min(settings.FindProperty("unitFootprintRadius").floatValue, Rules().MinimumSeparation * .5f);
                var matrices = (Matrix4x4[][])typeof(ElementalVisuals).GetField("matrices", Private).GetValue(visuals);
                Matrix4x4 matrix = matrices[Constant("OwnershipStart") + 2 * Constant("OwnershipKinds") + Constant("OwnerUnitRing")][0];
                Vector3 center = matrix.GetColumn(3);
                for (int index = 0; index < vertices.Length; index++)
                {
                    Assert.That(vertices[index].magnitude, Is.EqualTo(index % 2 == 0 ? 1 : 1 - thickness).Within(.0001f));
                    Assert.That((matrix.MultiplyPoint3x4(vertices[index]) - center).magnitude, Is.LessThanOrEqualTo(footprint + .0001f));
                }
                int[] triangles = mesh.triangles;
                for (int index = 0; index < triangles.Length; index += 3)
                {
                    Vector3 normal = Vector3.Cross(vertices[triangles[index + 1]] - vertices[triangles[index]],
                        vertices[triangles[index + 2]] - vertices[triangles[index]]);
                    Assert.That(normal.y, Is.GreaterThan(0), "위쪽 카메라에서 보이는 면이어야 합니다.");
                }
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ClearKeepsTheReusableUnitMeshAndDisableReleasesItsNativeResource()
        {
            GameObject root = Prefab();
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                Mesh first = UnitRing(visuals);
                visuals.Clear();
                Assert.That(UnitRing(visuals), Is.SameAs(first), "관측 초기화마다 메시를 다시 할당하면 안 됩니다.");
                root.SetActive(false);
                Assert.That(first == null, Is.True, "OnDisable에서 네이티브 메시를 해제해야 합니다.");
                Assert.That(UnitRing(visuals) == null, Is.True);
                root.SetActive(true);
                visuals.Initialize();
                Mesh second = UnitRing(visuals);
                Assert.That(second != null, Is.True);
                Assert.That(ReferenceEquals(first, second), Is.False);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void GhostAndUnfinishedBuildingsKeepTheirOwnersTintAndConfiguredOpacity()
        {
            GameObject root = Prefab();
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                visuals.Observe(Frame(Rules(), out _, out _));
                var ghost = new BuildingView(501, 62, BuildingKind.Collector, new Point(30, 40), 3, 1, 1, -1, 100);
                Invoke(visuals, "RenderBuilding", ghost, 21);
                AssertBatchContains(visuals, 9, "OwnerGhostRing", new Vector3(30, .08f, 40));
                AssertTint(Properties(visuals, 9, "OwnerGhostBuilding"), Palette(9));
                var unfinished = new BuildingView(502, 21, BuildingKind.Collector, new Point(35, 40), 3, 1, 1, -1, 0, 100);
                Invoke(visuals, "RenderBuilding", unfinished, 21);
                AssertTint(Properties(visuals, 2, "OwnerUnfinished"), Palette(2));
                var settings = new SerializedObject(visuals);
                float ghostOpacity = settings.FindProperty("ghostOpacity").floatValue;
                Assert.That(Properties(visuals, 9, "OwnerGhostBuilding").GetFloat("_Opacity"), Is.EqualTo(ghostOpacity));
                Assert.That(Properties(visuals, 9, "OwnerGhostRing").GetFloat("_Opacity"), Is.EqualTo(ghostOpacity));
                Invoke(visuals, "AddUnitOwnership", 62, true, Matrix4x4.TRS(new Vector3(30, .12f, 40), Quaternion.identity, Vector3.one * .6f));
                AssertBatchContains(visuals, 9, "OwnerGhostUnitRing", new Vector3(30, .12f, 40));
                AssertTint(Properties(visuals, 9, "OwnerGhostUnitRing"), Palette(9));
                Assert.That(Properties(visuals, 9, "OwnerGhostUnitRing").GetFloat("_Opacity"), Is.EqualTo(ghostOpacity));
                Assert.That(Properties(visuals, 2, "OwnerUnfinished").GetFloat("_Opacity"),
                    Is.EqualTo(settings.FindProperty("unfinishedOpacity").floatValue));
                Assert.That(Properties(visuals, 2, "OwnerDome").GetFloat("_Opacity"), Is.EqualTo(ghostOpacity * .5f));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void LargeOwnerIdsKeepTheirKnownColorUntilClearAndUnknownOwnersUseGray()
        {
            GameObject root = Prefab();
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                GameRules rules = Rules();
                visuals.Observe(Frame(rules, out _, out _));
                using (var empty = new GameplayReplica(rules)) visuals.Observe(empty.State);
                var building = new BuildingView(503, 62, BuildingKind.Collector, new Point(40, 40), 3);
                Invoke(visuals, "RenderBuilding", building, 21);
                AssertBatchContains(visuals, 9, "OwnerRing", new Vector3(40, .08f, 40));
                visuals.Clear();
                Invoke(visuals, "RenderBuilding", building, 21);
                AssertBatchContains(visuals, PlayerPalette.Count, "OwnerRing", new Vector3(40, .08f, 40));
                AssertTint(Properties(visuals, PlayerPalette.Count, "OwnerRing"), new Color32(164, 174, 183, 255));
                Invoke(visuals, "RenderBuilding", new BuildingView(504, -1, BuildingKind.Collector, new Point(50, 40), 3), 21);
                AssertBatchContains(visuals, PlayerPalette.Count, "OwnerRing", new Vector3(50, .08f, 40));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void MinimapUsesOwnerTintsAndResetDiscardsRememberedColors()
        {
            var root = new GameObject("Owner color map", typeof(RectTransform), typeof(CanvasRenderer), typeof(ObservatoryMap));
            var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            backdrop.transform.SetParent(root.transform, false);
            try
            {
                var map = root.GetComponent<ObservatoryMap>();
                GameRules rules = Rules();
                GameplayFrame frame = Frame(rules, out SnapshotTimeline timeline, out FogStateReplica fog);
                Invoke(map, "Initialize", backdrop.GetComponent<RawImage>(), rules);
                Invoke(map, "ObserveGameplay", frame);
                Invoke(map, "ObserveFog", fog);
                Invoke(map, "ObserveUnits", timeline, 21);
                Invoke(map, "ObservePose", new Point(110, 100));
                using (var mesh = new VertexHelper())
                {
                    Invoke(map, "OnPopulateMesh", mesh);
                    AssertMeshHasColor(mesh, Palette(2));
                    AssertMeshHasColor(mesh, Palette(9));
                }
                Invoke(map, "ResetMap");
                Invoke(map, "ObservePose", new Point(110, 100));
                using (var mesh = new VertexHelper())
                {
                    Invoke(map, "OnPopulateMesh", mesh);
                    AssertMeshHasColor(mesh, new Color32(164, 174, 183, 255));
                    Assert.That(mesh.currentVertCount, Is.EqualTo(9), "군단 중심 원형만 남고 이전 적 마커는 사라져야 합니다.");
                }
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static GameplayFrame Frame(GameRules rules, out SnapshotTimeline timeline, out FogStateReplica fog, int neutralUnits = 0)
        {
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(21, "water", new Point(100), new Point(110, 100), colorId: 2));
            setup.Players.Add(new PlayerSpawn(62, "water", new Point(300), new Point(112, 100), colorId: 9));
            setup.Units.Add(new UnitSpawn(21, "water", new Point(110, 100)));
            setup.Units.Add(new UnitSpawn(62, "water", new Point(112, 100)));
            for (int index = 0; index < neutralUnits; index++)
                setup.Units.Add(new UnitSpawn(-1, "water", new Point(110 + index % 10 * .7f, 100 + index / 10 * .7f)));
            var world = new SimulationWorld(rules, setup, 1);
            WorldView view = world.GetView(21);
            var writer = new NetDataWriter();
            var fogSender = new FogReplicationState(rules);
            fog = new FogStateReplica(rules);
            fogSender.Prepare(view);
            for (int part = 0; part < fogSender.ChunkCount; part++)
            { fogSender.WriteChunk(writer, part); fog.TryAccept(new NetDataReader(writer.CopyData())); }
            var sender = new GameplayReplicationState(rules);
            var replica = new GameplayReplica(rules);
            sender.Prepare(view, fogSender.Revision);
            for (int part = 0; part < sender.ChunkCount; part++)
            { sender.WriteChunk(writer, part); replica.TryAccept(new NetDataReader(writer.CopyData()), 0); }
            Assert.That(replica.TryPublish(fog, out GameplayFrame frame), Is.True);
            Assert.That(replica.PendingBytes, Is.Zero);
            var units = new UnitSnapshotAssembler(rules);
            timeline = new SnapshotTimeline(2);
            for (int part = 0; part < Protocol.ChunkCount(view.Units.Count); part++)
            {
                Protocol.WriteUnits(writer, view, rules.MapSize, part, fogSender.Revision);
                if (units.TryAccept(new NetDataReader(writer.CopyData()))) timeline.Push(units);
            }
            return frame;
        }

        private static void AssertMeshHasColor(VertexHelper mesh, Color expected)
        {
            var vertex = new UIVertex();
            for (int index = 0; index < mesh.currentVertCount; index++)
            {
                mesh.PopulateUIVertex(ref vertex, index);
                if (vertex.color.Equals((Color32)expected)) return;
            }
            Assert.Fail("미니맵에 소유 색 정점이 없습니다: " + expected);
        }

        private static void AssertBatchContains(ElementalVisuals visuals, int color, string kind, Vector3 position)
        {
            int start = Constant("OwnershipStart"), kinds = Constant("OwnershipKinds");
            int group = start + color * kinds + Constant(kind);
            var matrices = (Matrix4x4[][])typeof(ElementalVisuals).GetField("matrices", Private).GetValue(visuals);
            var counts = (int[])typeof(ElementalVisuals).GetField("counts", Private).GetValue(visuals);
            for (int index = 0; index < counts[group]; index++)
                if (((Vector3)matrices[group][index].GetColumn(3) - position).sqrMagnitude < .001f) return;
            Assert.Fail("소유 색 배치에 관측 위치가 없습니다: " + position);
        }

        private static MaterialPropertyBlock Properties(ElementalVisuals visuals, int color, string kind) =>
            ((MaterialPropertyBlock[][])typeof(ElementalVisuals).GetField("ownershipProperties", Private).GetValue(visuals))[color][Constant(kind)];
        private static Mesh UnitRing(ElementalVisuals visuals) => (Mesh)typeof(ElementalVisuals).GetField("unitOwnershipRing", Private).GetValue(visuals);
        private static int Constant(string name) => (int)typeof(ElementalVisuals).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        private static void AssertTint(MaterialPropertyBlock block, Color expected) => Assert.That((Color32)block.GetColor("_BaseColor"), Is.EqualTo((Color32)expected));
        private static Color Palette(int id)
        {
            uint rgb = PlayerPalette.GetRgb(id);
            return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
        }
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private | BindingFlags.DeclaredOnly).Invoke(target, args);
        private static GameObject Prefab() => Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
        private static GameRules Rules() => new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Packages/com.elementallegion.shared/Data/gamedata.json").text));
    }
}
