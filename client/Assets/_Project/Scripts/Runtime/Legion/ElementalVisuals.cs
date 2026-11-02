using System;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace ElementalLegion.Client.Legion
{
    /// <summary>원소별 인스턴스 배치와 제한된 VFX 풀을 소유한다. 피해 계산이나 사망 추정은 하지 않는다.</summary>
    [ExecuteAlways]
    public sealed class ElementalVisuals : MonoBehaviour
    {
        [SerializeField,Tooltip("공유 데이터 순서의 14종 표현입니다.")] private ElementArtProfile[] profiles;
        [SerializeField,Tooltip("아군·적·중립 소유자 링 머티리얼입니다.")] private Material[] ownership;
        [SerializeField,Tooltip("서버 반경을 표시하는 바닥 링 메시입니다.")] private Mesh ring;
        [SerializeField,Tooltip("핵·시설 보조 메시입니다.")] private Mesh crystal;
        [SerializeField,Tooltip("6시너지의 구분된 효과 머티리얼입니다.")] private Material[] reactions;
        [SerializeField,Tooltip("확정 사건의 효과음 풀입니다.")] private CombatAudio audioPool;
        [SerializeField,Tooltip("이 세션의 월드 표현을 볼 Base 카메라입니다.")] private UnityEngine.Camera targetCamera;
        [SerializeField,Tooltip("체력 표시 비율을 읽을 공유 JSON 원본입니다.")] private TextAsset gameData;
        [SerializeField,Tooltip("관측된 체력 막대 메시입니다.")] private Mesh healthBar;
        [SerializeField,Min(1),Tooltip("일회 VFX 개수 상한입니다. 중요 전향·사망에 일부를 예약합니다.")] private int maxEffects=256;
        [SerializeField,Min(64),Tooltip("동시 보조 입자 상한입니다.")] private int maxParticles=32768;
        [SerializeField,Range(0,1),Tooltip("중요 소멸·전향에 예약하는 풀 비율입니다.")] private float criticalReserve=0.25f;
        [SerializeField,Min(0.1f),Tooltip("기본 몸체 크기(m)입니다.")] private float bodySize=1.6f;
        [SerializeField,Min(0),Tooltip("몸체 중심 높이(m)입니다.")] private float bodyHeight=1.1f;
        [SerializeField,Min(0.01f),Tooltip("이동 흔적 방출 간격(초)입니다.")] private float trailInterval=0.12f;
        private Matrix4x4[][] matrices;
        private readonly int[] counts=new int[20];
        private readonly Matrix4x4[] draw=new Matrix4x4[1023];
        private Effect[] pool;
        private GameObject poolRoot;
        private ParticleSystem[] bodyLayers;
        private ParticleSystemRenderer[] bodyRenderers;
        private readonly ParticleSystem.Particle[] bodyParticles=new ParticleSystem.Particle[Protocol.MaxUnits*8];
        private int bodyParticleCount;
        private GameplayFrame observation;
        private float nextTrail;
        private int lightIndex,crystalIndex,lightningIndex,iceIndex;
        private MaterialPropertyBlock effectProperties;
        private float Now=>(float)Time.realtimeSinceStartupAsDouble;
        public int ActiveEffects {get;private set;}
        private int auxiliaryParticles;
        private AnimationCurve particleFrames;
        public int ActiveParticles=>auxiliaryParticles+bodyParticleCount;
        public int DecorationDrops {get;private set;}
        public int PeakEffects {get;private set;}
        public int MaxEffects=>maxEffects;
        public UnityEngine.Camera TargetCamera=>targetCamera;
        private bool renderingSubscribed;
        private float[] maxHealth;
        private int waterIndex,fireIndex,smokeIndex,windIndex,earthIndex,decayIndex,magnetismIndex;
        private sealed class Effect
        {
            public ParticleSystem System;public ParticleSystemRenderer Renderer;public float Until,Started,Lifetime;public bool Critical,Aggregated;public ObservedEntityId Entity;public int Reaction=-1;public Vector3 Position;
        }
        public void Initialize()
        {
            if(effectProperties==null)effectProperties=new MaterialPropertyBlock();
            if(particleFrames==null)particleFrames=AnimationCurve.Linear(0,0,1,1);
            if(profiles==null||profiles.Length!=14||ownership==null||ownership.Length!=3||reactions==null||reactions.Length!=6||ring==null||crystal==null||audioPool==null||targetCamera==null||gameData==null||healthBar==null)
                throw new InvalidOperationException("ElementalVisuals 필수 참조가 없습니다.");
            foreach(var profile in profiles)if(profile==null||profile.Body==null||profile.Mesh==null||profile.Particles==null)throw new InvalidOperationException("원소 표현 참조가 없습니다.");
            lightIndex=Array.FindIndex(profiles,profile=>profile.ElementId=="light");crystalIndex=Array.FindIndex(profiles,profile=>profile.ElementId=="crystal");
            lightningIndex=Array.FindIndex(profiles,profile=>profile.ElementId=="lightning");iceIndex=Array.FindIndex(profiles,profile=>profile.ElementId=="ice");
            if(lightIndex<0||crystalIndex<0)throw new InvalidOperationException("빛·결정 프로필이 없습니다.");
            if(pool!=null)return;
            var rules=new GameRules(new GameDataDocument(gameData.text));maxHealth=new float[rules.Elements.Count];for(int index=0;index<maxHealth.Length;index++)maxHealth[index]=rules.Elements[index].Hp;
            waterIndex=rules.ElementIndex("water");fireIndex=rules.ElementIndex("fire");smokeIndex=rules.ElementIndex("smoke");windIndex=rules.ElementIndex("wind");earthIndex=rules.ElementIndex("earth");decayIndex=rules.ElementIndex("decay");magnetismIndex=rules.ElementIndex("magnetism");
            matrices=new Matrix4x4[20][];for(int index=0;index<matrices.Length;index++)matrices[index]=new Matrix4x4[index<14?Protocol.MaxUnits*2:index<=16?Protocol.MaxUnits*2+GameplayCodec.MaxBuildings:Protocol.MaxUnits];
            maxEffects=Mathf.Clamp(maxEffects,1,1024);maxParticles=Mathf.Max(64,maxParticles);
            poolRoot=new GameObject("Combat VFX Pool");poolRoot.transform.SetParent(transform,false);pool=new Effect[maxEffects];
            for(int index=0;index<pool.Length;index++)
            {
                var go=new GameObject("Effect "+index);go.transform.SetParent(poolRoot.transform,false);
                var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var main=ps.main;main.playOnAwake=false;main.loop=false;main.simulationSpace=ParticleSystemSimulationSpace.World;main.maxParticles=256;main.useUnscaledTime=false;
                var emission=ps.emission;emission.enabled=false;
                var renderer=ps.GetComponent<ParticleSystemRenderer>();renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
                var color=ps.colorOverLifetime;color.enabled=true;color.color=new ParticleSystem.MinMaxGradient(new Gradient{colorKeys=new[]{new GradientColorKey(Color.white,0),new GradientColorKey(Color.white,1)},alphaKeys=new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(1,0.08f),new GradientAlphaKey(0.7f,0.5f),new GradientAlphaKey(0,1)}});
                pool[index]=new Effect{System=ps,Renderer=renderer};
            }
            audioPool.Initialize();
            bodyLayers=new ParticleSystem[profiles.Length];bodyRenderers=new ParticleSystemRenderer[profiles.Length];
            for(int index=0;index<profiles.Length;index++)
            {
                var go=new GameObject(profiles[index].ElementId+" Body Particles");go.transform.SetParent(poolRoot.transform,false);var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var main=ps.main;main.playOnAwake=false;main.loop=false;main.simulationSpace=ParticleSystemSimulationSpace.World;main.maxParticles=bodyParticles.Length;main.startSpeed=0;var emission=ps.emission;emission.enabled=false;
                var renderer=ps.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=profiles[index].Particles;renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
                if(profiles[index].Style==1||profiles[index].Style==3){var sheet=ps.textureSheetAnimation;sheet.enabled=true;sheet.numTilesX=8;sheet.numTilesY=8;sheet.frameOverTime=new ParticleSystem.MinMaxCurve(1,particleFrames);}
                bodyLayers[index]=ps;bodyRenderers[index]=renderer;
            }
            if(!renderingSubscribed){RenderPipelineManager.beginCameraRendering+=BeginCamera;renderingSubscribed=true;}
        }
        public void Observe(GameplayFrame frame){observation=frame;}
        public void RenderGallery(int unitCount,float seconds,bool emit)
        {
            if(pool==null)Initialize();Array.Clear(counts,0,counts.Length);MeasurePool();
            int columns=unitCount<=14?7:38;float spacing=unitCount<=14?6:1.45f;
            for(int index=0;index<unitCount;index++)
            {
                int element=index%profiles.Length;var profile=profiles[element];Vector3 position=transform.position+new Vector3((index%columns-(columns-1)*0.5f)*spacing,bodyHeight,8-index/columns*spacing);
                matrices[element][counts[element]++]=Matrix4x4.TRS(position,Quaternion.Euler(0,index*137.5f,0),Vector3.one*bodySize*profile.Size);
                matrices[14+index%3][counts[14+index%3]++]=Matrix4x4.TRS(position-Vector3.up*(bodyHeight-0.1f),Quaternion.identity,Vector3.one*1.15f);
                if(emit)Emit(profile,position,24,false,new ObservedEntityId(ObservedEntityKind.Unit,index+1),Vector3.up*0.6f,profile.Lifetime);
            }
            for(int element=0;element<profiles.Length;element++)DrawBatch(profiles[element].Mesh,profiles[element].Body,element);
            DrawBodyParticles(seconds);
            for(int relation=0;relation<3;relation++)DrawBatch(ring,ownership[relation],14+relation);
            if(unitCount<=14)for(int reaction=0;reaction<6;reaction++)
            {
                Vector3 point=transform.position+new Vector3((reaction-2.5f)*6,0.7f,-7);
                DrawOne(ring,ownership[2],Matrix4x4.TRS(point-Vector3.up*0.6f,Quaternion.identity,Vector3.one*1.8f));
                if(emit)Emit(profiles[lightIndex],point,72,false,default,Vector3.up*(reaction==5?2:0.5f),1.5f,reactions[reaction],reaction:reaction);
            }
            DrawEffects();
        }
        public void GalleryOutcome(int element,bool conversion)
        {
            if(pool==null)Initialize();Vector3 point=transform.position+new Vector3((element%7-3)*6,bodyHeight,8-element/7*6);
            Emit(profiles[element],point,96,true,default,Vector3.up,1.1f,null,conversion);
        }
        public void SimulatePreview(float seconds){if(pool!=null)foreach(Effect effect in pool)effect.System.Simulate(seconds,false,false);}
        public void Render(SnapshotTimeline timeline,int owner,System.Numerics.Vector2 predicted,double tick)
        {
            if(pool==null)return;Array.Clear(counts,0,counts.Length);MeasurePool();
            var offset=predicted-timeline.SampleAnchor(tick);bool trail=Now>=nextTrail;if(trail)nextTrail=Now+trailInterval;
            for(int index=0;index<timeline.Count;index++)
            {
                NetworkUnit unit=timeline.GetLatest(index);if(unit.Element>=profiles.Length||!timeline.TrySample(unit.Id,tick,out var location))continue;
                if(unit.OwnerId==owner&&!unit.Garrison)location+=offset;
                Vector3 position=new Vector3(location.X,bodyHeight,location.Y);ElementArtProfile profile=profiles[unit.Element];
                float scale=bodySize*profile.Size;float angle=(unit.Id*137.508f)%360;
                matrices[unit.Element][counts[unit.Element]++]=Matrix4x4.TRS(position,Quaternion.Euler(0,angle,0),new Vector3(scale,scale*(profile.Style==1?1.8f:1.15f),scale));
                int relation=unit.OwnerId==owner?0:unit.OwnerId<0?2:1;
                matrices[14+relation][counts[14+relation]++]=Matrix4x4.TRS(new Vector3(location.X,0.12f,location.Y),Quaternion.identity,Vector3.one*1.1f);
                if(unit.Shield>0)matrices[14+relation][counts[14+relation]++]=Matrix4x4.TRS(position,Quaternion.Euler(72,Time.time*25,0),Vector3.one*1.2f);
                if(unit.Health<maxHealth[unit.Element])
                {
                    float ratio=Mathf.Clamp01(unit.Health/maxHealth[unit.Element]);
                    matrices[19][counts[19]++]=Matrix4x4.TRS(position+new Vector3((ratio-1)*0.6f,1,-0.8f),Quaternion.identity,new Vector3(ratio*1.2f,0.055f,0.11f));
                }
                if((unit.StatusMask&(1<<4))!=0)matrices[iceIndex][counts[iceIndex]++]=Matrix4x4.TRS(position,Quaternion.Euler(0,angle,0),Vector3.one*bodySize*1.15f);
                if(unit.Shield>0)matrices[magnetismIndex][counts[magnetismIndex]++]=Matrix4x4.TRS(position,Quaternion.identity,Vector3.one*bodySize*1.2f);
                if(trail&&(unit.StatusMask!=0||profile.Emission>0))Emit(unit.StatusMask!=0?profiles[StatusProfile(unit.StatusMask)]:profile,position,unit.StatusMask!=0?10:3,false,new ObservedEntityId(ObservedEntityKind.Unit,unit.Id),Vector3.up*0.3f,0.45f);
            }
            if(observation!=null&&observation.SpatialReady)
            {
                foreach(BuildingView building in observation.Buildings)
                {
                    int relation=building.OwnerId==owner?0:1;Vector3 center=new Vector3(building.Position.X,0.08f,building.Position.Y);
                    matrices[14+relation][counts[14+relation]++]=Matrix4x4.TRS(center,Quaternion.identity,Vector3.one*building.Radius);
                    matrices[18][counts[18]++]=Matrix4x4.TRS(center+Vector3.up*(building.Kind==BuildingKind.Base?1.8f:1.2f),Quaternion.Euler(0,(int)building.Kind*45,0),new Vector3(building.Radius*0.7f,building.Kind==BuildingKind.Base?3.6f:2.4f,building.Radius*0.7f));
                }
                foreach(CoreView core in observation.Cores)matrices[17][counts[17]++]=Matrix4x4.TRS(new Vector3(core.Position.X,0.7f+Mathf.Sin(Time.time*2+core.Id)*0.1f,core.Position.Y),Quaternion.Euler(0,Time.time*40,15),Vector3.one*0.42f);
                foreach(AreaView area in observation.Areas)
                {
                    // 지속 영역은 최신 관측에 있는 동안만 그린다. 재관측 때 동일 ID로 복구된다.
                    DrawOne(ring,reactions[(int)area.Kind],Matrix4x4.TRS(new Vector3(area.Position.X,0.15f,area.Position.Y),Quaternion.identity,Vector3.one*area.Radius));
                }
            }
            for(int element=0;element<14;element++)DrawBatch(profiles[element].Mesh,profiles[element].Body,element);
            DrawBodyParticles(Now);
            for(int relation=0;relation<3;relation++)DrawBatch(ring,ownership[relation],14+relation);
            DrawBatch(crystal,profiles[lightIndex].Body,17);DrawBatch(crystal,profiles[crystalIndex].Body,18);
            DrawBatch(healthBar,ownership[0],19);
            DrawEffects();
        }
        public void Play(NetworkEvent entry)
        {
            SimEvent data=entry.Data;if(data.Kind==SimEventKind.CommandRejected)return;
            ElementArtProfile profile=profiles[data.Element>=0&&data.Element<profiles.Length?data.Element:lightIndex];
            if(data.Kind==SimEventKind.Converted)profile=profiles[lightIndex];
            Vector3 point=new Vector3(data.Position.X,bodyHeight,data.Position.Y);bool critical=data.Kind==SimEventKind.Death||data.Kind==SimEventKind.Converted;
            int amount=data.Kind==SimEventKind.Converted?96:critical?64:data.Kind==SimEventKind.Reaction?80:data.Kind==SimEventKind.Impact?8:24;
            Vector3 velocity=data.Kind==SimEventKind.Converted?Vector3.zero:Vector3.up*(critical?1.4f:0.6f);
            var entity=new ObservedEntityId(data.EntityKind,data.EntityId);
            Material material=data.Kind==SimEventKind.Reaction&&data.Detail>=0&&data.Detail<6?reactions[data.Detail]:null;
            Emit(profile,point,amount,critical,entity,velocity,data.Kind==SimEventKind.Converted?1.1f:profile.Lifetime,material,data.Kind==SimEventKind.Converted,reaction:data.Kind==SimEventKind.Reaction?data.Detail:-1);
            if(data.Kind==SimEventKind.Attack&&data.HasSourcePosition)
            {
                Vector3 start=new Vector3(data.SourcePosition.X,bodyHeight,data.SourcePosition.Y);Vector3 direction=point-start;
                // 서버 피해는 적용된 상태다. 투사체는 짧은 흔적만 표현한다.
                int segments=Mathf.Clamp(Mathf.CeilToInt(direction.magnitude*3),3,48);
                Emit(profile,point,segments,false,entity,Vector3.zero,0.16f,lineOrigin:start);
            }
            audioPool.Play(data.Kind,point,entity);
        }
        private void Emit(ElementArtProfile profile,Vector3 point,int amount,bool critical,ObservedEntityId entity,Vector3 velocity,float lifetime,Material material=null,bool accrete=false,int reaction=-1,Vector3? lineOrigin=null)
        {
            if(!critical){amount=Mathf.RoundToInt(amount*profile.Emission);if(amount<=0)return;}
            if(pool==null)return;int limit=critical?pool.Length:Mathf.FloorToInt(pool.Length*(1-criticalReserve));Effect effect=null;
            for(int index=0;index<limit;index++)if(pool[index].Until<=Now){effect=pool[index];break;}
            if(effect==null&&critical)
                foreach(Effect candidate in pool)if(!candidate.Critical){candidate.System.Clear();effect=candidate;break;}
            bool append=effect==null&&critical;
            if(append){effect=pool[pool.Length-1];amount=1;material=profiles[lightIndex].Particles;effect.Aggregated=true;var main=effect.System.main;main.maxParticles=maxParticles;}
            if(effect==null||!critical&&ActiveParticles+amount>maxParticles){DecorationDrops++;return;}
            if(critical&&ActiveParticles+amount>maxParticles)
            {
                foreach(Effect candidate in pool)if(!candidate.Critical&&candidate.System.particleCount>0){auxiliaryParticles-=candidate.System.particleCount;candidate.System.Clear();candidate.Until=0;if(ActiveParticles+amount<=maxParticles)break;}
                if(ActiveParticles+amount>maxParticles)foreach(var layer in bodyLayers){bodyParticleCount-=layer.particleCount;layer.Clear();if(ActiveParticles+amount<=maxParticles)break;}
                if(ActiveParticles>=maxParticles)
                {auxiliaryParticles-=effect.System.particleCount;effect.System.Clear();append=false;}
                amount=Mathf.Min(amount,Mathf.Max(1,maxParticles-ActiveParticles));
            }
            if(!append){auxiliaryParticles-=effect.System.particleCount;effect.System.Clear();effect.Aggregated=false;}
            effect.Renderer.sharedMaterial=material!=null?material:profile.Particles;effect.Entity=entity;effect.Critical=critical;effect.Started=Now;effect.Lifetime=lifetime;effect.Until=Now+lifetime;effect.Reaction=reaction;effect.Position=point;
            var sheet=effect.System.textureSheetAnimation;sheet.enabled=material==null&&(profile.Style==1||profile.Style==3);if(sheet.enabled){sheet.numTilesX=8;sheet.numTilesY=8;sheet.frameOverTime=new ParticleSystem.MinMaxCurve(1,particleFrames);}
            effect.System.Play();var parameters=new ParticleSystem.EmitParams{position=point,startColor=material!=null?Color.white:profile.Tint,startLifetime=lifetime,startSize=accrete?0.18f:critical?0.65f:0.25f,velocity=velocity};
            for(int index=0;index<amount;index++)
            {
                // 반복 가능한 표시 노이즈이며 게임 판정의 난수와 공유하지 않는다.
                float angle=index*2.399963f;float radius=accrete?1.7f:Mathf.Sqrt((index+0.5f)/amount)*0.45f;
                Vector3 offset=new Vector3(Mathf.Cos(angle),Mathf.Sin(angle*1.7f)*0.5f,Mathf.Sin(angle))*radius;
                if(reaction==2){offset*=2;parameters.startSize=1.2f;velocity=Vector3.up*1.6f;}
                if(reaction==3){offset*=3;velocity=Vector3.up;parameters.startSize=0.7f;}
                if(reaction==4){float orbit=index/(float)amount*Mathf.PI*5;offset=new Vector3(Mathf.Cos(orbit),index/(float)amount,Mathf.Sin(orbit))*1.6f;velocity=new Vector3(-offset.z,0.3f,offset.x)*2;parameters.startSize=0.3f;}
                if(reaction==5){offset=offset.normalized*0.5f;velocity=offset*6;parameters.startSize=0.55f;}
                parameters.position=point+offset;parameters.velocity=accrete?-offset/lifetime:velocity+offset*(critical?2:0.8f);
                if(lineOrigin.HasValue){parameters.position=Vector3.Lerp(lineOrigin.Value,point,index/(float)amount)+offset*0.1f;parameters.velocity=Vector3.zero;}
                if(reaction<0)parameters.startSize=accrete?0.14f:0.18f+0.6f*((index*17%31)/31f);effect.System.Emit(parameters,1);
            }
            auxiliaryParticles+=amount;
        }
        private void DrawEffects()
        {
            foreach(Effect effect in pool)
            {
                if(effect.Until<=Now||effect.Reaction<0)continue;float phase=Mathf.Clamp01((Now-effect.Started)/effect.Lifetime);int profile=effect.Reaction==0?lightningIndex:effect.Reaction==1?iceIndex:effect.Reaction==5?lightIndex:-1;
                if(profile<0)continue;float size=effect.Reaction==5?0.7f+phase*3:effect.Reaction==1?1.8f:1.2f;
                effectProperties.Clear();effectProperties.SetFloat("_Opacity",1-phase);if(effect.Reaction==5)effectProperties.SetColor("_BaseColor",new Color(0.7f,0.25f,1));
                Graphics.DrawMesh(profiles[profile].Mesh,Matrix4x4.TRS(effect.Position,Quaternion.Euler(0,phase*90,0),Vector3.one*size),profiles[profile].Body,gameObject.layer,targetCamera,0,effectProperties,ShadowCastingMode.Off,false);
            }
        }
        private void DrawBodyParticles(float seconds)
        {
            bodyParticleCount=0;
            for(int element=0;element<profiles.Length;element++)
            {
                var profile=profiles[element];int perUnit=profile.Style==1?7:profile.Style==3?6:profile.Style==0?4:profile.Style==6?4:profile.Style==2?3:2;int written=0;
                for(int unit=0;unit<counts[element];unit++)for(int index=0;index<perUnit;index++)
                {
                    if(auxiliaryParticles+bodyParticleCount+written>=maxParticles)break;
                    float phase=Mathf.Repeat(seconds*(profile.Style==1?0.75f:0.4f)+index/(float)perUnit+unit*0.618f,1);float angle=(index*2.39996f+seconds+unit);
                    Vector3 center=matrices[element][unit].GetColumn(3);float radius=profile.Style==0?0.65f:profile.Style==3?0.35f+phase*0.5f:0.35f;
                    Vector3 offset=new Vector3(Mathf.Cos(angle)*radius,(phase-0.3f)*(profile.Style==1?2:profile.Style==3?2.5f:0.6f),Mathf.Sin(angle)*radius);
                    ref ParticleSystem.Particle particle=ref bodyParticles[written++];particle.position=center+offset;particle.startColor=profile.Style==3?new Color(0.48f,0.5f,0.53f,0.5f):profile.Style==1?new Color(1,0.55f,0.12f,0.8f):new Color(profile.Tint.r,profile.Tint.g,profile.Tint.b,0.65f);
                    particle.startSize=(profile.Style==1?1.6f:profile.Style==3?1.4f+phase:profile.Style==0?0.25f:0.13f)*profile.Size;particle.startLifetime=1;particle.remainingLifetime=1-phase;particle.randomSeed=(uint)(unit*17+index+1);
                }
                if(!bodyLayers[element].isPlaying)bodyLayers[element].Play();bodyLayers[element].SetParticles(bodyParticles,written);bodyParticleCount+=written;
            }
        }
        private int StatusProfile(ushort mask)
        {
            if((mask&((1<<4)|(1<<1)))!=0)return iceIndex;if((mask&(1<<2))!=0)return fireIndex;if((mask&(1<<3))!=0)return decayIndex;
            if((mask&((1<<6)|(1<<8)))!=0)return smokeIndex;if((mask&((1<<5)|(1<<9)))!=0)return earthIndex;if((mask&(1<<7))!=0)return windIndex;return waterIndex;
        }
        private void MeasurePool(){ActiveEffects=auxiliaryParticles=0;foreach(Effect effect in pool){if(effect.Until>Now)ActiveEffects++;auxiliaryParticles+=effect.System.particleCount;}PeakEffects=Mathf.Max(PeakEffects,ActiveEffects);}
        private void DrawBatch(Mesh mesh,Material material,int group)
        {
            for(int offset=0;offset<counts[group];offset+=draw.Length)
            {int count=Math.Min(draw.Length,counts[group]-offset);Array.Copy(matrices[group],offset,draw,0,count);Graphics.DrawMeshInstanced(mesh,0,material,draw,count,null,ShadowCastingMode.Off,false,gameObject.layer,targetCamera);}
        }
        private void DrawOne(Mesh mesh,Material material,Matrix4x4 matrix)=>Graphics.DrawMesh(mesh,matrix,material,gameObject.layer,targetCamera,0,null,ShadowCastingMode.Off,false);
        private void BeginCamera(ScriptableRenderContext context,UnityEngine.Camera camera)
        {
            if(pool==null)return;bool hidden=camera!=targetCamera;
            foreach(Effect effect in pool)effect.Renderer.forceRenderingOff=hidden;
            foreach(var renderer in bodyRenderers)renderer.forceRenderingOff=hidden;
        }
        public void Forget(ObservedEntityId entity)
        {
            if(pool==null)return;foreach(Effect effect in pool)if(effect.Aggregated||effect.Entity.Equals(entity)){effect.System.Clear();effect.Until=0;}
            audioPool.Forget(entity);
        }
        public void Clear()
        {
            observation=null;if(bodyLayers!=null)foreach(var layer in bodyLayers)if(layer!=null)layer.Clear();if(pool!=null)foreach(Effect effect in pool){effect.System.Clear();effect.Until=0;}
            if(audioPool!=null)audioPool.Clear();ActiveEffects=auxiliaryParticles=bodyParticleCount=0;
        }
        private void OnDisable(){if(renderingSubscribed){RenderPipelineManager.beginCameraRendering-=BeginCamera;renderingSubscribed=false;}Clear();if(poolRoot!=null){if(Application.isPlaying)Destroy(poolRoot);else DestroyImmediate(poolRoot);}poolRoot=null;pool=null;bodyLayers=null;bodyRenderers=null;matrices=null;}
    }
}
