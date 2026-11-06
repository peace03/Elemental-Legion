using System;
using System.Collections.Generic;
using System.Reflection;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    /// <summary>원소별 실루엣·동작·표시 반경과 시야를 잃은 효과의 정리를 검증한다.</summary>
    public sealed class ElementMotionTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ElementGlowUsesACameraOverrideAndReleasesItsProfile(bool existingCameraData)
        {
            GameObject root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            VolumeProfile profile = null;
            try
            {
                var camera = root.GetComponentInChildren<UnityEngine.Camera>(true);
                var original = camera.GetComponent<UniversalAdditionalCameraData>();
                if (existingCameraData)
                {
                    original = original ?? camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                    original.renderPostProcessing = true;
                    original.volumeLayerMask = 1 << 10;
                }
                bool postWasEnabled = original != null && original.renderPostProcessing;
                int originalMask = original != null ? original.volumeLayerMask.value : 0;
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                Assert.That(data, Is.Not.Null);
                Assert.That(data.renderPostProcessing, Is.True);
                var volumes = root.GetComponentsInChildren<Volume>(true);
                Assert.That(volumes.Length, Is.EqualTo(1));
                profile = volumes[0].sharedProfile;
                Assert.That(profile.TryGet<Bloom>(out var bloom), Is.True);
                Assert.That(bloom.intensity.overrideState, Is.True);
                Assert.That(bloom.intensity.value, Is.GreaterThan(0));
                root.SetActive(false);
                if (original == null) Assert.That(camera.GetComponent<UniversalAdditionalCameraData>(), Is.Null);
                else
                {
                    Assert.That(data.renderPostProcessing, Is.EqualTo(postWasEnabled));
                    Assert.That(data.volumeLayerMask.value, Is.EqualTo(originalMask));
                }
                Assert.That(profile == null, Is.True, "일시적인 발광 설정을 정리해야 합니다.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (profile != null) Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void FourteenElementsHaveDistinctReadableSilhouettesAndAnimationControls()
        {
            var rules = Rules();
            var meshes = new HashSet<Mesh>();
            foreach (var definition in rules.Elements)
            {
                var profile = AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder + "/" + definition.Id + ".asset");
                Assert.That(meshes.Add(profile.Mesh), Is.True, definition.Id + "의 실루엣이 다른 원소와 같아서는 안 됩니다.");
                Assert.That(profile.Mesh.isReadable, Is.True);
                Assert.That(profile.Mesh.subMeshCount, Is.EqualTo(1));
                Assert.That(profile.Mesh.vertexCount, Is.InRange(100, 2500));
                Assert.That(profile.Mesh.normals.Length, Is.EqualTo(profile.Mesh.vertexCount));
                Assert.That(profile.Mesh.uv.Length, Is.EqualTo(profile.Mesh.vertexCount));
                Assert.That(profile.Mesh.colors.Length, Is.EqualTo(profile.Mesh.vertexCount));
                foreach (Vector3 vertex in profile.Mesh.vertices)
                    Assert.That(float.IsNaN(vertex.sqrMagnitude) || float.IsInfinity(vertex.sqrMagnitude), Is.False, definition.Id);
                var serialized = new SerializedObject(profile);
                foreach (string field in new[] { "hoverAmplitude", "motionFrequency", "spinSpeed", "pulseAmplitude", "attackRecoil", "attackDuration", "projectileSpeed", "bodyParticleCount", "deformationPadding" })
                    Assert.That(serialized.FindProperty(field), Is.Not.Null, definition.Id + ": " + field);
            }
        }

        [Test]
        public void EveryBodyMovesOverTimeAndDenseUnitsStayInsideTheirFootprints()
        {
            GameObject root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                var serialized = new SerializedObject(visuals);
                const float radius = 1f;
                serialized.FindProperty("unitFootprintRadius").floatValue = radius;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                visuals.Initialize();
                visuals.RenderGallery(14, 0, false);
                var matrices = (Matrix4x4[][])typeof(ElementalVisuals).GetField("matrices", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(visuals);
                var first = new Matrix4x4[14];
                for (int element = 0; element < 14; element++) first[element] = matrices[element][0];
                visuals.RenderGallery(14, 0.7f, false);
                for (int element = 0; element < 14; element++)
                    Assert.That(matrices[element][0], Is.Not.EqualTo(first[element]), "대기 중에도 본체가 살아 움직여야 합니다: " + element);
                visuals.RenderGallery(988, 0.7f, false);
                var profiles = serialized.FindProperty("profiles");
                var rules = Rules();
                var water = (ElementArtProfile)profiles.GetArrayElementAtIndex(rules.ElementIndex("water")).objectReferenceValue;
                Assert.That(serialized.FindProperty("domeMesh").objectReferenceValue,
                    Is.EqualTo(AssetDatabase.LoadAssetAtPath<Mesh>("Assets/_Project/Meshes/ElementSphere.asset")));
                Assert.That(serialized.FindProperty("domeMesh").objectReferenceValue, Is.Not.EqualTo(water.Mesh), "물의 위성 물방울을 자기권으로 확대하면 안 됩니다.");
                float largestRadius = 0;
                for (int element = 0; element < 14; element++)
                {
                    var profile = (ElementArtProfile)profiles.GetArrayElementAtIndex(element).objectReferenceValue;
                    Matrix4x4 matrix = matrices[element][0];
                    Vector3 center = matrix.GetColumn(3);
                    foreach (Vector3 vertex in profile.Mesh.vertices)
                    {
                        Vector3 point = matrix.MultiplyPoint3x4(vertex) - center;
                        float extent = new Vector2(point.x, point.z).magnitude;
                        Assert.That(extent, Is.LessThanOrEqualTo(radius + 0.001f), profile.ElementId);
                        largestRadius = Mathf.Max(largestRadius, extent);
                    }
                }
                Assert.That(largestRadius, Is.GreaterThan(rules.MinimumSeparation * 0.5f + 0.001f),
                    "본체의 표시 반경에 유닛 간격 제한을 적용하면 안 됩니다.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void NearbyBodiesUseTheAvailableWidthAndKeepTheirObservedCenters()
        {
            var rules = Rules();
            var center = new WorldPoint(840, 840);
            float halfSlot = rules.SlotSpacing * .5f;
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "earth", center - new WorldPoint(30, 0), center, 3));
            setup.Units.Add(new UnitSpawn(1, "earth", center - new WorldPoint(halfSlot, 0)));
            setup.Units.Add(new UnitSpawn(1, "earth", center + new WorldPoint(halfSlot, 0)));
            setup.Units.Add(new UnitSpawn(1, "earth", center + new WorldPoint(5, 0)));
            var world = new SimulationWorld(rules, setup, 1);
            var view = world.GetView(1);
            var writer = new NetDataWriter();
            Protocol.WriteUnits(writer, view, rules.MapSize, 0);
            var snapshot = new UnitSnapshotAssembler(rules);
            Assert.That(snapshot.TryAccept(new NetDataReader(writer.CopyData())), Is.True);
            var timeline = new SnapshotTimeline(4);
            timeline.Push(snapshot);

            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                visuals.Render(timeline, 1, center, view.Tick);
                int earth = rules.ElementIndex("earth");
                var profile = AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder + "/earth.asset");
                var matrices = (Matrix4x4[][])typeof(ElementalVisuals)
                    .GetField("matrices", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(visuals);
                Vector3[] vertices = profile.Mesh.vertices;
                float left = Radius(matrices[earth][0]);
                float right = Radius(matrices[earth][1]);
                float alone = Radius(matrices[earth][2]);
                for (int index = 0; index < view.Units.Count; index++)
                {
                    Assert.That(timeline.TrySample(view.Units[index].Id, view.Tick, out WorldPoint location), Is.True);
                    Matrix4x4 pose = matrices[earth][index];
                    Assert.That(pose.m03, Is.EqualTo(location.X).Within(.001f));
                    Assert.That(pose.m23, Is.EqualTo(location.Y).Within(.001f));
                }
                float spacing = matrices[earth][1].m03 - matrices[earth][0].m03;
                Assert.That(left + right, Is.LessThanOrEqualTo(spacing + .002f));
                Assert.That(alone, Is.GreaterThan(Mathf.Max(left, right) + .05f));

                float Radius(Matrix4x4 pose)
                {
                    float radius = 0;
                    foreach (Vector3 vertex in vertices)
                    {
                        Vector3 offset = pose.MultiplyVector(vertex);
                        radius = Mathf.Max(radius, new Vector2(offset.x, offset.z).magnitude);
                    }
                    return radius;
                }
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void AttackTravelsAndForgettingItsSourceClearsTheEffect()
        {
            GameObject root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                int element = Rules().ElementIndex("water");
                var data = (SimEvent)Activator.CreateInstance(typeof(SimEvent), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { SimEventKind.Attack, 1, 44, 55, new System.Numerics.Vector2(7, 0), 1f, 0, 1UL, element,
                        System.Numerics.Vector2.Zero, true, 55, ObservedEntityKind.Unit, ObservedEntityKind.Unit, ObservedEntityKind.Unit }, null);
                var entry = (NetworkEvent)Activator.CreateInstance(typeof(NetworkEvent), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { 1UL, 10L, data }, null);
                visuals.Play(entry);
                var particles = new ParticleSystem.Particle[256];
                bool moving = false;
                foreach (var system in root.GetComponentsInChildren<ParticleSystem>(true))
                {
                    int count = system.GetParticles(particles);
                    for (int index = 0; index < count; index++) moving |= particles[index].velocity.x > 2;
                }
                Assert.That(moving, Is.True, "공격 입자가 실제로 시작점에서 목표 쪽으로 이동해야 합니다.");
                visuals.Forget(new ObservedEntityId(ObservedEntityKind.Unit, 44));
                foreach (var system in root.GetComponentsInChildren<ParticleSystem>(true)) Assert.That(system.particleCount, Is.Zero);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static GameRules Rules() => new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Packages/com.elementallegion.shared/Data/gamedata.json").text));
    }
}
