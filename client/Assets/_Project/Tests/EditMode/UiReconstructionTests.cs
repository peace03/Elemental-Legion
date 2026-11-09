using System;
using System.Linq;
using System.Reflection;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.Input;
using ElementalLegion.Client.UI;
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
    /// <summary>공용 UI 그림의 완전성과 화면 재구성 후 입력 계약을 확인한다.</summary>
    public sealed class UiReconstructionTests
    {
        [Test]
        public void SharedArtCoversEveryElementAndSixFacilitySilhouettes()
        {
            var art = Resources.Load<ScriptableObject>("UI/LegionUiArt");
            Assert.That(art, Is.Not.Null, "모든 화면이 사용하는 UI 아트가 필요합니다.");
            var serialized = new SerializedObject(art);
            Assert.That(serialized.FindProperty("backdrop").objectReferenceValue, Is.Not.Null);
            var elements = serialized.FindProperty("elementIcons");
            var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text));
            Assert.That(elements.arraySize, Is.EqualTo(rules.Elements.Count));
            foreach (var definition in rules.Elements)
            {
                bool found = false;
                for (int index = 0; index < elements.arraySize; index++)
                {
                    var entry = elements.GetArrayElementAtIndex(index);
                    if (entry.FindPropertyRelative("id").stringValue != definition.Id) continue;
                    Assert.That(entry.FindPropertyRelative("sprite").objectReferenceValue, Is.Not.Null, definition.Id);
                    found = true;
                }
                Assert.That(found, Is.True, definition.Id);
            }
            var facilities = serialized.FindProperty("buildingIcons");
            Assert.That(facilities.arraySize, Is.EqualTo(6));
            for (int index = 0; index < facilities.arraySize; index++)
                Assert.That(facilities.GetArrayElementAtIndex(index).objectReferenceValue, Is.Not.Null);
        }

        [Test]
        public void TitleArtworkKeepsNicknameValidationAndEmitsOneStartIntent()
        {
            Assembly assembly = typeof(ClientFlow).Assembly;
            Type skinType = assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            Type screenType = assembly.GetType("ElementalLegion.Client.UI.ClientScreen", true);
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
            object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { font }, null);
            var root = new GameObject("Title input test");
            root.SetActive(false);
            try
            {
                Component screen = root.AddComponent(screenType);
                Invoke(screen, "Initialize", skin, 0, true);
                int starts = 0;
                string saved = null;
                Invoke(screen, "BuildTitle", "별의씨앗_01", (Action<string>)(value => saved = value),
                    (Action)(() => { }), (Action)(() => starts++), (Action)(() => { }),
                    (Action)(() => { }), (Action)(() => { }));
                var nickname = root.GetComponentsInChildren<TMP_InputField>(true).Single();
                var play = root.GetComponentsInChildren<Button>(true).Single(button =>
                {
                    string caption = button.GetComponentInChildren<TMP_Text>(true).text;
                    return caption == "게임 시작" || caption == "시작하기";
                });
                nickname.text = "<b>별</b>";
                play.onClick.Invoke();
                Assert.That(starts, Is.Zero);
                nickname.text = "  별의씨앗_UI  ";
                play.onClick.Invoke();
                Assert.That(starts, Is.EqualTo(1));
                Assert.That(saved, Is.EqualTo("별의씨앗_UI"));
                var title = root.GetComponentsInChildren<TMP_Text>(true).Single(text => text.name == "Game title");
                Assert.That(title.GetPreferredValues(title.text, title.rectTransform.rect.width, float.PositiveInfinity).y,
                    Is.LessThanOrEqualTo(title.rectTransform.rect.height), "게임 제목 두 줄이 모두 보여야 합니다.");
                Assert.That(root.GetComponentsInChildren<RawImage>(true)
                    .Any(image => image.texture != null && !image.raycastTarget), Is.True,
                    "배경 그림은 입력을 가로채지 않아야 합니다.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DashboardKeepsTheBattlefieldVisibleAndPreservesItsCloseFlow(bool held)
        {
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            string[] names = rules.Elements.Select((definition, index) => (string)document.Root["elements"][index]["displayName"]).ToArray();
            Assembly assembly = typeof(ClientFlow).Assembly;
            Type skinType = assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            Type screenType = assembly.GetType("ElementalLegion.Client.UI.ClientScreen", true);
            object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset") }, null);
            var root = new GameObject("Dashboard battlefield overlay test");
            root.SetActive(false);
            try
            {
                Component screen = root.AddComponent(screenType);
                // 미리보기처럼 불투명 초기화를 거쳐도 현황판에는 우주 그림이 남지 않아야 한다.
                Invoke(screen, "Initialize", skin, 60, true);
                int closed = 0;
                Invoke(screen, "BuildDashboard", held ? null : (Action)(() => closed++),
                    held ? null : (Action)(() => Invoke(screen, "ShowMatchups", rules, document, names)), held ? "Tab" : null);
                Transform canvas = root.transform.Find("Observatory canvas");
                var backdrop = canvas.Find("Backdrop").GetComponent<UnityEngine.UI.Image>();
                Assert.That(backdrop.color.a, Is.Zero, "전체 화면 배경은 전장 카메라를 가리지 않아야 합니다.");
                Assert.That(backdrop.raycastTarget, Is.True, "현황판 뒤의 게임 조작은 막아야 합니다.");
                Assert.That(root.GetComponentsInChildren<RawImage>(true).All(image => !image.gameObject.activeSelf), Is.True);
                Transform dashboard = canvas.Find("Player dashboard");
                if (held)
                {
                    Assert.That(dashboard.Find("Hold hint").GetComponent<TMP_Text>().text, Does.Contain("Tab"));
                    Assert.That(dashboard.Find("돌아가기"), Is.Null);
                }
                else
                {
                    dashboard.Find("원소 상성표").GetComponent<Button>().onClick.Invoke();
                    Transform matchups = canvas.Find("Element matchups");
                    Assert.That(dashboard.gameObject.activeSelf, Is.False);
                    Assert.That(matchups.gameObject.activeSelf, Is.True);
                    matchups.Find("돌아가기").GetComponent<Button>().onClick.Invoke();
                    Assert.That(dashboard.gameObject.activeSelf, Is.True);
                    Assert.That(matchups.gameObject.activeSelf, Is.False);
                    dashboard.Find("돌아가기").GetComponent<Button>().onClick.Invoke();
                    Assert.That(closed, Is.EqualTo(1));
                }
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void Invoke(Component target, string method, params object[] arguments)
        {
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, arguments);
        }

        [Test]
        public void AtlasIconsUseTheCurrentTextureDimensions()
        {
            var art = new SerializedObject(Resources.Load<ScriptableObject>("UI/LegionUiArt"));
            var elements = art.FindProperty("elementIcons");
            for (int index = 0; index < elements.arraySize; index++)
            {
                var sprite = (Sprite)elements.GetArrayElementAtIndex(index).FindPropertyRelative("sprite").objectReferenceValue;
                Assert.That(sprite.rect.width, Is.EqualTo(sprite.texture.width / 4f).Within(0.01), sprite.name);
                Assert.That(sprite.rect.height, Is.EqualTo(sprite.texture.height / 4f).Within(0.01), sprite.name);
            }
            var facilities = art.FindProperty("buildingIcons");
            for (int index = 0; index < facilities.arraySize; index++)
            {
                var sprite = (Sprite)facilities.GetArrayElementAtIndex(index).objectReferenceValue;
                Assert.That(sprite.rect.width, Is.EqualTo(sprite.texture.width / 3f).Within(0.01), sprite.name);
                Assert.That(sprite.rect.height, Is.EqualTo(sprite.texture.height / 2f).Within(0.01), sprite.name);
            }
        }

        [Test]
        public void MinimapReleasesItsPreviewTextureWhenDestroyed()
        {
            var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text));
            var root = new GameObject("Map cleanup", typeof(RectTransform));
            Texture texture = null;
            try
            {
                var image = root.AddComponent<RawImage>();
                var child = new GameObject("Map markers", typeof(RectTransform));
                child.transform.SetParent(root.transform, false);
                var map = child.AddComponent<ObservatoryMap>();
                Invoke(map, "Initialize", image, rules);
                texture = image.texture;
                Object.DestroyImmediate(root);
                Assert.That(texture == null, Is.True);
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (texture != null) Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void TrainingUsesTheRightSidebarAndRetainsFourteenOriginalButtons()
        {
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            Type skinType = typeof(ClientFlow).Assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset") }, null);
            GameObject root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var hud = root.GetComponentInChildren<CombatHud>(true);
                hud.Configure(document, rules);
                Invoke(hud, "BuildObservatory", skin, rules, root.GetComponent<ElementalVisuals>());
                var serialized = new SerializedObject(hud);
                var buttons = serialized.FindProperty("train");
                Assert.That(buttons.arraySize, Is.EqualTo(14));
                var button = (Button)buttons.GetArrayElementAtIndex(0).objectReferenceValue;
                var panel = (RectTransform)button.transform.parent;
                Assert.That(panel.sizeDelta.x, Is.LessThanOrEqualTo(320), "양성 목록이 전장 중앙을 덮지 않아야 합니다.");
                Assert.That(panel.anchorMax, Is.EqualTo(Vector2.one));
                int intents = 0;
                hud.TrainRequested += (_, _, _) => intents++;
                button.onClick.Invoke();
                Assert.That(intents, Is.EqualTo(1));
                var canvas = button.GetComponentInParent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.GetComponent<CanvasScaler>().enabled = false;
                ((RectTransform)canvas.transform).sizeDelta = new Vector2(1280, 720) / 1.1f;
                Canvas.ForceUpdateCanvases();
                Invoke(hud, "ApplyPreferences", new ControlBindings(), 110);
                var label = (TMP_Text)serialized.FindProperty("labels").GetArrayElementAtIndex(0).objectReferenceValue;
                label.text = "암흑 물질\n<size=9>근접 17핵 13.6초</size>\n<size=9>미해금 · 양성소 필요</size>";
                Assert.That(label.GetPreferredValues(label.text, label.rectTransform.rect.width, float.PositiveInfinity).y,
                    Is.LessThanOrEqualTo(label.rectTransform.rect.height), "UI 110%에서도 양성 설명이 버튼 안에 들어와야 합니다.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void IndividualAiDifficultiesFitEveryRowAndFollowHostAndRecoveryPermissions()
        {
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            string[] names = rules.Elements.Select((definition, index) => (string)document.Root["elements"][index]["displayName"]).ToArray();
            string[] starting = rules.StartingElements.Select(index => names[index]).ToArray();
            Assembly assembly = typeof(ClientFlow).Assembly;
            Type skinType = assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            Type screenType = assembly.GetType("ElementalLegion.Client.UI.ClientScreen", true);
            object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset") }, null);
            var root = new GameObject("Individual AI difficulty test");
            root.SetActive(false);
            try
            {
                Component screen = root.AddComponent(screenType);
                Invoke(screen, "Initialize", skin, 0, true);
                int changes = 0, changedAi = -1, requestedCount = -1;
                AiDifficulty requestedDifficulty = AiDifficulty.Normal;
                Invoke(screen, "BuildLobby", starting, rules, 0, rules.MaxPlayers, (Action<int, bool>)((_, __) => { }),
                    (Action<int>)(count => requestedCount = count),
                    (Action<int, AiDifficulty>)((index, difficulty) => { changes++; changedAi = index; requestedDifficulty = difficulty; }),
                    (Action)(() => { }), (Action)(() => { }), (Action<int>)(_ => { }));

                TMP_Dropdown[] SetState(int people, int self, AiDifficulty[] levels, bool recovering = false)
                {
                    var members = Enumerable.Range(0, people).Select(index =>
                        new LobbyMember("플레이어" + index, rules.StartingElements[index % starting.Length], false)).ToArray();
                    var writer = new NetDataWriter();
                    M5Protocol.WriteLobbyState(writer, "AI 난이도", self, 2, rules.MaxPlayers, members, levels.Length, levels);
                    Assert.That(M5Protocol.TryReadLobbyState(new NetDataReader(writer.CopyData()), rules, out LobbyState lobby), Is.True);
                    Invoke(screen, "SetLobby", lobby, names, rules.StartingElements, null, recovering);
                    return root.GetComponentsInChildren<TMP_Dropdown>(true).Where(choice => choice.name == "AI difficulty" &&
                        choice.gameObject.activeSelf && choice.transform.parent.gameObject.activeSelf).ToArray();
                }

                var levels = Enumerable.Range(0, rules.MaxPlayers - 1).Select(index => (AiDifficulty)(index % 3)).ToArray();
                TMP_Dropdown[] choices = SetState(1, 0, levels);
                Assert.That(choices.Length, Is.EqualTo(rules.MaxPlayers - 1));
                Assert.That(choices.Select(choice => choice.value), Is.EqualTo(levels.Select(level => (int)level)));
                Assert.That(choices.All(choice => choice.interactable), Is.True);
                Assert.That(changes, Is.Zero, "서버 상태를 표시할 때 변경 명령을 재전송하지 않아야 합니다.");
                foreach (TMP_Dropdown choice in choices)
                {
                    Assert.That(choice.options.Select(option => option.text), Is.EqualTo(new[] { "쉬움", "보통", "어려움" }));
                    var rect = (RectTransform)choice.transform;
                    var row = (RectTransform)rect.parent;
                    var label = row.Find("Member").GetComponent<TMP_Text>().rectTransform;
                    Assert.That(label.anchoredPosition.x + label.rect.width, Is.LessThanOrEqualTo(rect.anchoredPosition.x));
                    Assert.That(rect.anchoredPosition.x + rect.rect.width, Is.LessThanOrEqualTo(row.rect.width));
                    Assert.That(rect.rect.height, Is.LessThanOrEqualTo(row.rect.height));
                    Assert.That(choice.captionText.rectTransform.rect.height,
                        Is.GreaterThanOrEqualTo(choice.captionText.GetPreferredValues("어려움").y));
                }
                choices[0].value = (int)AiDifficulty.Hard;
                Assert.That(changes, Is.EqualTo(1));
                Assert.That(changedAi, Is.Zero);
                Assert.That(requestedDifficulty, Is.EqualTo(AiDifficulty.Hard));
                levels[0] = AiDifficulty.Hard;
                root.GetComponentsInChildren<Button>(true).Single(button => button.name == "AI 제거").onClick.Invoke();
                Assert.That(requestedCount, Is.EqualTo(levels.Length - 1));
                choices = SetState(1, 0, levels.Take(levels.Length - 1).ToArray());
                Assert.That(choices.Select(choice => choice.value), Is.EqualTo(levels.Take(levels.Length - 1).Select(level => (int)level)));
                root.GetComponentsInChildren<Button>(true).Single(button => button.name == "AI 추가").onClick.Invoke();
                Assert.That(requestedCount, Is.EqualTo(levels.Length));
                levels[levels.Length - 1] = AiDifficulty.Normal;
                choices = SetState(1, 0, levels);
                Assert.That(choices.Last().value, Is.EqualTo((int)AiDifficulty.Normal));
                Assert.That(choices.First().value, Is.EqualTo((int)AiDifficulty.Hard));

                // 사람이 늘어 행 위치가 바뀌어도 AI 순서와 게스트의 읽기 전용 표시를 유지한다.
                levels = levels.Take(levels.Length - 1).ToArray();
                choices = SetState(2, 1, levels);
                Assert.That(choices.Length, Is.EqualTo(levels.Length));
                Assert.That(choices.Select(choice => choice.value), Is.EqualTo(levels.Select(level => (int)level)));
                Assert.That(choices.All(choice => !choice.interactable), Is.True);
                choices[0].onValueChanged.Invoke((int)AiDifficulty.Easy);
                Assert.That(changes, Is.EqualTo(1), "게스트는 AI 난이도 변경 명령을 보내지 않아야 합니다.");
                choices = SetState(2, 0, levels, true);
                Assert.That(choices.All(choice => !choice.interactable), Is.True);
                choices[0].onValueChanged.Invoke((int)AiDifficulty.Easy);
                Assert.That(changes, Is.EqualTo(1), "복구 중에는 방장도 변경 명령을 보내지 않아야 합니다.");
                choices = SetState(2, 0, levels);
                Assert.That(choices.All(choice => choice.interactable), Is.True);
                choices[1].value = (int)AiDifficulty.Hard;
                Assert.That(changes, Is.EqualTo(2));
                Assert.That(changedAi, Is.EqualTo(1), "사람이 늘어도 화면 행 번호 대신 현재 AI 순번을 전송해야 합니다.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void FullLobbyLocksTheNewElementChoicesDuringRecovery()
        {
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            string[] names = rules.Elements.Select((definition, index) => (string)document.Root["elements"][index]["displayName"]).ToArray();
            string[] starting = rules.StartingElements.Select(index => names[index]).ToArray();
            Assembly assembly = typeof(ClientFlow).Assembly;
            Type skinType = assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            Type screenType = assembly.GetType("ElementalLegion.Client.UI.ClientScreen", true);
            object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset") }, null);
            var root = new GameObject("Full lobby recovery test");
            root.SetActive(false);
            try
            {
                Component screen = root.AddComponent(screenType);
                Invoke(screen, "Initialize", skin, 0, true);
                Invoke(screen, "BuildLobby", starting, rules, 0, rules.MaxPlayers, (Action<int, bool>)((_, __) => { }),
                    (Action<int>)(_ => { }), (Action<int, AiDifficulty>)((_, __) => { }), (Action)(() => { }), (Action)(() => { }), (Action<int>)(_ => { }));
                var members = Enumerable.Range(0, rules.MaxPlayers).Select(index =>
                    new LobbyMember(new string('별', 14) + index.ToString("D2"), rules.StartingElements[index % starting.Length], false)).ToArray();
                var writer = new NetDataWriter();
                M5Protocol.WriteLobbyState(writer, new string('별', 32), 0, 2, rules.MaxPlayers, members);
                Assert.That(M5Protocol.TryReadLobbyState(new NetDataReader(writer.CopyData()), rules, out LobbyState lobby), Is.True);
                var choices = (Button[])screenType.GetField("elementChoices", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screen);
                Invoke(screen, "SetLobby", lobby, names, rules.StartingElements, null, true);
                Assert.That(choices.Length, Is.EqualTo(starting.Length));
                Assert.That(choices.All(button => !button.interactable), Is.True);
                var rows = (TMP_Text[])screenType.GetField("memberRows", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screen);
                Assert.That(rows.Count(row => row.gameObject.activeSelf), Is.EqualTo(rules.MaxPlayers));
                Assert.That(rows.All(row => !row.richText), Is.True);
                Invoke(screen, "SetLobby", lobby, names, rules.StartingElements, null, false);
                Assert.That(choices.All(button => button.interactable), Is.True);
                var count = root.GetComponentsInChildren<TMP_Text>(true).SingleOrDefault(label => label.name == "Lobby player count");
                Assert.That(count, Is.Not.Null, "대기실 제목 옆에 인원이 표시되어야 합니다.");
                Assert.That(count.text, Is.EqualTo($"{rules.MaxPlayers} / {rules.MaxPlayers}"));
                M5Protocol.WriteLobbyState(writer, lobby.Title, 0, 2, rules.MaxPlayers, members.Take(2).ToArray(), 1);
                Assert.That(M5Protocol.TryReadLobbyState(new NetDataReader(writer.CopyData()), rules, out lobby), Is.True);
                Invoke(screen, "SetLobby", lobby, names, rules.StartingElements, null, false);
                Assert.That(count.text, Is.EqualTo($"3 / {rules.MaxPlayers}"), "AI도 정원을 사용하는 참가자입니다.");
                Assert.That(rows.Count(row => row.transform.parent.gameObject.activeSelf), Is.EqualTo(3), "빈 슬롯의 배경도 숨겨야 합니다.");
                Assert.That(root.GetComponentsInChildren<TMP_Text>(true).Any(label => label.text == "빈 자리"), Is.False);
                M5Protocol.WriteLobbyState(writer, lobby.Title, 0, 2, rules.MaxPlayers, members);
                Assert.That(M5Protocol.TryReadLobbyState(new NetDataReader(writer.CopyData()), rules, out lobby), Is.True);
                Invoke(screen, "SetLobby", lobby, names, rules.StartingElements, null, false);
                Assert.That(rows.Count(row => row.transform.parent.gameObject.activeSelf), Is.EqualTo(rules.MaxPlayers));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(BuildingKind.Base)]
        [TestCase(BuildingKind.NatureForge)]
        [TestCase(BuildingKind.EnergyForge)]
        [TestCase(BuildingKind.Collector)]
        [TestCase(BuildingKind.NatureTower)]
        [TestCase(BuildingKind.EnergyTower)]
        [TestCase(BuildingKind.Dome)]
        public void BuildingDescriptionsWrapAndFitTheirFrame(BuildingKind kind)
        {
            var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var rules = new GameRules(document);
            string[] names = rules.Elements.Select((definition, index) => (string)document.Root["elements"][index]["displayName"]).ToArray();
            var font = TMP_FontAsset.CreateFontAsset(AssetDatabase.LoadAssetAtPath<Font>(
                "Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf"), 40, 5,
                UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            var root = new GameObject("Building description test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                Assembly assembly = typeof(ClientFlow).Assembly;
                Type skinType = assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
                Type tooltipType = assembly.GetType("ElementalLegion.Client.UI.BuildingTooltip", true);
                object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { font }, null);
                object tooltip = Activator.CreateInstance(tooltipType, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { skin, (RectTransform)root.transform, rules, names }, null);
                ((RectTransform)root.transform).sizeDelta = new Vector2(1280, 720) / 1.1f;
                tooltipType.GetMethod("ShowMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tooltip,
                    new object[] { kind, rules.ElementIndex("dark_matter"), "슬롯 제한", Vector2.zero });
                var frame = (RectTransform)root.transform.Find("Building tooltip");
                TMP_Text[] labels = frame.GetComponentsInChildren<TMP_Text>(true);
                AssertTooltipTextFits(frame, labels);
                Rect bounds = ((RectTransform)root.transform).rect;
                foreach (Vector2 cursor in new[] { bounds.min, bounds.max })
                {
                    tooltipType.GetMethod("ShowMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tooltip,
                        new object[] { kind, rules.ElementIndex("dark_matter"), "슬롯 제한", cursor });
                    Assert.That(frame.anchoredPosition.x, Is.GreaterThanOrEqualTo(bounds.xMin));
                    Assert.That(frame.anchoredPosition.x + frame.rect.width, Is.LessThanOrEqualTo(bounds.xMax));
                    Assert.That(frame.anchoredPosition.y - frame.rect.height, Is.GreaterThanOrEqualTo(bounds.yMin));
                    Assert.That(frame.anchoredPosition.y, Is.LessThanOrEqualTo(bounds.yMax));
                }
                float menuHeight = frame.rect.height;
                tooltipType.GetMethod("ShowWorld", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tooltip,
                    new object[] { new BuildingView(1, 1, kind, System.Numerics.Vector2.Zero, 1), 100L, Vector2.zero });
                AssertTooltipTextFits(frame, labels);
                Assert.That(labels.Single(label => label.name == "Building status").text, Is.Empty);
                Assert.That(frame.rect.height, Is.LessThan(menuHeight), "표시할 상태가 없으면 설명창 높이도 줄어야 합니다.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (Texture2D texture in font.atlasTextures) if (texture != null) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(font.material);
                Object.DestroyImmediate(font);
            }
        }

        private static void AssertTooltipTextFits(RectTransform frame, TMP_Text[] labels)
        {
            foreach (TMP_Text label in labels)
            {
                if (string.IsNullOrEmpty(label.text)) continue;
                Assert.That(label.overflowMode, Is.Not.EqualTo(TextOverflowModes.Ellipsis), label.name);
                Vector2 required = label.GetPreferredValues(label.text, label.rectTransform.rect.width, float.PositiveInfinity);
                Assert.That(required.x, Is.LessThanOrEqualTo(label.rectTransform.rect.width + 0.1f), label.name);
                Assert.That(required.y, Is.LessThanOrEqualTo(label.rectTransform.rect.height + 0.1f), label.name);
                Assert.That(label.rectTransform.anchoredPosition.y - label.rectTransform.rect.height,
                    Is.GreaterThanOrEqualTo(-frame.rect.height), label.name);
            }
        }
    }
}
