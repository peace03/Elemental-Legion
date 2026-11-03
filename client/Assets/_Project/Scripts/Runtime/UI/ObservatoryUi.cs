using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>관측소 화면에서 공통으로 쓰는 uGUI 배치와 색을 정의한다.</summary>
    internal sealed class ObservatoryUi
    {
        internal static readonly Color Background = new Color32(9, 19, 25, 255);
        internal static readonly Color Surface = new Color32(17, 32, 41, 255);
        internal static readonly Color Raised = new Color32(29, 47, 57, 255);
        internal static readonly Color TextColor = new Color32(236, 246, 241, 255);
        internal static readonly Color Muted = new Color32(164, 186, 194, 255);
        internal static readonly Color Accent = new Color32(140, 224, 196, 255);
        internal static readonly Color Gold = new Color32(217, 182, 124, 255);
        internal static readonly Color Danger = new Color32(255, 153, 132, 255);
        internal readonly TMP_FontAsset Font;
        internal bool ReduceMotion { get; set; }

        internal ObservatoryUi(TMP_FontAsset font) => Font = font;

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
            var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>();
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
            label.text = value;
            label.raycastTarget = false;
            return label;
        }

        internal UnityEngine.UI.Button Button(Transform parent, string caption, float x, float y, float width, float height, Action pressed, bool primary = false)
        {
            RectTransform rect = Panel(parent, caption, x, y, width, height, primary ? Accent : Raised, true);
            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = rect.GetComponent<UnityEngine.UI.Image>();
            TMP_Text label = Label(rect, "Label", caption, 17, 12, 0, width - 24, height, primary ? Background : TextColor);
            label.alignment = TextAlignmentOptions.Center;
            var colors = button.colors;
            colors.highlightedColor = new Color(0.8f, 1, 0.92f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.53f, 0.61f, 0.62f);
            colors.fadeDuration = ReduceMotion ? 0 : 0.1f;
            button.colors = colors;
            if (pressed != null) button.onClick.AddListener(() => pressed());
            return button;
        }

        internal TMP_InputField Input(Transform parent, string value, float x, float y, float width)
        {
            GameObject root = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
            root.transform.SetParent(parent, false);
            Place((RectTransform)root.transform, Vector2.up, new Vector2(x, -y), new Vector2(width, 42));
            root.GetComponent<UnityEngine.UI.Image>().color = Raised;
            var input = root.GetComponent<TMP_InputField>();
            input.characterLimit = 253;
            input.textComponent.font = Font;
            input.textComponent.fontSize = 17;
            input.textComponent.color = TextColor;
            ((TMP_Text)input.placeholder).font = Font;
            ((TMP_Text)input.placeholder).text = "서버 주소";
            input.text = value;
            return input;
        }

        internal TMP_Dropdown Dropdown(Transform parent, string[] options, int value, float x, float y, float width)
        {
            GameObject root = TMP_DefaultControls.CreateDropdown(new TMP_DefaultControls.Resources());
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
            return dropdown;
        }

        internal UnityEngine.UI.Slider Slider(Transform parent, float value, float x, float y, float width)
        {
            GameObject root = UnityEngine.UI.DefaultControls.CreateSlider(new UnityEngine.UI.DefaultControls.Resources());
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
