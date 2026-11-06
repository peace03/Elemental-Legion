using System.Reflection;
using ElementalLegion.Client.Bootstrap;
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
    public sealed class MigrationPresentationTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(MigrationPhase.Recovering)]
        [TestCase(MigrationPhase.Resyncing)]
        public void MigrationKeepsOnlyTheLastObservedUnitsOnScreen(MigrationPhase phase)
        {
            var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
            var document = new GameDataDocument(data.text);
            var rules = new GameRules(document);
            var settings = new NetworkSettings(document);
            var setup = new WorldSetup();
            var center = new Point(840, 840);
            setup.Players.Add(new PlayerSpawn(1, "water", center - new Point(30, 0), center, 3));
            setup.Players.Add(new PlayerSpawn(2, "fire", center + new Point(30, 0), center + new Point(4, 0), 3));
            setup.Units.Add(new UnitSpawn(1, "water", center - new Point(4, 0)));
            setup.Units.Add(new UnitSpawn(2, "earth", center + new Point(4, 0)));
            var world = new SimulationWorld(rules, setup, 1);
            WorldView view = world.GetView(1);
            var writer = new NetDataWriter();
            Protocol.WriteUnits(writer, view, rules.MapSize, 0);
            var units = new UnitSnapshotAssembler(rules);
            Assert.That(units.TryAccept(new NetDataReader(writer.CopyData())), Is.True);
            var timeline = new SnapshotTimeline(4);
            timeline.Push(units);
            var enemy = view.Units[1];
            Assert.That(enemy.OwnerId, Is.EqualTo(2));
            Protocol.WritePose(writer, view);
            Assert.That(Protocol.TryReadPose(new NetDataReader(writer.CopyData()), rules, out var pose), Is.True);
            var prediction = new MovementPrediction(rules);
            prediction.Reconcile(pose);

            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            var bootstrap = root.GetComponent<ClientBootstrap>();
            using var connection = new ClientConnection(rules, settings);
            try
            {
                typeof(ClientConnection).GetProperty(nameof(ClientConnection.State)).SetValue(connection, ConnectionState.Connected);
                typeof(ClientConnection).GetProperty(nameof(ClientConnection.PlayerId)).SetValue(connection, 1);
                typeof(ClientConnection).GetMethod("AcceptMigration", PrivateInstance)
                    .Invoke(connection, new object[] { new HostMigrationNotice(phase, 1, view.Tick, false, "fixture") });
                Set(bootstrap, "ready", true);
                Set(bootstrap, "displayedState", ConnectionState.Connected);
                Set(bootstrap, "inputBlocked", true);
                Set(bootstrap, "connection", connection);
                Set(bootstrap, "prediction", prediction);
                Set(bootstrap, "timeline", timeline);
                var presentation = root.GetComponent<LegionPresentation>();
                Set(presentation, "showGrid", true);
                presentation.ValidateReferences();
                var markers = (ParticleSystem)new SerializedObject(presentation).FindProperty("markers").objectReferenceValue;
                MethodInfo update = typeof(ClientBootstrap).GetMethod("Update", PrivateInstance);

                update.Invoke(bootstrap, null);
                Assert.That(markers.particleCount, Is.EqualTo(3));
                timeline.Forget(enemy.Id, view.Tick);
                update.Invoke(bootstrap, null);
                Assert.That(markers.particleCount, Is.EqualTo(2));
                Assert.That(connection.SentPayloadBytes, Is.Zero);
            }
            finally
            {
                Set(bootstrap, "connection", null);
                Object.DestroyImmediate(root);
            }
        }

        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, PrivateInstance).SetValue(target, value);
    }
}
