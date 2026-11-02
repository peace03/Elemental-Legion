using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    /// <summary>전투 확률과 무관한 별도 표현 비교 씬이다. 네트워크와 게임 판정을 시작하지 않는다.</summary>
    public sealed class EffectGallery : MonoBehaviour
    {
        [SerializeField,Tooltip("14원소·6시너지 비교 표현입니다.")] private ElementalVisuals visuals;
        [SerializeField,Range(14,988),Tooltip("렌더링 부하 비교용 유닛 수입니다.")] private int units=14;
        [SerializeField,Min(0.2f),Tooltip("공격·피격·전향·소멸의 순환 간격(초)입니다.")] private float interval=2;
        private float next;private int stage;
        private void OnEnable(){if(visuals!=null)visuals.Initialize();}
        private void Update()
        {
            if(visuals==null)return;bool emit=Time.time>=next;
            if(emit){next=Time.time+interval;stage++;if(units==14&&stage%3==0)for(int element=0;element<14;element++)visuals.GalleryOutcome(element,stage%2==0);}
            visuals.RenderGallery(units,Time.time,emit);
        }
    }
}
