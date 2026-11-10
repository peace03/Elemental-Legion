using System;
using System.Linq;
using ElementalLegion.Client.Legion;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>전장 표시의 맵 경계·중복 초기화·정리 후 상태 보존을 확인한다.</summary>
    public sealed class BattlefieldPresentationTests
    {
        private GameObject root;
        private GameObject cameraRoot;
        private Material material;
        private BattlefieldPresentation presentation;
        private UnityEngine.Camera camera;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Battlefield test");
            cameraRoot = new GameObject("Battlefield camera test", typeof(UnityEngine.Camera));
            camera = cameraRoot.GetComponent<UnityEngine.Camera>();
            presentation = root.AddComponent<BattlefieldPresentation>();
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var serialized = new SerializedObject(presentation);
            serialized.FindProperty("groundMaterial").objectReferenceValue = material;
            serialized.FindProperty("rockMaterial").objectReferenceValue = material;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(cameraRoot);
            Object.DestroyImmediate(material);
        }

        [Test]
        public void GroundCoversAuthoritativeMapAndRemainsBelowMarkers()
        {
            presentation.Initialize(camera, 240);
            Mesh mesh = root.GetComponentsInChildren<MeshFilter>().Single(filter => filter.name == "Basalt ground").sharedMesh;
            Assert.That(mesh.bounds.min.x, Is.EqualTo(0).Within(.0001));
            Assert.That(mesh.bounds.min.z, Is.EqualTo(0).Within(.0001));
            Assert.That(mesh.bounds.max.x, Is.EqualTo(240).Within(.0001));
            Assert.That(mesh.bounds.max.z, Is.EqualTo(240).Within(.0001));
            foreach (Vector3 point in mesh.vertices)
            {
                Assert.That(float.IsFinite(point.sqrMagnitude), Is.True, "유한하지 않은 정점: " + point);
                Assert.That(point.y, Is.InRange(-.075f, 0), "지면 높이 범위를 벗어난 정점: " + point);
            }
            Assert.That(mesh.bounds.size.y, Is.GreaterThan(.01f));
            Assert.That(root.GetComponentsInChildren<Collider>(), Is.Empty);
        }

        [Test]
        public void ReinitializeReusesSameMapAndReleasePreservesBorrowedState()
        {
            camera.backgroundColor = Color.magenta;
            Color ambient = RenderSettings.ambientLight;
            presentation.Initialize(camera, 240);
            var originalMesh = root.GetComponentsInChildren<MeshFilter>()[0].sharedMesh;
            presentation.Initialize(camera, 240);
            Assert.That(root.GetComponentsInChildren<MeshFilter>(), Has.Length.EqualTo(2));
            Assert.That(root.GetComponentsInChildren<MeshFilter>()[0].sharedMesh, Is.SameAs(originalMesh));
            presentation.Initialize(camera, 480);
            Assert.That(root.GetComponentsInChildren<MeshFilter>(), Has.Length.EqualTo(2));
            Assert.That(originalMesh == null, Is.True);
            presentation.Release();
            presentation.Release();
            Assert.That(root.transform.childCount, Is.Zero);
            Assert.That(camera.backgroundColor, Is.EqualTo(Color.magenta));
            Assert.That(RenderSettings.ambientLight, Is.EqualTo(ambient));
            Assert.That(material != null, Is.True);
        }

        [Test]
        public void InvalidMapDoesNotDestroyExistingPresentation()
        {
            presentation.Initialize(camera, 240);
            Assert.Throws<ArgumentOutOfRangeException>(() => presentation.Initialize(camera, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => presentation.Initialize(camera, 0));
            Assert.That(root.GetComponentsInChildren<MeshFilter>(), Has.Length.EqualTo(2));
        }
    }
}
