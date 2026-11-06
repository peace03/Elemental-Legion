using System;
using ElementalLegion.Shared.Sim;
using UnityEngine;

namespace ElementalLegion.Client.UI
{
    /// <summary>메뉴와 전장 HUD가 함께 사용하는 표시 전용 그림과 프레임 설정이다.</summary>
    [CreateAssetMenu(menuName = "Elemental Legion/UI Art")]
    public sealed class LegionUiArt : ScriptableObject
    {
        internal const string ResourcePath = "UI/LegionUiArt";
        [Serializable]
        private sealed class ElementIcon
        {
            [SerializeField] private string id;
            [SerializeField] private string displayName;
            [SerializeField] private Sprite sprite;
            internal string Id => id;
            internal string DisplayName => displayName;
            internal Sprite Sprite => sprite;
        }

        [SerializeField, Tooltip("글자와 버튼을 포함하지 않는 우주 배경입니다.")] private Texture2D backdrop;
        [SerializeField, Tooltip("공유 데이터의 원소 ID와 대응하는 14종 아이콘입니다.")] private ElementIcon[] elementIcons;
        [SerializeField, Tooltip("원시핵·포집기·응집로·점화로·수호 위성·자기권 순서의 아이콘입니다.")] private Sprite[] buildingIcons;
        [SerializeField, Range(0, 24), Tooltip("패널 모서리를 잘라 내는 길이(UI 기준 픽셀)입니다.")] private float cornerCut = 10;
        [SerializeField, Range(0.5f, 3), Tooltip("프레임 테두리 두께(UI 기준 픽셀)입니다.")] private float lineWidth = 1;
        public Texture2D Backdrop => backdrop;
        internal float CornerCut => cornerCut;
        internal float LineWidth => lineWidth;

        internal Sprite Element(string id)
        {
            foreach (ElementIcon entry in elementIcons)
                if (entry.Id == id || entry.DisplayName == id) return entry.Sprite;
            throw new InvalidOperationException("UI 원소 아이콘이 없습니다: " + id);
        }

        internal Sprite Building(BuildingKind kind)
        {
            int index = kind switch
            {
                BuildingKind.Base => 0,
                BuildingKind.Collector => 1,
                BuildingKind.NatureForge => 2,
                BuildingKind.EnergyForge => 3,
                BuildingKind.NatureTower => 4,
                BuildingKind.EnergyTower => 4,
                BuildingKind.Dome => 5,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            return buildingIcons[index];
        }
    }
}
