using System.Linq;
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
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class FacilityEffectsTests
    {
        [Test]
        public void RuntimePrefabReferencesFacilityEffectsAndAllThreeModelRings()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);
            var serialized = new SerializedObject(prefab.GetComponent<ElementalVisuals>());
            foreach (var field in new[] { "nucleusGem", "nucleusGemMaterial", "cohesionCurrents", "cohesionCurrentsMaterial", "collectorIntakeMaterial" })
            {
                Object asset = serialized.FindProperty(field).objectReferenceValue;
                Assert.That(asset, Is.Not.Null, field);
                if (asset is Material material) Assert.That(ShaderUtil.ShaderHasError(material.shader), Is.False, field);
            }
            var groundGlow = (Material)serialized.FindProperty("facilityGroundGlow").objectReferenceValue;
            Assert.That(groundGlow, Is.Not.Null);
            Assert.That(groundGlow.enableInstancing, Is.True);
            Assert.That(ShaderUtil.ShaderHasError(groundGlow.shader), Is.False);
            var catalog = (FacilityModelCatalog)serialized.FindProperty("facilityModels").objectReferenceValue;
            Assert.That(catalog.TryGet(BuildingKind.EnergyForge, out var model), Is.True);
            CollectionAssert.AreEquivalent(new[]
            {
                FacilityModelCatalog.PartMotion.IgnitionCrystal, FacilityModelCatalog.PartMotion.IgnitionRingLower,
                FacilityModelCatalog.PartMotion.IgnitionRingMiddle, FacilityModelCatalog.PartMotion.IgnitionRingUpper
            }, model.Parts.Where(part => part.Motion != FacilityModelCatalog.PartMotion.None).Select(part => part.Motion));
        }

        [TestCase(BuildingKind.Base)]
        [TestCase(BuildingKind.Collector)]
        [TestCase(BuildingKind.NatureForge)]
        [TestCase(BuildingKind.EnergyForge)]
        [TestCase(BuildingKind.NatureTower)]
        public void FacilityLightUsesLinearRadianceAndStopsForInactiveStates(BuildingKind kind)
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                var render = typeof(ElementalVisuals).GetMethod("RenderBuilding", hidden);
                var properties = (MaterialPropertyBlock[][])typeof(ElementalVisuals).GetField("facilityProperties", hidden).GetValue(visuals);
                var neutral = properties[PlayerPalette.Count];
                Draw(100, 0, 0);
                Assert.That(neutral[0].GetVector("_FacilityLightColor").sqrMagnitude, Is.GreaterThan(0));
                Assert.That(neutral[0].GetVector("_FacilityLightSource").w, Is.GreaterThan(0));
                if (kind == BuildingKind.Collector)
                {
                    var settings = new SerializedObject(visuals);
                    Color radiance = settings.FindProperty("collectorArrivalGlow").colorValue;
                    Assert.That(neutral[0].GetVector("_FacilityEmission").x, Is.EqualTo(radiance.r * .07f).Within(.0001f));
                }

                Draw(100, 100, 0);
                Draw(100, 0, 100);
                Draw(0, 0, 0);
                foreach (var state in neutral)
                {
                    Assert.That(state.GetVector("_FacilityLightColor"), Is.EqualTo(Vector4.zero));
                    Assert.That(state.GetVector("_FacilityLightSource"), Is.EqualTo(Vector4.zero));
                    Assert.That(state.GetVector("_FacilityEmission"), Is.EqualTo(Vector4.zero));
                    Assert.That(state.GetFloat("_FacilityInlayGlow"), Is.Zero);
                    Assert.That(state.GetFloat("_FacilityCrystal"), Is.Zero);
                }

                void Draw(float health, long ghostEnd, long buildEnd)
                {
                    var building = new BuildingView(501, 1, kind, new Point(40, 40), 3, health, 100, 0, ghostEnd, buildEnd);
                    render.Invoke(visuals, new object[] { building, 1, 0f });
                }
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ServerConfirmedCoreFlowsIntoItsCollectorAndStopsWhenTheFacilityIsForgotten()
        {
            var data = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var json = data.Root.DeepClone();
            json["buildings"]["collector"]["buildSeconds"] = 0;
            var rules = new GameRules(new GameDataDocument(json.ToString()));
            var origin = new Point(124, 100);
            var setup = new WorldSetup();
            setup.Players.Add(new PlayerSpawn(1, "water", new Point(143, 100), new Point(143, 100), cores: 100));
            setup.Cores.Add(new CoreSpawn(origin, 1));
            var world = new SimulationWorld(rules, setup, 1);
            Assert.That(world.TryEnqueue(1, SimCommand.Build(1, BuildingKind.Collector, 128, 100)), Is.EqualTo(CommandAdmission.Accepted));
            world.Step();

            var view = world.GetView(1);
            var sender = new GameplayReplicationState(rules);
            using var replica = new GameplayReplica(rules);
            replica.BindPlayer(1);
            var writer = new NetDataWriter();
            var fog = new FogStateReplica(rules);
            var fogSender = new FogReplicationState(rules);
            fogSender.Prepare(view);
            for (int index = 0; index < fogSender.ChunkCount; index++)
            {
                fogSender.WriteChunk(writer, index);
                fog.TryAccept(new NetDataReader(writer.CopyData()));
            }
            sender.Capture(view);
            sender.Prepare(view, fogSender.Revision);
            for (int index = 0; index < sender.ChunkCount; index++)
            {
                sender.WriteChunk(writer, index);
                replica.TryAccept(new NetDataReader(writer.CopyData()), 0);
            }
            Assert.That(replica.TryPublish(fog, out var frame), Is.True);
            Assert.That(frame.SpatialReady, Is.True);
            var collector = frame.Buildings.ToArray().Single(building => building.Kind == BuildingKind.Collector);
            var pickup = frame.Events.ToArray().Single(entry => entry.Data.Kind == SimEventKind.CorePickedUp);
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                visuals.Observe(frame);
                var particles = root.GetComponentsInChildren<ParticleSystem>(true).Single(system => system.name == "Collector Intake Particles");
                var render = typeof(ElementalVisuals).GetMethod("RenderCollectorIntakes", BindingFlags.NonPublic | BindingFlags.Instance);
                var buffer = new ParticleSystem.Particle[64];
                visuals.Play(pickup);
                float time = (float)Time.realtimeSinceStartupAsDouble;
                render.Invoke(visuals, new object[] { time + .25f });
                float nearOrigin = AverageDistanceToCollector();
                render.Invoke(visuals, new object[] { time + .67f });
                Assert.That(AverageDistanceToCollector(), Is.LessThan(nearOrigin - 1f));
                visuals.Forget(new ObservedEntityId(ObservedEntityKind.Building, collector.Id));
                Assert.That(particles.particleCount, Is.Zero);
                Assert.That(visuals.ActiveParticles, Is.Zero);
                visuals.Play(pickup);
                render.Invoke(visuals, new object[] { time + .4f });
                Assert.That(particles.particleCount, Is.Zero);

                float AverageDistanceToCollector()
                {
                    int count = particles.GetParticles(buffer);
                    Assert.That(count, Is.GreaterThan(0));
                    return buffer.Take(count).Average(particle =>
                        Vector2.Distance(new Vector2(particle.position.x, particle.position.z), new Vector2(collector.Position.X, collector.Position.Y)));
                }
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
