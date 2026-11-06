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
        [SerializeField,Tooltip("깊이를 기록하는 고체 유닛 본체의 머티리얼입니다. 유령과 파편은 기본 머티리얼을 사용합니다.")] private Material solidUnitBody;
        [SerializeField,Tooltip("입자 예산이 충분할 때 실제 입자의 흐름으로 본체를 구성합니다. 유령과 밀집 시에는 3D 메시를 사용합니다.")] private bool particleBody;
        [SerializeField,Tooltip("이동 흔적·공격·피격·소멸 파티클 머티리얼입니다.")] private Material particles;
        [SerializeField,Tooltip("원소의 광학 특성에 맞는 몸체 메시입니다.")] private Mesh mesh;
        [SerializeField,Tooltip("확정된 피격·소멸에서 재사용하는 작은 물질 파편입니다. 본체 메시와 분리해 입자 비용을 제한합니다.")] private Mesh fragmentMesh;
        [SerializeField,ColorUsage(true,true),Tooltip("주요 입자 색입니다.")] private Color tint=Color.white;
        [SerializeField,Range(0.2f,4),Tooltip("몸체 표시 크기 배율입니다.")] private float size=1;
        [SerializeField,Range(0,4),Tooltip("보조 입자 방출량 배율입니다.")] private float emission=1;
        [SerializeField,Min(0.1f),Tooltip("입자 표시 수명(초)입니다.")] private float lifetime=0.65f;
        [Header("본체 동작")]
        [SerializeField,Range(0,.3f),Tooltip("대기 중 부유 높이(m)입니다.")] private float hoverAmplitude=.06f;
        [SerializeField,Min(.1f),Tooltip("맥동과 보조 입자의 움직임 주파수입니다.")] private float motionFrequency=1.7f;
        [SerializeField,Range(-360,360),Tooltip("대기 중 회전 속도(도/초)입니다.")] private float spinSpeed=20;
        [SerializeField,Range(0,.25f),Tooltip("본체가 숨 쉬듯 늘어나는 비율입니다.")] private float pulseAmplitude=.06f;
        [SerializeField,Range(0,.4f),Tooltip("공격 시 압축과 늘어남의 크기입니다.")] private float attackRecoil=.18f;
        [SerializeField,Min(.05f),Tooltip("공격 반동과 피격 반응의 지속 시간(초)입니다.")] private float attackDuration=.22f;
        [SerializeField,Min(1),Tooltip("확정 공격을 보여 주는 투사체 속도(m/초)입니다. 피해 판정과 무관합니다.")] private float projectileSpeed=24;
        [SerializeField,Range(2,24),Tooltip("본체 하나에 사용하는 목표 입자 수입니다. 밀집도에 따라 줄어듭니다.")] private int bodyParticleCount=8;
        [SerializeField,Range(.18f,.3f),Tooltip("셰이더의 가로 변형을 위해 표시 반경에 예약하는 메시 단위 여백입니다.")] private float deformationPadding=.18f;
        [SerializeField,Range(0,1.6f),Tooltip("이동할 때 진행 방향으로 늘어나는 비율입니다. 최대 표시 반경은 유지합니다.")] private float movementStretch=.8f;
        [SerializeField,Range(0,1.5f),Tooltip("이동 중 마지막 일부 입자가 뒤로 남는 최대 거리(m)입니다. 본체 반경 밖의 순수 표현 꼬리이며 게임 판정에는 사용하지 않습니다.")] private float particleTrailLength=.65f;
        [SerializeField,Range(.2f,2),Tooltip("몸체 입자 크기 배율입니다. 공격·피격 입자에는 적용하지 않습니다.")] private float bodyParticleSize=1;
        [SerializeField,Range(0,1),Tooltip("흐르는 본체 입자 하나의 불투명도 배율입니다.")] private float bodyParticleOpacity=.65f;
        [SerializeField,Range(1,16),Tooltip("원본 입자 플립북의 가로 프레임 수입니다.")] private int particleColumns=1;
        [SerializeField,Range(1,16),Tooltip("원본 입자 플립북의 세로 프레임 수입니다.")] private int particleRows=1;
        [SerializeField,Range(.1f,4),Tooltip("입자 한 프레임의 가로/세로 비율입니다. 불 플립북은 12×6 셀이므로 0.5입니다.")] private float particleAspect=1;
        public string ElementId=>elementId;
        public int Style=>style;
        public Material Body=>body;
        public Material UnitBody=>solidUnitBody!=null?solidUnitBody:body;
        public bool ParticleBody=>particleBody;
        public Material Particles=>particles;
        public Mesh Mesh=>mesh;
        public Mesh FragmentMesh=>fragmentMesh;
        public Color Tint=>tint;
        public float Size=>size;
        public float Emission=>emission;
        public float Lifetime=>lifetime;
        public float HoverAmplitude=>hoverAmplitude;
        public float MotionFrequency=>motionFrequency;
        public float SpinSpeed=>spinSpeed;
        public float PulseAmplitude=>pulseAmplitude;
        public float AttackRecoil=>attackRecoil;
        public float AttackDuration=>attackDuration;
        public float ProjectileSpeed=>projectileSpeed;
        public int BodyParticleCount=>bodyParticleCount;
        public float DeformationPadding=>deformationPadding;
        public float MovementStretch=>movementStretch;
        public float ParticleTrailLength=>particleTrailLength;
        public float BodyParticleSize=>bodyParticleSize;
        public float BodyParticleOpacity=>bodyParticleOpacity;
        public int ParticleColumns=>particleColumns;
        public int ParticleRows=>particleRows;
        public float ParticleAspect=>particleAspect;
    }
}
