using System.Linq;
using System.Reflection;
using ElementalLegion.Client.Camera;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class LegionCameraTests
    {
        [Test]
        public void WheelZoomStopsAtBothLimitsAndRespectsAWideViewport()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);
            var root = Object.Instantiate(prefab);
            try
            {
                var camera = root.GetComponentInChildren<LegionCamera>(true);
                var settings = new SerializedObject(camera);
                settings.FindProperty("viewSize").floatValue = 30;
                settings.FindProperty("minimumViewSize").floatValue = 10;
                settings.FindProperty("zoomStep").floatValue = .2f;
                settings.ApplyModifiedPropertiesWithoutUndo();
                var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
                var rules = new GameRules(new GameDataDocument(data.text));
                camera.ConfigureVision(rules);
                camera.View.aspect = 16f / 9;
                var position = new System.Numerics.Vector2(100, 100);
                camera.Follow(position, position);

                camera.ZoomByWheel(-1);
                Assert.That(camera.View.orthographicSize, Is.EqualTo(30).Within(.001f));
                camera.ZoomByWheel(1);
                Assert.That(camera.View.orthographicSize, Is.EqualTo(25).Within(.001f));
                for (int step = 0; step < 30; step++) camera.ZoomByWheel(1);
                Assert.That(camera.View.orthographicSize, Is.EqualTo(10).Within(.001f));

                camera.View.aspect = 4;
                for (int step = 0; step < 30; step++) camera.ZoomByWheel(-1);
                float visibleLimit = (rules.CameraVisionHalfSize.X - rules.CameraVisionPadding) / 4;
                Assert.That(camera.View.orthographicSize, Is.EqualTo(visibleLimit).Within(.001f));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void MinimapClickPinsTheViewAndCenterButtonReturnsToTheMovingLegion()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);
            var root = Object.Instantiate(prefab);
            try
            {
                var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
                var document = new GameDataDocument(data.text);
                var rules = new GameRules(document).ForRoom(2);
                var camera = root.GetComponentInChildren<LegionCamera>(true);
                var hud = root.GetComponentInChildren<CombatHud>(true);
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
                hud.Configure(document, rules);
                hud.BuildObservatory(new ObservatoryUi(font), rules, root.GetComponent<ElementalVisuals>());
                hud.MapFocusRequested += camera.Inspect;
                hud.CameraCenterRequested += camera.CenterOnLegion;
                ((GameObject)new SerializedObject(hud).FindProperty("content").objectReferenceValue).SetActive(true);
                var origin = new WorldPoint(60, 50);
                hud.Map.ObservePose(origin);
                camera.ConfigureVision(rules);
                camera.View.aspect = 16f / 9;
                camera.Follow(origin, origin);
                camera.ZoomByWheel(1);
                float zoom = camera.View.orthographicSize;
                Canvas.ForceUpdateCanvases();

                RectTransform mapRect = hud.Map.rectTransform;
                var uv = new Vector2(.72f, .31f);
                var pointer = new PointerEventData(null)
                {
                    position = RectTransformUtility.WorldToScreenPoint(null,
                        mapRect.TransformPoint(Rect.NormalizedToPoint(mapRect.rect, uv))),
                    button = PointerEventData.InputButton.Right
                };
                Assert.That(hud.Map.raycastTarget && hud.Map.Raycast(pointer.position, null), Is.True);
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                AssertCenter(camera, origin);

                pointer.button = PointerEventData.InputButton.Left;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                var moved = new WorldPoint(64, 56);
                camera.Follow(moved, moved);
                typeof(LegionCamera).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(camera, null);
                AssertCenter(camera, new WorldPoint(uv.x * rules.MapSize, uv.y * rules.MapSize));

                var center = hud.GetComponentsInChildren<Button>(true).Single(button => button.name == "군단 중심");
                center.onClick.Invoke();
                AssertCenter(camera, moved);
                Assert.That(camera.View.orthographicSize, Is.EqualTo(zoom).Within(.001f));
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void AssertCenter(LegionCamera camera, WorldPoint expected)
        {
            Ray ray = camera.View.ViewportPointToRay(new Vector3(.5f, .5f));
            Assert.That(new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance), Is.True);
            Vector3 ground = ray.GetPoint(distance);
            Assert.That(ground.x, Is.EqualTo(expected.X).Within(.02f));
            Assert.That(ground.z, Is.EqualTo(expected.Y).Within(.02f));
        }
    }
}
