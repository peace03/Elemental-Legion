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
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>대기실 색상 선택의 서버 확인·잠금·AI 색 양보 표시를 검증합니다.</summary>
    public sealed class PlayerColorUiTests
    {
        [Test]
        public void LobbyShowsConfirmedColorAndLocksHumansButLeavesAiColorsSelectable()
        {
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            string[] names = rules.Elements.Select((_, i) => (string)document.Root["elements"][i]["displayName"]).ToArray();
            string[] starting = rules.StartingElements.Select(i => names[i]).ToArray();
            Assembly assembly = typeof(ClientFlow).Assembly;
            Type skinType = assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            Type screenType = assembly.GetType("ElementalLegion.Client.UI.ClientScreen", true);
            TMP_FontAsset font = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>(
                "Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf"), 40, 5,
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { font }, null);
            var root = new GameObject("Owner color lobby test");
            root.SetActive(false);
            try
            {
                Component screen = root.AddComponent(screenType);
                Call(screen, "Initialize", skin, 0, true);
                int requested = -1, requests = 0;
                Call(screen, "BuildLobby", starting, rules, 0, rules.MaxPlayers, (Action<int, bool>)((_, __) => { }),
                    (Action<int>)(_ => { }), (Action<int, AiDifficulty>)((_, __) => { }), (Action)(() => { }),
                    (Action)(() => { }), (Action<int>)(id => { requested = id; requests++; }));
                void State(bool ready, bool recovering, int ownColor = 14)
                {
                    var members = new[] { new LobbyMember("나", rules.StartingElements[0], ready, ownColor),
                        new LobbyMember("다른 사람", rules.StartingElements[1], false, 2) };
                    var writer = new NetDataWriter();
                    M5Protocol.WriteLobbyState(writer, "색 선택", 0, 2, rules.MaxPlayers, members, 1,
                        new[] { AiDifficulty.Normal }, new[] { 0 });
                    Assert.That(M5Protocol.TryReadLobbyState(new NetDataReader(writer.CopyData()), rules, out LobbyState lobby), Is.True);
                    Call(screen, "SetLobby", lobby, names, rules.StartingElements, null, recovering);
                }
                State(false, false);
                Button[] choices = root.GetComponentsInChildren<Button>(true)
                    .Where(button => button.name.StartsWith("Owner color ", StringComparison.Ordinal)).OrderBy(button => button.name).ToArray();
                Assert.That(choices.Length, Is.EqualTo(PlayerPalette.Count));
                Assert.That(choices[2].interactable, Is.False, "다른 사람의 색은 선택할 수 없어야 합니다.");
                Assert.That(choices[0].interactable, Is.True, "AI의 색은 사람이 선택하면 서버가 AI에 남은 색을 배정합니다.");
                Assert.That(choices[14].GetComponentInChildren<TMP_Text>().text, Does.Contain("✓"));
                choices[0].onClick.Invoke();
                Assert.That(requested, Is.Zero);
                Assert.That(choices[14].GetComponentInChildren<TMP_Text>().text, Does.Contain("✓"), "서버 확인 전에 선택 표시를 확정하지 않습니다.");
                State(true, false);
                Assert.That(choices.All(button => !button.interactable), Is.True);
                choices[0].onClick.Invoke();
                State(false, true);
                Assert.That(choices.All(button => !button.interactable), Is.True);
                choices[1].onClick.Invoke();
                Assert.That(requests, Is.EqualTo(1), "준비 중이거나 복구 중에는 색 명령을 보내지 않습니다.");
                State(false, false, 1);
                Assert.That(choices[1].GetComponentInChildren<TMP_Text>().text, Does.Contain("✓"));
                var readyRect = (RectTransform)root.GetComponentsInChildren<Button>(true).Single(button => button.name == "준비").transform;
                foreach (Button choice in choices)
                {
                    RectTransform rect = (RectTransform)choice.transform;
                    Assert.That(rect.anchoredPosition.x + rect.rect.width, Is.LessThanOrEqualTo(((RectTransform)rect.parent).rect.width));
                    Assert.That(-rect.anchoredPosition.y + rect.rect.height, Is.LessThanOrEqualTo(-readyRect.anchoredPosition.y));
                    TMP_Text caption = choice.GetComponentInChildren<TMP_Text>();
                    Assert.That(caption.GetPreferredValues(caption.text).y, Is.LessThanOrEqualTo(rect.rect.height));
                }
                foreach (TMP_Text label in root.GetComponentsInChildren<TMP_Text>(true).Where(label =>
                    label.name == "Selected element name" || label.name == "Owner color label" || label.name == "Lobby status"))
                    Assert.That(label.GetPreferredValues(label.text, label.rectTransform.rect.width, float.PositiveInfinity).y,
                        Is.LessThanOrEqualTo(label.rectTransform.rect.height), label.name);
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (Texture2D texture in font.atlasTextures) if (texture != null) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(font.material);
                Object.DestroyImmediate(font);
            }
        }

        [Test]
        public void PaletteHasFifteenDistinctColorsWithContrastAgainstNavy()
        {
            Assert.That(Enumerable.Range(0, PlayerPalette.Count).Select(PlayerPalette.GetRgb).Distinct().Count(), Is.EqualTo(15));
            float Luminance(Color color) { color = color.linear; return color.r * .2126f + color.g * .7152f + color.b * .0722f; }
            float background = Luminance(new Color32(25, 43, 66, 255));
            for (int id = 0; id < PlayerPalette.Count; id++)
            {
                uint rgb = PlayerPalette.GetRgb(id);
                float foreground = Luminance(new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255));
                Assert.That((foreground + .05f) / (background + .05f), Is.GreaterThanOrEqualTo(3.2f), "색상 " + (id + 1));
            }
        }

        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }
}
