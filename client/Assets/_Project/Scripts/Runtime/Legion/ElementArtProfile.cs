using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    /// <summary>원소의 표현만 조절한다. 피해·시야·속도 등 서버 수치는 포함하지 않는다.</summary>
    [CreateAssetMenu(menuName="Elemental Legion/Element Art")]
    public sealed class ElementArtProfile : ScriptableObject
    {
        [SerializeField,Tooltip("공유 데이터의 원소 ID입니다.")] private string elementId;
        [SerializeField,Range(0,13),Tooltip("광학 표현 유형입니다. 게임 원소 인덱스와 구분합니다.")] private int style;
        [SerializeField,Tooltip("URP 인스턴싱 몸체 머티리얼입니다.")] private Material body;
        [SerializeField,Tooltip("이동 흔적·공격·피격·소멸 파티클 머티리얼입니다.")] private Material particles;
        [SerializeField,Tooltip("원소의 광학 특성에 맞는 몸체 메시입니다.")] private Mesh mesh;
        [SerializeField,ColorUsage(true,true),Tooltip("주요 입자 색입니다.")] private Color tint=Color.white;
        [SerializeField,Range(0.2f,4),Tooltip("몸체 표시 크기 배율입니다.")] private float size=1;
        [SerializeField,Range(0,4),Tooltip("보조 입자 방출량 배율입니다.")] private float emission=1;
        [SerializeField,Min(0.1f),Tooltip("입자 표시 수명(초)입니다.")] private float lifetime=0.65f;
        public string ElementId=>elementId;
        public int Style=>style;
        public Material Body=>body;
        public Material Particles=>particles;
        public Mesh Mesh=>mesh;
        public Color Tint=>tint;
        public float Size=>size;
        public float Emission=>emission;
        public float Lifetime=>lifetime;
    }
}
