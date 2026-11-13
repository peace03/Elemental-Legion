using System.Linq;
using System.Reflection;
using ElementalLegion.Client.Camera;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Input;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class LegionCameraTests
    {
        [TestCase(1, 1, false)]
        [TestCase(1, 1, true)]
        [TestCase(1, 2, false)]
        [TestCase(2, 1, false)]
        [TestCase(4, 1, false)]
        [TestCase(8, 1, false)]
        [TestCase(7, 1, false)]
        public void GuardChordPreservesSelectionAndFocus(byte selectionMask, byte target, bool selectFromHud)
        {
            var fixture = new InputTestFixture();
            fixture.Setup();
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            var bootstrap = root.GetComponent<ClientBootstrap>();
            var input = root.GetComponent<LegionInput>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            using var connection = new ClientConnection(rules, new NetworkSettings(document));
            var connectionField = typeof(ClientBootstrap).GetField("connection", flags);
            try
            {
                typeof(ClientBootstrap).GetMethod("OnEnable", flags).Invoke(bootstrap, null);
                typeof(ClientConnection).GetProperty(nameof(ClientConnection.State)).SetValue(connection, ConnectionState.Connected);
                connectionField.SetValue(bootstrap, connection);
                var keyboard = InputSystem.AddDevice<Keyboard>();
                keyboard.MakeCurrent();
                input.Apply(new ControlBindings());
                input.ReadSquadShortcut(true);
                var selection = (SquadControlState)typeof(ClientBootstrap).GetField("squadControl", flags).GetValue(bootstrap);
                if (selectFromHud)
                {
                    var hud = root.GetComponentInChildren<CombatHud>(true);
                    var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
                    hud.BuildObservatory(new ObservatoryUi(font), rules, root.GetComponent<ElementalVisuals>());
                    var buttons = (Button[])typeof(CombatHud).GetField("squadButtons", flags).GetValue(hud);
                    buttons[0].onClick.Invoke();
                    Assert.That((ulong)typeof(ClientBootstrap).GetField("sequence", flags).GetValue(bootstrap), Is.EqualTo(1UL));
                }
                else selection.Select(selectionMask);
                var predictionField = typeof(ClientBootstrap).GetField("prediction", flags);
                var prediction = new MovementPrediction(rules);
                predictionField.SetValue(bootstrap, prediction);
                var camera = root.GetComponentInChildren<LegionCamera>(true);
                var center = new WorldPoint(100, 100);
                camera.ConfigureVision(rules);
                camera.SnapFollow(center, center);
                var destination = target == 1 ? Key.Digit1 : Key.Digit2;
                var apply = typeof(ClientBootstrap).GetMethod("ApplySquadShortcut", flags);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit4));
                InputSystem.Update();
                apply.Invoke(bootstrap, new object[] { input.ReadSquadShortcut(true) });
                Assert.That(input.PendingFromSquad, Is.EqualTo(4));
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Digit4, destination));
                InputSystem.Update();
                apply.Invoke(bootstrap, new object[] { input.ReadSquadShortcut(true) });
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.Update();
                apply.Invoke(bootstrap, new object[] { input.ReadSquadShortcut(true) });

                var writer = (NetDataWriter)typeof(ClientConnection).GetField("writer", flags).GetValue(connection);
                Assert.That(Protocol.TryReadCommand(new NetDataReader(writer.CopyData()), rules, out var command), Is.True);
                Assert.That(command.Kind, Is.EqualTo(CommandKind.QuickTransfer));
                Assert.That(command.FromSquad, Is.EqualTo(4));
                Assert.That(command.TargetSquad, Is.EqualTo(target));
                Assert.That(command.Sequence, Is.EqualTo(selectFromHud ? 2UL : 1UL));
                Assert.That(selection.SelectedMask, Is.EqualTo(selectionMask));
                Assert.That(predictionField.GetValue(bootstrap), Is.SameAs(prediction));
                AssertCenter(camera, center);
            }
            finally
            {
                connectionField.SetValue(bootstrap, null);
                var inputAsset = typeof(LegionInput).GetField("instance", flags);
                Object.DestroyImmediate((InputActionAsset)inputAsset.GetValue(input));
                inputAsset.SetValue(input, null);
                typeof(ClientBootstrap).GetMethod("OnDisable", flags).Invoke(bootstrap, null);
                Object.DestroyImmediate(root);
                fixture.TearDown();
            }
        }

        [Test]
        public void BaseFocusPreservesSelectionAndGuardCameraIgnoresItsMovingCenter()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            var bootstrap = root.GetComponent<ClientBootstrap>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            using var connection = new ClientConnection(rules, new NetworkSettings(document));
            try
            {
                typeof(ClientConnection).GetProperty(nameof(ClientConnection.State)).SetValue(connection, ConnectionState.Connected);
                typeof(GameplayFrame).GetProperty(nameof(GameplayFrame.BasePosition)).SetValue(connection.Gameplay.State, new WorldPoint(100, 100));
                typeof(ClientBootstrap).GetField("connection", flags).SetValue(bootstrap, connection);
                var selection = (SquadControlState)typeof(ClientBootstrap).GetField("squadControl", flags).GetValue(bootstrap);
                selection.Select(2);
                Assert.That(selection.NextDirect(true, out byte direct), Is.True);
                Assert.That(direct, Is.EqualTo(2));
                typeof(ClientBootstrap).GetMethod("FocusBase", flags).Invoke(bootstrap, null);
                Assert.That(selection.SelectedMask, Is.EqualTo(2));
                Assert.That(selection.NextDirect(false, out byte stopped), Is.True, "B는 진행 중인 직접 이동도 지우지 않습니다.");
                Assert.That(stopped, Is.EqualTo(2));
                var camera = root.GetComponentInChildren<LegionCamera>(true);
                camera.ConfigureVision(rules);
                AssertCenter(camera, new WorldPoint(100, 100));

                var shortcuts = new SquadShortcutState();
                var apply = typeof(ClientBootstrap).GetMethod("ApplySquadShortcut", flags);
                apply.Invoke(bootstrap, new object[] { shortcuts.Update(8, 8, 0, true) });
                Assert.That(selection.SelectedMask, Is.EqualTo(2));
                apply.Invoke(bootstrap, new object[] { shortcuts.Update(0, 0, 8, true) });
                Assert.That(selection.SelectedMask, Is.EqualTo(8));
                typeof(ClientBootstrap).GetMethod("FollowCamera", flags).Invoke(bootstrap, new object[] { new WorldPoint(120, 100) });
                typeof(LegionCamera).GetMethod("LateUpdate", flags).Invoke(camera, null);
                AssertCenter(camera, new WorldPoint(100, 100));
                Assert.That(selection.SelectedMask, Is.EqualTo(8));
            }
            finally
            {
                typeof(ClientBootstrap).GetField("connection", flags).SetValue(bootstrap, null);
                Object.DestroyImmediate(root);
            }
        }

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

                int moves = 0;
                WorldPoint? destination = null;
                bool held = false;
                hud.Map.MovePointerChanged += (point, down, pressed) =>
                {
                    destination = point;
                    held = down;
                    if (point.HasValue) moves++;
                };
                pointer.button = PointerEventData.InputButton.Right;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                Assert.That(moves, Is.EqualTo(1));
                Assert.That(held, Is.True);
                Assert.That(destination.Value.X, Is.EqualTo(uv.x * rules.MapSize).Within(.01f));
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerUpHandler);
                Assert.That(held, Is.False);
                int releasedMoves = moves;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.dragHandler);
                Assert.That(moves, Is.EqualTo(releasedMoves), "정상 해제 뒤 endDrag와 drag는 새 목적지를 만들지 않습니다.");

                pointer.button = PointerEventData.InputButton.Left;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                var dragUv = new Vector2(.2f, .8f);
                pointer.position = RectTransformUtility.WorldToScreenPoint(null,
                    mapRect.TransformPoint(Rect.NormalizedToPoint(mapRect.rect, dragUv)));
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.dragHandler);
                AssertCenter(camera, new WorldPoint(dragUv.x * rules.MapSize, dragUv.y * rules.MapSize));

                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.endDragHandler);
                pointer.position = RectTransformUtility.WorldToScreenPoint(null,
                    mapRect.TransformPoint(Rect.NormalizedToPoint(mapRect.rect, uv)));
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.dragHandler);
                AssertCenter(camera, new WorldPoint(dragUv.x * rules.MapSize, dragUv.y * rules.MapSize));

                pointer.button = PointerEventData.InputButton.Right;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerExitHandler);
                Assert.That(destination, Is.Null);
                Assert.That(held, Is.False);
                int before = moves;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.dragHandler);
                Assert.That(moves, Is.EqualTo(before));

                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                typeof(ObservatoryMap).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(hud.Map, new object[] { false });
                Assert.That(destination, Is.Null);
                Assert.That(held, Is.False);
                before = moves;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.dragHandler);
                Assert.That(moves, Is.EqualTo(before), "포커스를 잃은 홀드는 새 누름 없이 재개하지 않습니다.");

                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                hud.Map.gameObject.SetActive(false);
                Assert.That(destination, Is.Null);
                Assert.That(held, Is.False);
                hud.Map.gameObject.SetActive(true);
                before = moves;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.dragHandler);
                Assert.That(moves, Is.EqualTo(before), "다시 활성화해도 이전 홀드를 재개하지 않습니다.");

                pointer.button = PointerEventData.InputButton.Left;
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.pointerDownHandler);
                typeof(ObservatoryMap).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(hud.Map, new object[] { false });
                pointer.position = RectTransformUtility.WorldToScreenPoint(null,
                    mapRect.TransformPoint(Rect.NormalizedToPoint(mapRect.rect, dragUv)));
                ExecuteEvents.Execute(hud.Map.gameObject, pointer, ExecuteEvents.dragHandler);
                AssertCenter(camera, new WorldPoint(uv.x * rules.MapSize, uv.y * rules.MapSize));
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
