using System;
using ElementalLegion.Shared.Data;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ElementalLegion.Client.UI
{
    /// <summary>선택 버튼이 잠겨 있어도 시작 원소의 정보를 표시한다.</summary>
    internal sealed class LobbyElementHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private LobbyElementTooltip tooltip;
        private string elementName, roleLabel;

        internal void Bind(LobbyElementTooltip view, string name, UnitRole role)
        {
            tooltip = view;
            elementName = name;
            roleLabel = role switch
            {
                UnitRole.Melee => "전투 역할 · 근접",
                UnitRole.Mid => "전투 역할 · 중거리",
                UnitRole.Ranged => "전투 역할 · 원거리",
                _ => throw new ArgumentOutOfRangeException(nameof(role))
            };
        }

        public void OnPointerEnter(PointerEventData eventData) => tooltip?.Show((RectTransform)transform, elementName, roleLabel);
        public void OnPointerExit(PointerEventData eventData) => Hide();
        private void OnDisable() => Hide();
        private void Hide() => tooltip?.Hide((RectTransform)transform);
    }

    internal sealed class LobbyElementTooltip
    {
        private const float Width = 176, Height = 56, Gap = 8;
        private readonly RectTransform parent, root;
        private readonly TMP_Text title, role;
        private RectTransform hovered;

        internal LobbyElementTooltip(ObservatoryUi ui, RectTransform parent)
        {
            this.parent = parent;
            root = ObservatoryUi.Panel(parent, "Starting element tooltip", 0, 0, Width, Height, ObservatoryUi.Surface);
            title = ui.Label(root, "Element name", "", 17, 12, 5, Width - 24, 25, ObservatoryUi.Gold);
            role = ui.Label(root, "Element role", "", 14, 12, 29, Width - 24, 22);
            Hide();
        }

        internal void Show(RectTransform choice, string name, string roleLabel)
        {
            hovered = choice;
            title.text = name;
            role.text = roleLabel;

            // 버튼과 정보창의 좌표를 같은 대기실 패널 기준으로 계산한다.
            Rect bounds = parent.rect;
            Vector3 choiceTop = parent.InverseTransformPoint(choice.TransformPoint(new Vector3(choice.rect.xMin, choice.rect.yMax)));
            float x = Mathf.Clamp(choiceTop.x - bounds.xMin, 0, bounds.width - Width);
            float y = Mathf.Clamp(choiceTop.y - bounds.yMax + Gap + Height, Height - bounds.height, 0);
            root.anchoredPosition = new Vector2(x, y);
            root.SetAsLastSibling();
            root.gameObject.SetActive(true);
        }

        internal void Hide(RectTransform choice)
        {
            if (hovered == choice) Hide();
        }

        internal void Hide()
        {
            hovered = null;
            if (root != null) root.gameObject.SetActive(false);
        }
    }
}
