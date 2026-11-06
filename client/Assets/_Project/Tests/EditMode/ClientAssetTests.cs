using System;
using System.Linq;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.Editor;
using ElementalLegion.Shared.Data;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace ElementalLegion.Client.Tests
{
    /// <summary>실제 가져온 공유 자산·입력·프리팹 참조와 빌드 진입점을 검사한다. Play 검증을 대신하지 않는다.</summary>
    public sealed class ClientAssetTests
    {
        [Test]
        public void SharedDataAndMovedInputAreImportedWithOriginalActionIdentity()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
            Assert.That(asset, Is.Not.Null);
            Assert.DoesNotThrow(() => new GameRules(new GameDataDocument(asset.text)));
            string inputPath = AssetDatabase.GUIDToAssetPath("052faaac586de48259a63d0c4782560b");
            Assert.That(inputPath, Is.EqualTo("Assets/_Project/Settings/Input/InputSystem_Actions.inputactions"));
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(inputPath);
            Assert.That(actions.FindAction("Player/Move"), Is.Not.Null); Assert.That(actions.FindAction("UI/Click"), Is.Not.Null);
            Assert.That(typeof(ClientBootstrap).Assembly.GetReferencedAssemblies().Any(name => name.Name.StartsWith("UnityEditor", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public void ClientPrefabHasPersistentRequiredReferencesAndNoMissingScripts()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);
            Assert.That(prefab, Is.Not.Null);
            foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject), Is.Zero, transform.name);
            var serialized = new SerializedObject(prefab.GetComponent<ClientBootstrap>());
            foreach (string field in new[] { "gameData", "input", "presentation", "legionCamera", "panel", "fog" })
                Assert.That(serialized.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
            var module = prefab.GetComponentInChildren<InputSystemUIInputModule>(true);
            Assert.That(module.point, Is.Not.Null); Assert.That(module.point.action, Is.Not.Null); Assert.That(AssetDatabase.Contains(module.point), Is.True);
            Assert.That(module.leftClick.action, Is.Not.Null); Assert.That(module.move.action, Is.Not.Null);
            Assert.That(module.submit.action, Is.Not.Null); Assert.That(module.cancel.action, Is.Not.Null);
            Assert.That(module.point.action.actionMap.name, Is.EqualTo("UI"));
        }

        [Test]
        public void BootstrapIsBuildEntryAndLegionUiFontCoversDataNames()
        {
            const string bootstrapPath = "Assets/_Project/Scenes/Bootstrap.unity";
            Assert.That(EditorBuildSettings.scenes, Is.Not.Empty);
            Assert.That(EditorBuildSettings.scenes[0].path, Is.EqualTo(bootstrapPath));
            Assert.That(EditorBuildSettings.scenes[0].enabled, Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(bootstrapPath), Is.Not.Null);
            Assert.That(EditorBuildSettings.scenes.Any(scene => scene.path == ClientSceneBuilder.ScenePath && scene.enabled), Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(ClientSceneBuilder.ScenePath), Is.Not.Null);
            var data = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(data);
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
            Assert.That(font, Is.Not.Null);
            foreach (int element in rules.StartingElements)
                Assert.That(font.HasCharacters((string)data.Root["elements"][element]["displayName"]), Is.True);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);
            foreach (var label in prefab.GetComponentsInChildren<TMP_Text>(true)) Assert.That(font.HasCharacters(label.text), Is.True, label.text);
        }
    }
}
