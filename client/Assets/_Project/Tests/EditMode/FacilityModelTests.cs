using System;
using System.Collections;
using System.Reflection;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ElementalLegion.Client.Tests
{
    /// <summary>실제 제공 메시와 지면·반경, 실행 프리팹의 시설 연결을 확인한다.</summary>
    public sealed class FacilityModelTests
    {
        [Test]
        public void ImportedFacilitiesUseFiveModelsWithGroundedBoundsAndOriginalTextures()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FacilityModelCatalog>(FacilityModelCatalog.AssetPath);
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.Count, Is.EqualTo(5));
            foreach (BuildingKind kind in new[] { BuildingKind.Base, BuildingKind.NatureForge, BuildingKind.EnergyForge, BuildingKind.Collector, BuildingKind.NatureTower })
            {
                Assert.That(catalog.TryGet(kind, out var model), Is.True, kind.ToString());
                Assert.That(model.Parts, Is.Not.Empty);
                Assert.That(model.HorizontalRadius, Is.GreaterThan(0));
                float minY = float.MaxValue, maxY = float.MinValue, radius = 0;
                foreach (var part in model.Parts)
                {
                    Assert.That(part.Mesh, Is.Not.Null);
                    Assert.That(part.Submesh, Is.InRange(0, part.Mesh.subMeshCount - 1));
                    Assert.That(part.Material.shader.name, Is.EqualTo("ElementalLegion/Facility"));
                    Assert.That(ShaderUtil.ShaderHasError(part.Material.shader), Is.False);
                    Assert.That(part.Material.FindPass("DepthNormals"), Is.GreaterThanOrEqualTo(0));
                    Assert.That(part.Material.FindPass("ShadowCaster"), Is.GreaterThanOrEqualTo(0));
                    Assert.That(part.Material.GetTexture("_BaseMap"), Is.Not.Null);
                    Assert.That(part.Material.GetTexture("_NormalMap"), Is.Not.Null);
                    Assert.That(AssetDatabase.GetAssetPath(part.Mesh), Does.StartWith("Assets/_Project/Art/Facilities/"));
                    foreach (Vector3 vertex in part.Mesh.vertices)
                    {
                        Vector3 point = part.LocalMatrix.MultiplyPoint3x4(vertex);
                        minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
                        radius = Mathf.Max(radius, new Vector2(point.x, point.z).magnitude);
                    }
                }
                Assert.That(minY, Is.EqualTo(0).Within(.0001f), kind.ToString());
                Assert.That(maxY, Is.EqualTo(model.Height).Within(.0001f), kind.ToString());
                Assert.That(radius, Is.EqualTo(model.HorizontalRadius).Within(.0001f), kind.ToString());
            }
            catalog.TryGet(BuildingKind.NatureTower, out var nature);
            catalog.TryGet(BuildingKind.EnergyTower, out var energy);
            Assert.That(energy, Is.SameAs(nature));
            Assert.That(catalog.TryGet(BuildingKind.Dome, out _), Is.False);
        }

        [Test]
        public void RuntimePrefabUsesSuppliedCatalogAndBattlefield()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);
            var visuals = prefab.GetComponent<ElementalVisuals>();
            var catalog = new SerializedObject(visuals).FindProperty("facilityModels").objectReferenceValue;
            Assert.That(catalog, Is.SameAs(AssetDatabase.LoadAssetAtPath<FacilityModelCatalog>(FacilityModelCatalog.AssetPath)));
            var battlefield = prefab.GetComponent<BattlefieldPresentation>();
            Assert.That(battlefield, Is.Not.Null);
            Assert.That(battlefield.GroundMaterial, Is.Not.Null);
            Assert.That(new SerializedObject(prefab.GetComponent<LegionPresentation>()).FindProperty("battlefield").objectReferenceValue, Is.SameAs(battlefield));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TowerProjectileStartsAtTheDisplayedModelHeight(bool useImportedModel)
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                if (!useImportedModel)
                {
                    var serialized = new SerializedObject(visuals);
                    serialized.FindProperty("facilityModels").objectReferenceValue = null;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                visuals.Initialize();
                var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                var frame = (GameplayFrame)Activator.CreateInstance(typeof(GameplayFrame), hidden, null, new object[] { rules }, null);
                var buffer = (BuildingView[])typeof(GameplayFrame).GetField("BuildingBuffer", hidden).GetValue(frame);
                buffer[0] = new BuildingView(73, 1, BuildingKind.NatureTower, new System.Numerics.Vector2(100), 1.5f);
                typeof(GameplayFrame).GetField("BuildingCount", hidden).SetValue(frame, 1);
                typeof(GameplayFrame).GetProperty("SpatialReady").SetValue(frame, true);
                visuals.Observe(frame);
                var source = new System.Numerics.Vector2(100);
                var target = new System.Numerics.Vector2(103, 100);
                var data = (SimEvent)Activator.CreateInstance(typeof(SimEvent), hidden, null,
                    new object[] { SimEventKind.Attack, 1, 73, 90, target, 1f, 0, 0UL, rules.ElementIndex("lightning"), source, true, 90, ObservedEntityKind.Unit, ObservedEntityKind.Building, ObservedEntityKind.Unit }, null);
                var entry = (NetworkEvent)Activator.CreateInstance(typeof(NetworkEvent), hidden, null, new object[] { 1UL, 1L, data }, null);
                visuals.Play(entry);
                float expected;
                if (useImportedModel)
                {
                    var catalog = AssetDatabase.LoadAssetAtPath<FacilityModelCatalog>(FacilityModelCatalog.AssetPath);
                    catalog.TryGet(BuildingKind.NatureTower, out var model);
                    expected = model.Height * 1.5f / model.HorizontalRadius + .35f;
                }
                else
                {
                    float height = new SerializedObject(visuals).FindProperty("towerHeight").floatValue;
                    expected = height * .725f + .08f;
                }
                bool found = false;
                foreach (object effect in (IEnumerable)typeof(ElementalVisuals).GetField("pool", hidden).GetValue(visuals))
                {
                    Type type = effect.GetType();
                    if (!(bool)type.GetField("Projectile").GetValue(effect)) continue;
                    Assert.That(((Vector3)type.GetField("From").GetValue(effect)).y, Is.EqualTo(expected).Within(.001f));
                    found = true;
                }
                Assert.That(found, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
