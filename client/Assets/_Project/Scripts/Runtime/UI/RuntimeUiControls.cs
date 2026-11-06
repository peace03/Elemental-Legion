using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>Editor 메뉴용 팩터리와 Undo를 거치지 않고 런타임 UI의 필수 참조를 구성한다.</summary>
    public static class RuntimeUiControls
    {
        public static GameObject CreateInputField()
        {
            RectTransform root = Node("InputField (TMP)", null, typeof(Image), typeof(TMP_InputField));
            root.sizeDelta = new Vector2(160, 30);
            RectTransform viewport = Node("Text Area", root, typeof(RectMask2D));
            viewport.offsetMin = new Vector2(10, 6);
            viewport.offsetMax = new Vector2(-10, -7);
            viewport.GetComponent<RectMask2D>().padding = new Vector4(-8, -5, -8, -5);
            TMP_Text placeholder = Text(viewport, "Placeholder");
            placeholder.text = "Enter text...";
            placeholder.fontStyle = FontStyles.Italic;
            placeholder.color = new Color(0.2f, 0.2f, 0.2f, 0.5f);
            placeholder.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            TMP_Text text = Text(viewport, "Text");
            text.extraPadding = placeholder.extraPadding = true;
            var input = root.GetComponent<TMP_InputField>();
            input.targetGraphic = root.GetComponent<Image>();
            input.textViewport = viewport;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.fontAsset = text.font;
            return root.gameObject;
        }

        public static GameObject CreateDropdown()
        {
            RectTransform root = Node("Dropdown", null, typeof(Image), typeof(TMP_Dropdown));
            root.sizeDelta = new Vector2(160, 30);
            TMP_Text caption = Text(root, "Label");
            caption.rectTransform.offsetMin = new Vector2(10, 6);
            caption.rectTransform.offsetMax = new Vector2(-25, -7);
            RectTransform arrow = Node("Arrow", root, typeof(Image));
            ObservatoryUi.Place(arrow, new Vector2(1, 0.5f), new Vector2(-15, 0), new Vector2(20, 20));

            RectTransform template = Node("Template", root, typeof(Image), typeof(ScrollRect));
            template.gameObject.SetActive(false);
            template.anchorMax = Vector2.right;
            template.pivot = new Vector2(0.5f, 1);
            template.anchoredPosition = new Vector2(0, 2);
            template.sizeDelta = new Vector2(0, 150);
            RectTransform viewport = Node("Viewport", template, typeof(RectMask2D));
            viewport.pivot = Vector2.up;
            viewport.offsetMax = new Vector2(-18, 0);
            RectTransform content = Node("Content", viewport);
            TopContent(content, 28);
            RectTransform item = Node("Item", content, typeof(Toggle));
            item.anchorMin = new Vector2(0, 0.5f);
            item.anchorMax = new Vector2(1, 0.5f);
            item.sizeDelta = new Vector2(0, 20);
            Image background = Node("Item Background", item, typeof(Image)).GetComponent<Image>();
            Image checkmark = Node("Item Checkmark", item, typeof(Image)).GetComponent<Image>();
            ObservatoryUi.Place(checkmark.rectTransform, new Vector2(0, 0.5f), new Vector2(10, 0), new Vector2(20, 20));
            TMP_Text label = Text(item, "Item Label");
            label.rectTransform.offsetMin = new Vector2(20, 1);
            label.rectTransform.offsetMax = new Vector2(-10, -2);
            var toggle = item.GetComponent<Toggle>();
            toggle.targetGraphic = background;
            toggle.graphic = checkmark;
            toggle.isOn = true;

            var scroll = template.GetComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.verticalScrollbar = CreateScrollbar(template, true);
            scroll.verticalScrollbar.name = "Scrollbar";
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scroll.verticalScrollbarSpacing = -3;
            var dropdown = root.GetComponent<TMP_Dropdown>();
            dropdown.targetGraphic = root.GetComponent<Image>();
            dropdown.template = template;
            dropdown.captionText = caption;
            dropdown.itemText = label;
            return root.gameObject;
        }

        public static GameObject CreateSlider()
        {
            RectTransform root = Node("Slider", null, typeof(Slider));
            root.sizeDelta = new Vector2(160, 20);
            RectTransform background = Node("Background", root, typeof(Image));
            background.anchorMin = new Vector2(0, 0.25f);
            background.anchorMax = new Vector2(1, 0.75f);
            RectTransform fillArea = Node("Fill Area", root);
            fillArea.anchorMin = background.anchorMin;
            fillArea.anchorMax = background.anchorMax;
            fillArea.anchoredPosition = new Vector2(-5, 0);
            fillArea.sizeDelta = new Vector2(-20, 0);
            RectTransform fill = Node("Fill", fillArea, typeof(Image));
            fill.sizeDelta = new Vector2(10, 0);
            RectTransform handleArea = Node("Handle Slide Area", root);
            handleArea.sizeDelta = new Vector2(-20, 0);
            RectTransform handle = Node("Handle", handleArea, typeof(Image));
            handle.sizeDelta = new Vector2(20, 0);
            var slider = root.GetComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            return root.gameObject;
        }

        public static GameObject CreateScrollView()
        {
            RectTransform root = Node("Scroll View", null, typeof(Image), typeof(ScrollRect));
            root.sizeDelta = new Vector2(200, 200);
            root.GetComponent<Image>().color = new Color(1, 1, 1, 0.392f);
            RectTransform viewport = Node("Viewport", root, typeof(RectMask2D));
            viewport.pivot = Vector2.up;
            RectTransform content = Node("Content", viewport);
            TopContent(content, 300);
            var scroll = root.GetComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontalScrollbar = CreateScrollbar(root, false);
            scroll.verticalScrollbar = CreateScrollbar(root, true);
            scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scroll.horizontalScrollbarSpacing = scroll.verticalScrollbarSpacing = -3;
            return root.gameObject;
        }

        private static Scrollbar CreateScrollbar(Transform parent, bool vertical)
        {
            RectTransform root = Node(vertical ? "Scrollbar Vertical" : "Scrollbar Horizontal", parent, typeof(Image), typeof(Scrollbar));
            root.anchorMin = vertical ? Vector2.right : Vector2.zero;
            root.anchorMax = vertical ? Vector2.one : Vector2.right;
            root.pivot = vertical ? Vector2.one : Vector2.zero;
            root.sizeDelta = vertical ? new Vector2(20, 0) : new Vector2(0, 20);
            RectTransform area = Node("Sliding Area", root);
            area.sizeDelta = new Vector2(-20, -20);
            RectTransform handle = Node("Handle", area, typeof(Image));
            handle.sizeDelta = new Vector2(20, 20);
            var scrollbar = root.GetComponent<Scrollbar>();
            scrollbar.direction = vertical ? Scrollbar.Direction.BottomToTop : Scrollbar.Direction.LeftToRight;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handle.GetComponent<Image>();
            return scrollbar;
        }

        private static void TopContent(RectTransform content, float height)
        {
            content.anchorMin = content.pivot = Vector2.up;
            content.anchorMax = Vector2.one;
            content.sizeDelta = new Vector2(0, height);
            content.anchoredPosition = Vector2.zero;
        }

        private static TMP_Text Text(Transform parent, string name)
        {
            var text = Node(name, parent, typeof(CanvasRenderer), typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
            text.text = string.Empty;
            text.fontSize = 14;
            text.color = new Color32(50, 50, 50, 255);
            text.alignment = TextAlignmentOptions.Left;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }

        private static RectTransform Node(string name, Transform parent, params Type[] components)
        {
            var types = new Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            Array.Copy(components, 0, types, 1, components.Length);
            var rect = (RectTransform)new GameObject(name, types).transform;
            rect.SetParent(parent, false);
            if (parent != null) ObservatoryUi.Fill(rect);
            return rect;
        }
    }
}
