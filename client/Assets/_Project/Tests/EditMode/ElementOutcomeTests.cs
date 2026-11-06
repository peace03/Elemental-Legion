using System;
using System.Linq;
using System.Reflection;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class ElementOutcomeTests
    {
        [Test]
        public void ConfirmedOutcomesUseDifferentShapesAndTrajectories()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                visuals.Play(Entry(SimEventKind.Death, ObservedEntityKind.Unit));
                var death = ActiveSystems(root).Single();
                Assert.That(death.GetComponent<ParticleSystemRenderer>().renderMode, Is.EqualTo(ParticleSystemRenderMode.Mesh));
                visuals.Clear();

                const int colorId = 2;
                const int ownerId = 21;
                var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                    "Packages/com.elementallegion.shared/Data/gamedata.json").text));
                var frame = (GameplayFrame)Create(typeof(GameplayFrame), rules);
                var colors = (System.Collections.IList)typeof(GameplayFrame).GetField("OwnerColorBuffer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(frame);
                colors.Add(new OwnerColorView(ownerId, colorId));
                visuals.Observe(frame);
                visuals.Play(Entry(SimEventKind.Converted, ObservedEntityKind.Unit, ownerId));
                var conversion = ActiveSystems(root).Single();
                var conversionRenderer = conversion.GetComponent<ParticleSystemRenderer>();
                Assert.That(conversionRenderer.renderMode, Is.EqualTo(ParticleSystemRenderMode.Billboard));
                var properties = new MaterialPropertyBlock();
                conversionRenderer.GetPropertyBlock(properties);
                uint rgb = PlayerPalette.GetRgb(colorId);
                Color expected = new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
                var actual = properties.GetColor("_BaseColor");
                Assert.That(actual.r, Is.EqualTo(expected.r).Within(.001f));
                Assert.That(actual.g, Is.EqualTo(expected.g).Within(.001f));
                Assert.That(actual.b, Is.EqualTo(expected.b).Within(.001f));

                visuals.Play(Entry(SimEventKind.CoreDropped, ObservedEntityKind.Core));
                var drop = ActiveSystems(root).Single(system => system != conversion);
                var dropRenderer = drop.GetComponent<ParticleSystemRenderer>();
                Assert.That(dropRenderer.renderMode, Is.EqualTo(ParticleSystemRenderMode.Mesh));
                Assert.That(dropRenderer.mesh, Is.Not.Null);
                Assert.That(drop.main.gravityModifier.constant, Is.GreaterThan(0));
                dropRenderer.GetPropertyBlock(properties);
                Color amber = properties.GetColor("_BaseColor");
                Assert.That(amber.r, Is.GreaterThan(amber.g));
                Assert.That(amber.g, Is.GreaterThan(amber.b));

                AssertTrajectory(conversion, inward: true);
                AssertTrajectory(drop, inward: false);
                visuals.Forget(new ObservedEntityId(ObservedEntityKind.Unit, 55));
                Assert.That(ActiveSystems(root).Single(), Is.EqualTo(drop));
                visuals.Forget(new ObservedEntityId(ObservedEntityKind.Core, 55));
                Assert.That(ActiveSystems(root), Is.Empty);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void SaturatedPoolKeepsOneOutcomeStyleAndResetsOnReuse()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                var settings = new SerializedObject(visuals);
                settings.FindProperty("maxEffects").intValue = 1;
                settings.FindProperty("maxParticles").intValue = 64;
                settings.ApplyModifiedPropertiesWithoutUndo();
                visuals.Initialize();
                visuals.Play(Entry(SimEventKind.Death, ObservedEntityKind.Unit));
                visuals.Play(Entry(SimEventKind.CoreDropped, ObservedEntityKind.Core));
                var system = ActiveSystems(root).Single();
                AssertTrajectory(system, inward: false);
                visuals.Play(Entry(SimEventKind.Death, ObservedEntityKind.Unit));
                AssertTrajectory(system, inward: false);
                visuals.Play(Entry(SimEventKind.Converted, ObservedEntityKind.Unit));
                Assert.That(ActiveSystems(root).Single(), Is.EqualTo(system));
                Assert.That(system.main.gravityModifier.constant, Is.Zero);
                Assert.That(system.rotationOverLifetime.enabled, Is.False);
                Assert.That(system.GetComponent<ParticleSystemRenderer>().mesh, Is.Null);
                AssertTrajectory(system, inward: true);
                Assert.That(visuals.ActiveParticles, Is.InRange(1, 64));
                visuals.Clear();
                Assert.That(ActiveSystems(root), Is.Empty);
                Assert.That(visuals.ActiveParticles, Is.Zero);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ConfirmedDropHasRoomWhenOrdinaryDeathFillsTheParticleBudget()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                var settings = new SerializedObject(visuals);
                settings.FindProperty("maxEffects").intValue = 2;
                settings.FindProperty("maxParticles").intValue = 64;
                settings.ApplyModifiedPropertiesWithoutUndo();
                visuals.Initialize();
                visuals.Play(Entry(SimEventKind.Death, ObservedEntityKind.Unit));
                Assert.That(visuals.ActiveParticles, Is.EqualTo(64));
                visuals.Play(Entry(SimEventKind.CoreDropped, ObservedEntityKind.Core));
                AssertTrajectory(ActiveSystems(root).Single(), inward: false);
                Assert.That(visuals.ActiveParticles, Is.InRange(1, 64));
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void AssertTrajectory(ParticleSystem system, bool inward)
        {
            var particles = new ParticleSystem.Particle[256];
            int count = system.GetParticles(particles);
            Assert.That(count, Is.GreaterThan(0));
            for (int index = 0; index < count; index++)
            {
                var particle = particles[index];
                float radial = particle.position.x * particle.velocity.x + particle.position.z * particle.velocity.z;
                Assert.That(inward ? -radial : radial, Is.GreaterThan(0));
                Assert.That(particle.velocity.y, Is.GreaterThan(0));
            }
        }

        private static ParticleSystem[] ActiveSystems(GameObject root) => root.GetComponentsInChildren<ParticleSystem>(true)
            .Where(system => system.particleCount > 0).ToArray();

        private static NetworkEvent Entry(SimEventKind kind, ObservedEntityKind entityKind, int owner = -1)
        {
            var data = (SimEvent)Create(typeof(SimEvent), kind, owner, 44, 55, Point.Zero, 1f, 0, 0UL, 0, Point.Zero,
                false, 55, entityKind, ObservedEntityKind.Unit, ObservedEntityKind.Unit);
            return (NetworkEvent)Create(typeof(NetworkEvent), 1UL, 10L, data);
        }

        private static object Create(Type type, params object[] args) => Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.NonPublic, null, args, null);
    }
}
