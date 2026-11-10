using System.Reflection;
using System;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>건설 경계와 유닛 링의 표시 두께 및 메시 수명을 확인한다.</summary>
    public sealed class FeedbackRingTests
    {
        [Test]
        public void ThinRingsKeepTheirOuterRadiusAndReleaseRuntimeMeshes()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var settings = new SerializedObject(visuals);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var unit = (Mesh)typeof(ElementalVisuals).GetField("unitOwnershipRing", flags).GetValue(visuals);
                var range = (Mesh)typeof(ElementalVisuals).GetField("constructionRangeOutline", flags).GetValue(visuals);
                Assert.That(settings.FindProperty("unitRingThickness").floatValue, Is.LessThanOrEqualTo(.1f));
                Assert.That(settings.FindProperty("constructionRangeThickness").floatValue, Is.LessThanOrEqualTo(.02f));
                AssertRing(unit, settings.FindProperty("unitRingThickness").floatValue);
                AssertRing(range, settings.FindProperty("constructionRangeThickness").floatValue);
                root.SetActive(false);
                Assert.That(unit == null, Is.True);
                Assert.That(range == null, Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void AssertRing(Mesh mesh, float thickness)
        {
            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.vertices[0].magnitude, Is.EqualTo(1).Within(.0001f));
            Assert.That(mesh.vertices[1].magnitude, Is.EqualTo(1 - thickness).Within(.0001f));
        }

        [TestCase(1, false, false, "water")]
        [TestCase(2, true, false, "water")]
        [TestCase(3, false, true, "fire")]
        [TestCase(4, true, true, "fire")]
        public void SquadMarksFollowRenderedGroupAboveItsScreenBounds(byte squadId, bool crowded, bool predicted, string elementId)
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var camera = visuals.TargetCamera;
                camera.transform.rotation = Quaternion.Euler(52, 35, 15);
                camera.transform.position = new Vector3(100, 0, 100) - camera.transform.forward * 70;
                int element = rules.ElementIndex(elementId);
                var timeline = new SnapshotTimeline(2);
                for (int tick = 1; tick <= 3; tick += 2)
                {
                    var writer = new NetDataWriter();
                    writer.Put((byte)PacketKind.Units); writer.Put((long)tick); writer.Put(1);
                    writer.Put(100f); writer.Put(100f); writer.Put((ushort)2); writer.Put((ushort)0); writer.Put(0u);
                    for (int index = 0; index < 2; index++)
                    {
                        writer.Put(index + 1); writer.Put(1); writer.Put((byte)element);
                        writer.Put((short)Math.Round(((tick - 1) * 2 + (crowded ? 0 : index * 10)) / rules.MapSize * short.MaxValue));
                        writer.Put((short)Math.Round((crowded ? 0 : index * 4) / rules.MapSize * short.MaxValue));
                        writer.Put((byte)(squadId == 4 ? 1 : 0)); writer.Put(rules.Elements[element].Hp); writer.Put(0f); writer.Put((ushort)0);
                        writer.Put(squadId); writer.Put(0u);
                    }
                    var assembler = new UnitSnapshotAssembler(rules);
                    Assert.That(assembler.TryAccept(new NetDataReader(writer.CopyData())), Is.True);
                    timeline.Push(assembler);
                    timeline.ObservePose(new LegionPose(tick, 1, 0, new System.Numerics.Vector2(100), default, squadId));
                }
                var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), hidden, null, new object[] { rules }, null);
                typeof(GameplayFrame).GetProperty("PlayerId").SetValue(frame, 1);
                var squads = (SquadView[])typeof(GameplayFrame).GetField("SquadBuffer", hidden).GetValue(frame);
                squads[squadId - 1] = new SquadView(squadId, new System.Numerics.Vector2(40), default, default, default, SquadOrderKind.Hold, 0, 2, 0, 0);
                visuals.Observe(frame);
                visuals.Render(timeline, 1, new System.Numerics.Vector2(predicted ? 110 : 100), 2, predicted ? squadId : (byte)(squadId == 1 ? 2 : 1));
                var matrices = (Matrix4x4[][])typeof(ElementalVisuals).GetField("matrices", hidden).GetValue(visuals);
                var profiles = (ElementArtProfile[])typeof(ElementalVisuals).GetField("profiles", hidden).GetValue(visuals);
                var body = new Bounds();
                bool first = true;
                if (!profiles[element].ParticleBody)
                    for (int unit = 0; unit < 2; unit++) IncludeMesh(camera, profiles[element].Mesh, matrices[element][unit], ref body, ref first);
                var layers = (ParticleSystem[])typeof(ElementalVisuals).GetField("bodyLayers", hidden).GetValue(visuals);
                var particles = new ParticleSystem.Particle[layers[element].particleCount];
                layers[element].GetParticles(particles);
                foreach (var particle in particles)
                {
                    float radians = particle.rotation * Mathf.Deg2Rad;
                    Vector3 size = particle.startSize3D;
                    float halfX = (Mathf.Abs(Mathf.Cos(radians)) * size.x + Mathf.Abs(Mathf.Sin(radians)) * size.y) * .5f;
                    float halfY = (Mathf.Abs(Mathf.Sin(radians)) * size.x + Mathf.Abs(Mathf.Cos(radians)) * size.y) * .5f;
                    Include(camera.WorldToScreenPoint(particle.position - camera.transform.right * halfX - camera.transform.up * halfY), ref body, ref first);
                    Include(camera.WorldToScreenPoint(particle.position + camera.transform.right * halfX + camera.transform.up * halfY), ref body, ref first);
                }
                var draw = (Matrix4x4[])typeof(ElementalVisuals).GetField("draw", hidden).GetValue(visuals);
                var crystal = (Mesh)typeof(ElementalVisuals).GetField("crystal", hidden).GetValue(visuals);
                var marks = new Bounds(); first = true;
                for (int index = 0; index < squadId; index++) IncludeMesh(camera, crystal, draw[index], ref marks, ref first);
                Assert.That(marks.center.x, Is.EqualTo(body.center.x).Within(3), "카메라가 회전해도 실제 몸체 범위의 가로 중앙을 따라야 합니다.");
                Assert.That(marks.min.y, Is.GreaterThan(body.max.y), "마름모 전체가 몸체 위쪽에 있어야 합니다.");
                float markSize = new SerializedObject(visuals).FindProperty("squadMarkSize").floatValue;
                float gap = (camera.WorldToScreenPoint(camera.ScreenToWorldPoint(body.center) + camera.transform.up * markSize) - body.center).y;
                Assert.That(marks.min.y - body.max.y, Is.LessThan(gap * 3), "분대 크기와 무관한 고정 높이로 멀리 뜨면 안 됩니다.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void IncludeMesh(UnityEngine.Camera camera, Mesh mesh, Matrix4x4 pose, ref Bounds bounds, ref bool first)
        {
            Bounds local = mesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                Include(camera.WorldToScreenPoint(pose.MultiplyPoint3x4(point)), ref bounds, ref first);
            }
        }

        private static void Include(Vector3 point, ref Bounds bounds, ref bool first)
        {
            if (first) { bounds = new Bounds(point, Vector3.zero); first = false; }
            else bounds.Encapsulate(point);
        }
    }
}
