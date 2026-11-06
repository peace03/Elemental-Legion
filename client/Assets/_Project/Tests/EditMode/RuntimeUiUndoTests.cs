using System;
using System.Collections.Generic;
using System.Reflection;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.UI;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>일시적인 게임 UI 생성이 Editor의 Undo 기록에 섞이지 않는지 확인한다.</summary>
    public sealed class RuntimeUiUndoTests
    {
        [Test]
        public void RuntimeControlsKeepTheirReferencesAndValueCallbacksWithoutUndoRecords()
        {
            var root = new GameObject("Runtime controls", typeof(RectTransform));
            root.SetActive(false);
            string[] before = UndoRecords();
            try
            {
                GameObject inputRoot = RuntimeUiControls.CreateInputField();
                inputRoot.transform.SetParent(root.transform, false);
                var input = inputRoot.GetComponent<TMP_InputField>();
                Assert.That(input.textViewport, Is.Not.Null);
                Assert.That(input.textComponent, Is.Not.Null);
                Assert.That(input.placeholder, Is.Not.Null);
                int inputChanges = 0;
                input.onValueChanged.AddListener(_ => inputChanges++);
                input.text = "별의씨앗";
                Assert.That(input.text, Is.EqualTo("별의씨앗"));
                Assert.That(inputChanges, Is.EqualTo(1));

                GameObject dropdownRoot = RuntimeUiControls.CreateDropdown();
                dropdownRoot.transform.SetParent(root.transform, false);
                var dropdown = dropdownRoot.GetComponent<TMP_Dropdown>();
                Assert.That(dropdown.template.gameObject.activeSelf, Is.False);
                Assert.That(dropdown.template.GetComponentInChildren<Toggle>(true), Is.Not.Null);
                Assert.That(dropdown.itemText, Is.Not.Null);
                dropdown.AddOptions(new List<string> { "첫 번째", "두 번째" });
                int selected = -1;
                dropdown.onValueChanged.AddListener(value => selected = value);
                dropdown.value = 1;
                Assert.That(selected, Is.EqualTo(1));
                Assert.That(dropdown.captionText.text, Is.EqualTo("두 번째"));

                GameObject sliderRoot = RuntimeUiControls.CreateSlider();
                sliderRoot.transform.SetParent(root.transform, false);
                var slider = sliderRoot.GetComponent<Slider>();
                Assert.That(slider.fillRect, Is.Not.Null);
                Assert.That(slider.handleRect, Is.Not.Null);
                Assert.That(sliderRoot.transform.Find("Background"), Is.Not.Null);

                GameObject scrollRoot = RuntimeUiControls.CreateScrollView();
                scrollRoot.transform.SetParent(root.transform, false);
                var scroll = scrollRoot.GetComponent<ScrollRect>();
                Assert.That(scroll.viewport.GetComponent<RectMask2D>(), Is.Not.Null);
                Assert.That(scroll.content.parent, Is.EqualTo(scroll.viewport));
                Assert.That(scroll.horizontalScrollbar.handleRect, Is.Not.Null);
                Assert.That(scroll.verticalScrollbar.handleRect, Is.Not.Null);
                Undo.FlushUndoRecordObjects();
                Assert.That(UndoRecords(), Is.EqualTo(before));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UndoHistoryProbeDetectsAndRevertsOnlyItsOwnRecordedChange()
        {
            var sentinel = new GameObject("Undo probe");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            string[] before = UndoRecords();
            try
            {
                Undo.RecordObject(sentinel.transform, "Runtime UI undo probe");
                sentinel.transform.position = Vector3.one;
                Undo.FlushUndoRecordObjects();
                Assert.That(UndoRecords(), Is.Not.EqualTo(before));
                Undo.RevertAllDownToGroup(group);
                Assert.That(sentinel.transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(UndoRecords(), Is.EqualTo(before));
            }
            finally
            {
                Undo.RevertAllDownToGroup(group);
                Object.DestroyImmediate(sentinel);
            }
        }

        [Test]
        public void RuntimeSliderDoesNotAddEditorUndoHistory()
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
            Type skinType = typeof(ClientFlow).Assembly.GetType("ElementalLegion.Client.UI.ObservatoryUi", true);
            object skin = Activator.CreateInstance(skinType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { font }, null);
            var root = new GameObject("Runtime UI undo test", typeof(RectTransform));
            root.SetActive(false);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Transform[] created = null;
            try
            {
                string[] before = UndoRecords();
                var slider = (Slider)skinType.GetMethod("Slider", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(skin, new object[] { root.transform, .5f, 0f, 0f, 160f });
                created = root.GetComponentsInChildren<Transform>(true);
                Assert.That(slider.fillRect, Is.Not.Null);
                Assert.That(slider.handleRect, Is.Not.Null);
                Undo.FlushUndoRecordObjects();
                Assert.That(UndoRecords(), Is.EqualTo(before), "일시적인 게임 UI가 Editor Undo 기록을 추가했습니다.");
            }
            finally
            {
                // 이 테스트가 만든 그룹만 되돌린 뒤 분리된 자식도 정리한다. 사용자 기록은 보존한다.
                Undo.RevertAllDownToGroup(group);
                if (created != null)
                    for (int index = created.Length - 1; index >= 0; index--)
                        if (created[index] != null) Object.DestroyImmediate(created[index].gameObject);
                if (root != null) Object.DestroyImmediate(root);
            }
        }

        private static string[] UndoRecords()
        {
            // 읽기 전용 진단 API이며 현재 Unity 버전의 실제 기록을 비교한다.
            var records = new List<string>();
            MethodInfo get = typeof(Undo).GetMethod("GetRecords", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(List<string>), typeof(int).MakeByRefType() }, null);
            Assert.That(get, Is.Not.Null);
            get.Invoke(null, new object[] { records, 0 });
            return records.ToArray();
        }
    }
}
