using ElementalLegion.Client.Legion;
using UnityEngine;

namespace ElementalLegion.Client.UI
{
    public sealed class CombatAudioControls : MonoBehaviour
    {
        [SerializeField,Tooltip("세션의 음향 풀입니다.")] private CombatAudio audioPool;
        [SerializeField,Tooltip("전체·SFX·UI 음량 슬라이더입니다.")] private UnityEngine.UI.Slider[] volumes;
        [SerializeField,Tooltip("전체·SFX·UI 음소거입니다.")] private UnityEngine.UI.Toggle[] mutes;
        private void OnEnable()
        {
            if(audioPool==null||volumes.Length!=3||mutes.Length!=3){Debug.LogError("음량 설정 참조가 없습니다.",this);return;}
            volumes[0].SetValueWithoutNotify(audioPool.Master);volumes[1].SetValueWithoutNotify(audioPool.Sfx);volumes[2].SetValueWithoutNotify(audioPool.Ui);
            foreach(var slider in volumes)slider.onValueChanged.AddListener(Change);foreach(var toggle in mutes)toggle.onValueChanged.AddListener(ChangeMute);
        }
        private void OnDisable(){foreach(var slider in volumes)if(slider!=null)slider.onValueChanged.RemoveListener(Change);foreach(var toggle in mutes)if(toggle!=null)toggle.onValueChanged.RemoveListener(ChangeMute);}
        private void Change(float value)=>Apply();private void ChangeMute(bool value)=>Apply();
        private void Apply()=>audioPool.Volumes(volumes[0].value,volumes[1].value,volumes[2].value,mutes[0].isOn,mutes[1].isOn,mutes[2].isOn);
    }
}
