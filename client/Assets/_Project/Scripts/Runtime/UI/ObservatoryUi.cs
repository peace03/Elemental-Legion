using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>관측소 화면에서 공통으로 쓰는 uGUI 배치와 색을 정의한다.</summary>
    internal sealed class ObservatoryUi
    {
        internal static readonly Color Background = new Color32(6, 13, 25, 255);
        internal static readonly Color Surface = new Color32(10, 23, 38, 244);
        internal static readonly Color Raised = new Color32(19, 36, 53, 255);
        internal static readonly Color TextColor = new Color32(239, 241, 230, 255);
        internal static readonly Color Muted = new Color32(164, 181, 197, 255);
        internal static readonly Color Accent = new Color32(76, 226, 226, 255);
        internal static readonly Color Gold = new Color32(219, 191, 139, 255);
        internal static readonly Color Danger = new Color32(255, 133, 112, 255);
        internal readonly TMP_FontAsset Font;
        internal readonly LegionUiArt Art;
        internal bool ReduceMotion { get; set; }

        internal ObservatoryUi(TMP_FontAsset font)
        {
            Font = font;
            // 메뉴가 비어 있는 씬에 동적으로 만들어지므로 공용 그림을 한 곳에서 명시적으로 읽는다.
            Art = Resources.Load<LegionUiArt>(LegionUiArt.ResourcePath);
            if (Art == null) throw new InvalidOperationException("UI 아트 설정이 없습니다. UiArtSetup.Configure를 실행하세요.");
        }

        internal RawImage Backdrop(Transform parent, bool opaque)
        {
            RectTransform root = Rect(parent, "Backdrop", 0, 0, 0, 0);
            Fill(root);
            var blocker = root.gameObject.AddComponent<UnityEngine.UI.Image>();
            blocker.color = opaque ? Background : new Color(0.015f, 0.025f, 0.045f, 0.82f);
            blocker.raycastTarget = true;
            if (!opaque) return null;
            RectTransform art = Rect(root, "Cosmic artwork", 0, 0, 0, 0);
            Fill(art);
            var image = art.gameObject.AddComponent<RawImage>();
            image.texture = Art.Backdrop;
            image.color = new Color(0.28f, 0.28f, 0.32f, 1);
            image.raycastTarget = false;
            return image;
        }

        internal UnityEngine.UI.Image Icon(Transform parent, string name, Sprite sprite,
            float x, float y, float width, float height)
        {
            var image = Rect(parent, name, x, y, width, height).gameObject.AddComponent<UnityEngine.UI.Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        internal void ButtonIcon(UnityEngine.UI.Button button, Sprite sprite, float size = 32)
        {
            RectTransform rect = (RectTransform)button.transform;
            Icon(rect, "Action icon", sprite, 8, (rect.sizeDelta.y - size) * 0.5f, size, size);
            TMP_Text label = button.GetComponentInChildren<TMP_Text>();
            Place(label.rectTransform, Vector2.up, new Vector2(size + 14, 0), new Vector2(rect.sizeDelta.x - size - 22, rect.sizeDelta.y));
        }

        internal void StyleButton(UnityEngine.UI.Button button, bool primary = false)
        {
            var frame = button.targetGraphic as LegionUiFrame;
            if (frame == null)
            {
                var old = button.GetComponent<UnityEngine.UI.Image>();
                if (old != null) { old.color = Color.clear; old.raycastTarget = false; }
                RectTransform graphic = Rect(button.transform, "Button frame", 0, 0, 0, 0);
                Fill(graphic);
                graphic.SetAsFirstSibling();
                frame = graphic.gameObject.AddComponent<LegionUiFrame>();
                button.targetGraphic = frame;
            }
            frame.color = primary ? new Color32(12, 66, 77, 255) : Raised;
            frame.Style(Art.CornerCut, Art.LineWidth, primary ? Accent : new Color(Gold.r, Gold.g, Gold.b, 0.7f));
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.75f, 1, 1);
            colors.selectedColor = new Color(0.6f, 1, 1);
            colors.pressedColor = new Color(0.45f, 0.8f, 0.85f);
            colors.disabledColor = new Color(0.42f, 0.48f, 0.53f, 0.65f);
            colors.fadeDuration = ReduceMotion ? 0 : 0.1f;
            button.colors = colors;
            button.GetComponentInChildren<TMP_Text>().color = TextColor;
        }

        private void StyleControl(Selectable control)
        {
            control.GetComponent<UnityEngine.UI.Image>().color = Color.clear;
            RectTransform rect = Rect(control.transform, "Control frame", 0, 0, 0, 0);
            Fill(rect);
            rect.SetAsFirstSibling();
            var frame = rect.gameObject.AddComponent<LegionUiFrame>();
            frame.color = Raised;
            frame.raycastTarget = false;
            frame.Style(Art.CornerCut * 0.6f, Art.LineWidth, new Color(Gold.r, Gold.g, Gold.b, 0.65f));
            control.targetGraphic = frame;
            ColorBlock colors = control.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = colors.selectedColor = new Color(0.65f, 1, 1);
            colors.disabledColor = new Color(0.4f, 0.46f, 0.5f, 0.7f);
            control.colors = colors;
        }

        internal static void StyleScroll(UnityEngine.UI.ScrollRect scroll)
        {
            if (scroll.verticalScrollbar == null) return;
            scroll.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            var bar = scroll.verticalScrollbar;
            bar.GetComponent<UnityEngine.UI.Image>().color = Raised;
            bar.targetGraphic.color = Muted;
            var rect = (RectTransform)bar.transform;
            rect.sizeDelta = new Vector2(8, rect.sizeDelta.y);
        }

        internal static RectTransform Canvas(Transform parent, string name, int order)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.transform.SetParent(parent, false);
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            SetScale(root.GetComponent<CanvasScaler>(), 100);
            return (RectTransform)root.transform;
        }

        internal static void SetScale(CanvasScaler scaler, int percent)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.referenceResolution = new Vector2(1280, 720) * (100f / percent);
        }

        internal void ApplyMotion(Transform root)
        {
            foreach (Selectable control in root.GetComponentsInChildren<Selectable>(true))
            {
                var colors = control.colors;
                colors.fadeDuration = ReduceMotion ? 0 : 0.1f;
                control.colors = colors;
            }
        }

        internal static void Place(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        internal static void Fill(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        internal static RectTransform Rect(Transform parent, string name, float x, float y, float width, float height)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            Place(rect, Vector2.up, new Vector2(x, -y), new Vector2(width, height));
            return rect;
        }

        internal static RectTransform Panel(Transform parent, string name, float x, float y, float width, float height, Color color, bool blocksInput = false)
        {
            RectTransform rect = Rect(parent, name, x, y, width, height);
            UnityEngine.UI.Image image = width >= 12 && height >= 12
                ? rect.gameObject.AddComponent<LegionUiFrame>() : rect.gameObject.AddComponent<UnityEngine.UI.Image>();
            image.color = color;
            image.raycastTarget = blocksInput;
            return rect;
        }

        internal TMP_Text Label(Transform parent, string name, string value, float size, float x, float y, float width, float height, Color? color = null)
        {
            var label = Rect(parent, name, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = Font;
            label.fontSize = size;
            label.color = color ?? TextColor;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.text = value;
            label.raycastTarget = false;
            return label;
        }

        internal LegionUiFrame SquadBadge(Transform parent, int squad, float numberSize)
        {
            numberSize = Mathf.Clamp(numberSize, 10, 24);
            float size = numberSize + 6;
            string number = squad.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RectTransform badge = Panel(parent, "Squad " + number, 0, 0, size, size, Background);
            Place(badge, Vector2.one * .5f, Vector2.zero, new Vector2(size, size));
            Label(badge, "Squad number", number, numberSize, 0, 0, size, size).alignment = TextAlignmentOptions.Center;
            badge.gameObject.SetActive(false);
            return badge.GetComponent<LegionUiFrame>();
        }

        internal static void StyleSquadBadge(LegionUiFrame badge, bool selected, bool alert = false)
        {
            badge.color = alert ? new Color32(99, 39, 31, 255) : Background;
            badge.Style(3, selected ? 2 : 1, selected ? Accent : alert ? Danger : Muted);
        }

        internal UnityEngine.UI.Button Button(Transform parent, string caption, float x, float y, float width, float height, Action pressed, bool primary = false)
        {
            RectTransform rect = Panel(parent, caption, x, y, width, height, Raised, true);
            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = rect.GetComponent<UnityEngine.UI.Image>();
            TMP_Text label = Label(rect, "Label", caption, 17, 12, 0, width - 24, height, TextColor);
            label.alignment = TextAlignmentOptions.Center;
            StyleButton(button, primary);
            if (pressed != null) button.onClick.AddListener(() => pressed());
            return button;
        }

        internal TMP_InputField Input(Transform parent, string value, float x, float y, float width)
        {
            GameObject root = RuntimeUiControls.CreateInputField();
            root.transform.SetParent(parent, false);
            Place((RectTransform)root.transform, Vector2.up, new Vector2(x, -y), new Vector2(width, 42));
            root.GetComponent<UnityEngine.UI.Image>().color = Raised;
            var input = root.GetComponent<TMP_InputField>();
            input.characterLimit = 253;
            input.textComponent.font = Font;
            input.textComponent.fontSize = 17;
            input.textComponent.color = TextColor;
            ((TMP_Text)input.placeholder).font = Font;
            ((TMP_Text)input.placeholder).color = Muted;
            ((TMP_Text)input.placeholder).text = "서버 주소";
            input.text = value;
            StyleControl(input);
            return input;
        }

        internal TMP_Dropdown Dropdown(Transform parent, string[] options, int value, float x, float y, float width)
        {
            GameObject root = RuntimeUiControls.CreateDropdown();
            root.transform.SetParent(parent, false);
            Place((RectTransform)root.transform, Vector2.up, new Vector2(x, -y), new Vector2(width, 39));
            root.GetComponent<UnityEngine.UI.Image>().color = Raised;
            var dropdown = root.GetComponent<TMP_Dropdown>();
            foreach (TMP_Text label in root.GetComponentsInChildren<TMP_Text>(true))
            {
                label.font = Font;
                label.fontSize = 16;
                label.color = TextColor;
            }
            foreach (UnityEngine.UI.Image image in dropdown.template.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                image.color = Surface;
            var item = dropdown.itemText.GetComponentInParent<UnityEngine.UI.Toggle>();
            if (item != null)
            {
                var colors = item.colors;
                colors.highlightedColor = Accent;
                colors.selectedColor = Accent;
                item.colors = colors;
                if (item.graphic != null) item.graphic.color = Accent;
            }
            dropdown.ClearOptions();
            dropdown.AddOptions(new System.Collections.Generic.List<string>(options));
            dropdown.SetValueWithoutNotify(value);
            dropdown.RefreshShownValue();
            StyleControl(dropdown);
            Transform arrow = root.transform.Find("Arrow");
            if (arrow != null)
            {
                arrow.GetComponent<UnityEngine.UI.Image>().enabled = false;
                for (int index = 0; index < 2; index++)
                {
                    RectTransform stroke = Panel(arrow, "Chevron", 0, 0, 7, 1.5f, Gold);
                    Place(stroke, Vector2.one * 0.5f, new Vector2(index == 0 ? -2 : 2, 0), new Vector2(7, 1.5f));
                    stroke.localRotation = Quaternion.Euler(0, 0, index == 0 ? -45 : 45);
                }
            }
            return dropdown;
        }

        internal UnityEngine.UI.Slider Slider(Transform parent, float value, float x, float y, float width)
        {
            GameObject root = RuntimeUiControls.CreateSlider();
            root.transform.SetParent(parent, false);
            Place((RectTransform)root.transform, Vector2.up, new Vector2(x, -y), new Vector2(width, 24));
            var slider = root.GetComponent<UnityEngine.UI.Slider>();
            slider.fillRect.GetComponent<UnityEngine.UI.Image>().color = Accent;
            slider.handleRect.GetComponent<UnityEngine.UI.Image>().color = TextColor;
            root.transform.Find("Background").GetComponent<UnityEngine.UI.Image>().color = Raised;
            slider.SetValueWithoutNotify(value);
            return slider;
        }
    }
}
