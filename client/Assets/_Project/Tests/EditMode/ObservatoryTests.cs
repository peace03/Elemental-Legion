using System.Linq;
using ElementalLegion.Client.Input;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class ObservatoryTests
    {
        [Test]
        public void MinimapMarkersHaveRendererBeforeRectClipping()
        {
            var markers = new GameObject("Observed markers", typeof(RectTransform));
            try
            {
                markers.SetActive(false);
                var map = markers.AddComponent<ObservatoryMap>();
                Assert.That(markers.TryGetComponent<CanvasRenderer>(out _), Is.True);
                Assert.DoesNotThrow(() => map.SetClipRect(new Rect(0, 0, 214, 214), true));
            }
            finally
            {
                Object.DestroyImmediate(markers);
            }
        }

        [Test]
        public void SavedKeyboardControlsLoadAtomicallyAndRejectDuplicateBindings()
        {
            var controls = new ControlBindings();
            Assert.That(controls.Restore("I,K,J,L,UpArrow,DownArrow,LeftArrow,RightArrow,R,F1"), Is.True);
            Assert.That(controls[0], Is.EqualTo(Key.I));
            Assert.That(controls[ControlBindings.Rally], Is.EqualTo(Key.R));
            Assert.That(controls[ControlBindings.Menu], Is.EqualTo(Key.F1));
            Assert.That(controls[ControlBindings.Dashboard], Is.EqualTo(Key.Tab));
            Assert.That(controls.TrySet(ControlBindings.Menu, Key.R, out int conflict), Is.False);
            Assert.That(conflict, Is.EqualTo(ControlBindings.Rally));
            Assert.That(controls.TrySet(ControlBindings.Rally, Key.Enter, out _), Is.False);
            Assert.That(controls.Restore("W,S,A,D,UpArrow,DownArrow,LeftArrow,RightArrow,G,999"), Is.False);
            Assert.That(controls[0], Is.EqualTo(Key.I));
            Assert.That(controls[ControlBindings.Menu], Is.EqualTo(Key.F1));
            controls.Reset();
            Assert.That(controls[0], Is.EqualTo(Key.W));
            Assert.That(controls[ControlBindings.Menu], Is.EqualTo(Key.Escape));
        }

        [Test]
        public void LegacyTabBindingIsPreservedAndNewDashboardBindingCanBeRestored()
        {
            var controls = new ControlBindings();
            Assert.That(controls.Restore("W,S,A,D,UpArrow,DownArrow,LeftArrow,RightArrow,Tab,F1"), Is.True);
            Assert.That(controls[ControlBindings.Rally], Is.EqualTo(Key.Tab));
            Assert.That(controls[ControlBindings.Dashboard], Is.EqualTo(Key.F2));
            Assert.That(controls.Restore("W,S,A,D,UpArrow,DownArrow,LeftArrow,RightArrow,Tab,F1,H"), Is.True);
            Assert.That(controls[ControlBindings.Dashboard], Is.EqualTo(Key.H));
            Assert.That(controls.Restore("W,S,A,D,UpArrow,DownArrow,LeftArrow,RightArrow,Tab,F1,Tab"), Is.False);
            Assert.That(controls[ControlBindings.Dashboard], Is.EqualTo(Key.H));
            controls.Reset();
            Assert.That(controls[ControlBindings.Dashboard], Is.EqualTo(Key.Tab));
        }

        [Test]
        public void MovementPredictionIgnoresTheDomeAndStillStopsAtTheBase()
        {
            var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
            var rules = new GameRules(new GameDataDocument(data.text));
            var prediction = new MovementPrediction(rules);
            var center = new WorldPoint(90, 100);
            var origin = new WorldPoint(104, 100);
            var dome = new BuildingView(1, 2, BuildingKind.Dome, center, 12);
            var baseBuilding = new BuildingView(2, 2, BuildingKind.Base, center, 6);
            prediction.Reconcile(new LegionPose(1, 1, 0, origin, WorldPoint.Zero));
            prediction.SetBuildings(new[] { dome, baseBuilding });

            // 자기권 안으로 들어갈 수 있지만 더 안쪽의 본진 경계에서는 멈춘다.
            for (ulong sequence = 1; sequence <= 30; sequence++)
                Assert.That(prediction.Push(sequence, -WorldPoint.UnitX), Is.True);
            Assert.That(prediction.Position.X, Is.EqualTo(96).Within(0.01));
        }

        [Test]
        public void ObservatoryScenesArePresentInTheEnabledPlayerSceneList()
        {
            var paths = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            Assert.That(paths[0], Is.EqualTo("Assets/_Project/Scenes/Bootstrap.unity"));
            foreach (string name in new[] { "Bootstrap", "Title", "ServerBrowser", "Legion", "Settings", "PlayerDashboard" })
            {
                string path = $"Assets/_Project/Scenes/{name}.unity";
                Assert.That(paths, Does.Contain(path));
                Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(path), Is.Not.Null, name);
            }
        }
    }
}
