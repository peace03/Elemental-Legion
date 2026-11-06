using System;
using System.Linq;
using System.Reflection;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    public sealed class LobbyElementTooltipTests
    {
        [Test]
        public void StartingChoicesShowTheirRolesWhileReadyAndHideOnExit()
        {
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            string[] names = rules.Elements.Select((_, index) => (string)document.Root["elements"][index]["displayName"]).ToArray();
            string[] starting = rules.StartingElements.Select(index => names[index]).ToArray();
            Assembly client = typeof(ClientFlow).Assembly;
            Type screenType = client.GetType("ElementalLegion.Client.UI.ClientScreen", true);
            Type skinType = client.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            TMP_FontAsset font = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>(
                "Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf"), 40, 5,
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            var root = new GameObject("Starting element tooltip test");
            try
            {
                Component screen = root.AddComponent(screenType);
                object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { font }, null);
                Call(screen, "Initialize", skin, 0, true);
                int selections = 0;
                Call(screen, "BuildLobby", starting, rules, 0, rules.MaxPlayers, (Action<int, bool>)((_, __) => selections++),
                    (Action<int>)(_ => { }), (Action<int, AiDifficulty>)((_, __) => { }), (Action)(() => { }), (Action)(() => { }), (Action<int>)(_ => { }));
                var writer = new NetDataWriter();
                var members = new[] { new LobbyMember("나", rules.StartingElements[0], true, 2),
                    new LobbyMember("다른 사람", rules.StartingElements[1], false, 3) };
                M5Protocol.WriteLobbyState(writer, "원소 정보", 0, 2, rules.MaxPlayers, members, 0, Array.Empty<AiDifficulty>(), Array.Empty<int>());
                Assert.That(M5Protocol.TryReadLobbyState(new NetDataReader(writer.CopyData()), rules, out LobbyState lobby), Is.True);
                Call(screen, "SetLobby", lobby, names, rules.StartingElements, null, false);

                var tooltip = root.GetComponentsInChildren<RectTransform>(true).Single(rect => rect.name == "Starting element tooltip");
                Assert.That(tooltip.gameObject.activeSelf, Is.False);
                TMP_Text title = tooltip.Find("Element name").GetComponent<TMP_Text>();
                TMP_Text detail = tooltip.Find("Element role").GetComponent<TMP_Text>();
                var choices = root.GetComponentsInChildren<UnityEngine.UI.Button>();
                var pointer = new PointerEventData(null);
                for (int index = 0; index < starting.Length; index++)
                {
                    var choice = choices.Single(button => button.name == starting[index]);
                    Assert.That(choice.interactable, Is.False);
                    Assert.That(choice.targetGraphic.raycastTarget, Is.True);
                    Assert.That(ExecuteEvents.Execute(choice.gameObject, pointer, ExecuteEvents.pointerEnterHandler), Is.True);
                    Assert.That(tooltip.gameObject.activeSelf, Is.True);
                    Assert.That(title.text, Is.EqualTo(starting[index]));
                    string role = rules.Elements[rules.StartingElements[index]].Role switch
                    {
                        UnitRole.Melee => "근접", UnitRole.Mid => "중거리", UnitRole.Ranged => "원거리",
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    Assert.That(detail.text, Does.EndWith(role));
                    Rect area = ((RectTransform)tooltip.parent).rect;
                    Vector2 position = tooltip.anchoredPosition;
                    Assert.That(position.x, Is.InRange(0, area.width - tooltip.rect.width));
                    Assert.That(position.y, Is.InRange(tooltip.rect.height - area.height, 0));
                    Assert.That(tooltip.GetComponentsInChildren<UnityEngine.UI.Graphic>().All(graphic => !graphic.raycastTarget), Is.True);
                    ExecuteEvents.Execute(choice.gameObject, pointer, ExecuteEvents.pointerExitHandler);
                    Assert.That(tooltip.gameObject.activeSelf, Is.False);
                }
                Assert.That(selections, Is.Zero);

                var first = choices.Single(button => button.name == starting[0]);
                ExecuteEvents.Execute(first.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
                Call(screen, "SetInteractive", false);
                Assert.That(tooltip.gameObject.activeSelf, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(font);
            }
        }

        private static void Call(Component screen, string name, params object[] args) =>
            screen.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(screen, args);
    }
}
