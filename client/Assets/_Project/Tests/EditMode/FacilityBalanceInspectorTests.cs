using System;
using System.IO;
using ElementalLegion.Client.Editor;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ElementalLegion.Client.Tests
{
    public sealed class FacilityBalanceInspectorTests
    {
        private FacilityBalanceInspector editorModel;
        private string path;
        private string original;

        [SetUp]
        public void CopyData()
        {
            path = Path.Combine(Path.GetTempPath(), $"elemental-balance-{Guid.NewGuid():N}.json");
            original = File.ReadAllText(FacilityBalanceInspector.SourcePath());
            File.WriteAllText(path, original);
            editorModel = ScriptableObject.CreateInstance<FacilityBalanceInspector>();
            editorModel.Reload(path);
        }

        [TearDown]
        public void RemoveCopy()
        {
            UnityEngine.Object.DestroyImmediate(editorModel);
            File.Delete(path);
        }

        [Test]
        public void SaveReplacesOnlyEditedNumbersAndKeepsRuntimeRulesInSync()
        {
            Edit("baseLevels[8].populationCap", 301);
            Edit("facilityUpgrades.collector.a[3].cost", 37);
            editorModel.Save(path);
            string saved = File.ReadAllText(path);
            Assert.That(saved, Is.EqualTo(original.Replace("\"populationCap\": 300,", "\"populationCap\": 301,")
                .Replace("\"max\": 300", "\"max\": 301").Replace("\"cost\": 36, \"effect\": 2", "\"cost\": 37, \"effect\": 2")));
            editorModel.Reload(path);
            var rules = new GameRules(new GameDataDocument(saved));
            Assert.That(editorModel.SourceHash, Is.EqualTo(rules.DataHash));
            Assert.That(rules.MaxPopulation, Is.EqualTo(301));
            Assert.That(rules.TryGetFacilityUpgrade(BuildingKind.Collector, FacilityUpgradeBranch.A, 4, out FacilityUpgrade upgrade), Is.True);
            Assert.That(upgrade.Cost, Is.EqualTo(37));
        }

        [Test]
        public void SpawnEditsKeepTheTwoSupplyBudgetsSeparate()
        {
            Edit("neutral.respawnSeconds", 180);
            Edit("economy.opening.bundleSize", 3);
            Edit("economy.opening.nearbyBundlesPerMinute", 2);
            Edit("economy.opening.sharedBundlesPerMinute", 1);
            Edit("economy.opening.explorationBundlesPerMinute", 7);
            Edit("economy.opening.explorationCoreLifetimeSeconds", 100);

            editorModel.Save(path);
            editorModel.Reload(path);
            var document = new GameDataDocument(File.ReadAllText(path));
            var rules = new GameRules(document);
            Assert.That(rules.NeutralRespawnSeconds, Is.EqualTo(180));
            Assert.That(rules.OpeningCoreBundleSize, Is.EqualTo(3));
            Assert.That(rules.OpeningNearbyBundlesPerMinute, Is.EqualTo(2));
            Assert.That(rules.OpeningSharedBundlesPerMinute, Is.EqualTo(1));
            Assert.That(rules.MapCoreSpawnPerPlayerPerMinute, Is.EqualTo(9));
            Assert.That((int)document.Root.SelectToken("economy.opening.explorationBundlesPerMinute"), Is.EqualTo(7));
            Assert.That((int)document.Root.SelectToken("economy.opening.explorationCoreLifetimeSeconds"), Is.EqualTo(100));
            Assert.That(editorModel.SourceHash, Is.EqualTo(rules.DataHash));
        }

        [TestCase("buildings.collector.production.intervalSeconds", 0)]
        [TestCase("facilityUpgrades.tower.a[0].cost", -1)]
        [TestCase("neutral.walkSpacingSeconds", 300)]
        [TestCase("world.mapSize", 1400)]
        [TestCase("economy.opening.bundleSize", int.MaxValue)]
        [TestCase("economy.opening.nearbyBundlesPerMinute", -1)]
        public void InvalidEditLeavesSourceIntact(string field, int value)
        {
            Edit(field, value);
            Assert.Throws<ArgumentException>(() => editorModel.Save(path));
            Assert.That(File.ReadAllText(path), Is.EqualTo(original));
        }

        [Test]
        public void ExternalEditMustBeReloadedBeforeSaving()
        {
            Edit("facilityUpgrades.collector.a[3].cost", 37);
            string externallyEdited = original + " ";
            File.WriteAllText(path, externallyEdited);
            Assert.Throws<InvalidOperationException>(() => editorModel.Save(path));
            Assert.That(File.ReadAllText(path), Is.EqualTo(externallyEdited));
        }

        private void Edit(string field, int value)
        {
            JObject draft = JObject.Parse(editorModel.DraftJson);
            draft.SelectToken(field).Replace(new JValue(value));
            editorModel.DraftJson = draft.ToString(Formatting.None);
        }
    }
}
