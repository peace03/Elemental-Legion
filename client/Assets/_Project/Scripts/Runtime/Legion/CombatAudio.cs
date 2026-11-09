using System;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Net;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    /// <summary>허용된 사건만 재생하는 세션별 음성 풀과 Master/SFX/UI 볼륨 버스다.</summary>
    [ExecuteAlways]
    public sealed class CombatAudio : MonoBehaviour
    {
        [SerializeField,Tooltip("공격·피격·전향·사망·양성·UI·회복 순서의 효과음입니다.")] private AudioClip[] clips;
        [SerializeField,Range(1,128),Tooltip("동시 음성 수 상한입니다.")] private int maxVoices=32;
        [SerializeField,Range(0,1),Tooltip("전체 음량입니다.")] private float master=0.7f;
        [SerializeField,Range(0,1),Tooltip("월드 효과음 음량입니다.")] private float sfx=0.7f;
        [SerializeField,Range(0,1),Tooltip("UI 효과음 음량입니다.")] private float ui=0.5f;
        [SerializeField,Tooltip("전체 음소거입니다.")] private bool masterMute;
        [SerializeField,Tooltip("효과음 음소거입니다.")] private bool sfxMute;
        [SerializeField,Tooltip("UI 음소거입니다.")] private bool uiMute;
        [SerializeField,Min(1),Tooltip("월드 효과음 최대 거리(m)입니다. 게임 시야와 무관합니다.")] private float maxDistance=65;
        private AudioSource[] sources;
        private bool[] userInterface;
        private ObservedEntityId[] entities;
        public int ActiveVoices {get;private set;}
        public int DroppedVoices {get;private set;}
        public int MaxVoices=>maxVoices;
        public float Master=>master;public float Sfx=>sfx;public float Ui=>ui;
        public void Initialize()
        {
            if(clips==null||clips.Length!=7)throw new InvalidOperationException("CombatAudio 효과음 참조가 없습니다.");
            foreach(var clip in clips)if(clip==null)throw new InvalidOperationException("효과음 파일이 없습니다.");
            if(sources!=null)return;sources=new AudioSource[Mathf.Clamp(maxVoices,1,128)];userInterface=new bool[sources.Length];entities=new ObservedEntityId[sources.Length];
            for(int index=0;index<sources.Length;index++)
            {
                var go=new GameObject("Combat Voice "+index);go.transform.SetParent(transform,false);var source=go.AddComponent<AudioSource>();source.playOnAwake=false;source.loop=false;
                source.rolloffMode=AudioRolloffMode.Linear;source.minDistance=8;source.maxDistance=maxDistance;source.dopplerLevel=0;sources[index]=source;
            }
        }
        public void Volumes(float masterValue,float sfxValue,float uiValue,bool muteMaster,bool muteSfx,bool muteUi)
        {master=Mathf.Clamp01(masterValue);sfx=Mathf.Clamp01(sfxValue);ui=Mathf.Clamp01(uiValue);masterMute=muteMaster;sfxMute=muteSfx;uiMute=muteUi;UpdateVolumes();}
        public void Play(SimEventKind kind,Vector3 point,ObservedEntityId entity=default)
        {
            int clip=kind==SimEventKind.Attack?0:kind==SimEventKind.Converted?2:kind==SimEventKind.Death?3:kind==SimEventKind.Trained?4:kind==SimEventKind.Healed||kind==SimEventKind.ShieldPulse?6:1;
            PlayClip(clip,point,false,entity);
        }
        public void Click()=>PlayClip(5,Vector3.zero,true);
        /// <summary>공격자의 위치와 무관한 2D 피격 효과음을 UI 음량으로 재생한다.</summary>
        internal void DefenseAlert()=>PlayClip(1,Vector3.zero,true);
        private void PlayClip(int clip,Vector3 point,bool isUi,ObservedEntityId entity=default)
        {
            if(sources==null||masterMute||isUi&&uiMute||!isUi&&sfxMute)return;
            for(int index=0;index<sources.Length;index++)if(!sources[index].isPlaying)
            {
                AudioSource source=sources[index];userInterface[index]=isUi;entities[index]=entity;source.transform.position=point;source.spatialBlend=isUi?0:1;source.clip=clips[clip];
                source.volume=master*(isUi?ui:sfx);source.pitch=clip==2?1.35f:clip==3?0.8f:1;source.Play();return;
            }
            DroppedVoices++;
        }
        private void UpdateVolumes(){if(sources==null)return;for(int index=0;index<sources.Length;index++){bool muted=masterMute||(userInterface[index]?uiMute:sfxMute);sources[index].volume=muted?0:master*(userInterface[index]?ui:sfx);}}
        private void Update(){ActiveVoices=0;if(sources!=null)foreach(var source in sources)if(source.isPlaying)ActiveVoices++;}
        public void Clear(){if(sources!=null)foreach(var source in sources)source.Stop();ActiveVoices=0;}
        public void Forget(ObservedEntityId entity){if(sources!=null)for(int index=0;index<sources.Length;index++)if(entities[index].Equals(entity))sources[index].Stop();}
        private void OnDisable(){Clear();if(sources!=null)foreach(var source in sources)if(source!=null){if(Application.isPlaying)Destroy(source.gameObject);else DestroyImmediate(source.gameObject);}sources=null;}
    }
}
