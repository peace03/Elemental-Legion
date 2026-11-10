using System;
using System.Collections.Generic;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace ElementalLegion.Client.Legion
{
    /// <summary>원소별 인스턴스 배치와 제한된 VFX 풀을 소유한다. 피해 계산이나 사망 추정은 하지 않는다.</summary>
    [ExecuteAlways]
    public sealed partial class ElementalVisuals : MonoBehaviour
    {
        [SerializeField,Tooltip("공유 데이터 순서의 14종 표현입니다.")] private ElementArtProfile[] profiles;
        [SerializeField,Tooltip("아군·적·중립 소유자 링 머티리얼입니다.")] private Material[] ownership;
        [SerializeField,Tooltip("서버 반경을 표시하는 바닥 링 메시입니다.")] private Mesh ring;
        [SerializeField,Tooltip("핵·시설 보조 메시입니다.")] private Mesh crystal;
        [SerializeField,Tooltip("물 원소의 외형과 분리한 자기권 구형 메시입니다.")] private Mesh domeMesh;
        [SerializeField,Tooltip("6시너지의 구분된 효과 머티리얼입니다.")] private Material[] reactions;
        [SerializeField,Tooltip("확정 사건의 효과음 풀입니다.")] private CombatAudio audioPool;
        [SerializeField,Tooltip("이 세션의 월드 표현을 볼 Base 카메라입니다.")] private UnityEngine.Camera targetCamera;
        [SerializeField,Tooltip("체력 표시 비율을 읽을 공유 JSON 원본입니다.")] private TextAsset gameData;
        [SerializeField,Tooltip("관측된 체력 막대 메시입니다.")] private Mesh healthBar;
        [SerializeField,Tooltip("부유 원소 아래의 부드러운 접지 그림자입니다.")] private Material groundShadow;
        [SerializeField,Tooltip("원소 본체를 덧그리지 않는 보호막·빙결 전용 외피 재질입니다.")] private Material statusShell;
        [SerializeField,Min(1),Tooltip("일회 VFX 개수 상한입니다. 전향·핵 드롭·사망에 일부를 예약합니다.")] private int maxEffects=256;
        [SerializeField,Min(64),Tooltip("본체와 전투 효과에 사용하는 동시 입자 상한입니다.")] private int maxParticles=32768;
        [SerializeField,Range(0,1),Tooltip("전향·핵 드롭·사망에 예약하는 풀 비율입니다.")] private float criticalReserve=0.25f;
        [SerializeField,Min(0.1f),Tooltip("기본 몸체 크기(m)입니다.")] private float bodySize=1.9f;
        [SerializeField,Min(0.1f),Tooltip("전장 원소 몸체의 최대 가로 표시 반경(m)입니다.")] private float unitFootprintRadius=1f;
        [SerializeField,Min(0),Tooltip("몸체 중심 높이(m)입니다.")] private float bodyHeight=1.1f;
        [SerializeField,Min(0.01f),Tooltip("이동 흔적 방출 간격(초)입니다.")] private float trailInterval=0.12f;
        [SerializeField,Range(0,1),Tooltip("유령 원소와 기지의 불투명도입니다.")] private float ghostOpacity=0.25f;
        [SerializeField,Range(0,1),Tooltip("건설 미리보기의 불투명도입니다.")] private float previewOpacity=0.4f;
        [SerializeField,Range(0,1),Tooltip("건설 중인 시설의 불투명도입니다.")] private float unfinishedOpacity=0.48f;
        [SerializeField,Min(0.1f),Tooltip("타워 받침과 원소 장식을 합친 높이(m)입니다.")] private float towerHeight=4f;
        [SerializeField, Range(.1f, 2), Tooltip("현재 관측 중인 전향 후보 결정의 표시 크기(m)입니다.")] private float candidateSize = .7f;
        private const int GhostUnits=20, GhostBuildings=34, GhostRings=35, Domes=36, Projectiles=37;
        private const int StatusIce=OwnershipStart+OwnershipKinds*(PlayerPalette.Count+1),StatusShield=StatusIce+1;
        private static readonly int Opacity=Shader.PropertyToID("_Opacity"), ElementStyle=Shader.PropertyToID("_Element");
        private static readonly int CoreOpacity=Shader.PropertyToID("_CoreOpacity");
        private static readonly int BaseColor=Shader.PropertyToID("_BaseColor"), SecondColor=Shader.PropertyToID("_SecondColor");
        private static readonly int OrbitSpeed=Shader.PropertyToID("_OrbitSpeed");
        private static readonly List<ParticleSystemVertexStream> effectStreams=new List<ParticleSystemVertexStream>
        {ParticleSystemVertexStream.Position,ParticleSystemVertexStream.Normal,ParticleSystemVertexStream.Tangent,ParticleSystemVertexStream.Color,ParticleSystemVertexStream.UV};
        private Matrix4x4[][] matrices;
        private int[][] bodyUnitIds;
        private byte[][] bodySquadIds;
        private float[][] bodyFootprints;
        private readonly Bounds[] squadScreenBounds = new Bounds[4];
        private readonly bool[] squadHasBounds = new bool[4];
        private const int Candidates = StatusShield + 1;
        private const int UnitShadows = Candidates + 1;
        private readonly int[] counts=new int[UnitShadows+1];
        private readonly Matrix4x4[] draw=new Matrix4x4[1023];
        private Effect[] pool;
        private GameObject poolRoot;
        private ParticleSystem[] bodyLayers;
        private ParticleSystemRenderer[] bodyRenderers;
        private readonly ParticleSystem.Particle[] bodyParticles=new ParticleSystem.Particle[Protocol.MaxUnits*8];
        private int bodyParticleCount;
        private GameplayFrame observation;
        private byte selectedSquads = 7;
        internal byte SelectedSquads => selectedSquads;
        /// <summary>자기 분대 1~4의 선택 비트를 표시하며 예측 대상과는 별도로 유지한다.</summary>
        public void SetSelectedSquads(byte mask) => selectedSquads = (byte)(mask & 15);
        [SerializeField, Range(.5f, 8f), Tooltip("자기 분대 중심 고리의 반경(m)입니다.")] private float squadRingRadius = 3f;
        [SerializeField, Range(.05f, .8f), Tooltip("분대 번호를 나타내는 마름모의 크기와 몸체 위 여백(m)입니다.")] private float squadMarkSize = .4f;
        [SerializeField, Range(2f, 20f), Tooltip("양자 이동 중인 분대와 도착 지점에 표시하는 고리의 높이(m)입니다.")] private float squadIndicatorHeight = 8f;
        [SerializeField, Range(.1f, 3f), Tooltip("양자 이동 성공 후 본진 고리가 펼쳐지는 시간(초)입니다.")] private float recallArrivalDuration = .8f;
        private long recallTick = -1, recallEnd, recallReady, recallStart;
        private int recallPlayer = -1;
        private byte recallSquad;
        private float arrivalUntil;
        private Vector3 arrivalPosition;
        private float nextTrail;
        private int lightIndex,crystalIndex,lightningIndex,iceIndex;
        private MaterialPropertyBlock effectProperties;
        private MaterialPropertyBlock ghostProperties, previewProperties, towerPreviewProperties, unfinishedProperties, domeProperties;
        private MaterialPropertyBlock particleFallbackProperties, particleGhostProperties;
        private MaterialPropertyBlock frozenShellProperties;
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
        private void OnValidate() { candidateSize = Mathf.Clamp(candidateSize, .1f, 2); squadRingRadius = Mathf.Clamp(squadRingRadius, .5f, 8); squadMarkSize = Mathf.Clamp(squadMarkSize, .05f, .8f); squadIndicatorHeight = Mathf.Clamp(squadIndicatorHeight, 2, 20); recallArrivalDuration = Mathf.Clamp(recallArrivalDuration, .1f, 3); }
#if UNITY_EDITOR
        internal static Action<ElementalVisuals, List<UnityEngine.Camera>> CollectSceneViewCameras { get; set; }
        private readonly List<UnityEngine.Camera> sceneViewCameras = new List<UnityEngine.Camera>();
#endif
        private float[] maxHealth, bodyRadii;
        private float unitFootprint;
        private float unitRingRadius;
        private int waterIndex,fireIndex,smokeIndex,windIndex,earthIndex,decayIndex,magnetismIndex;
        private sealed class Effect
        {
            public ParticleSystem System;public ParticleSystemRenderer Renderer;public float Until,Started,Lifetime;public bool Critical,Aggregated;public ObservedEntityId Entity;public int Reaction=-1;public Vector3 Position;
            public ObservedEntityId Source;
            public SimEventKind? Cue;
            public int Element;
            public bool Projectile;
            public Vector3 From;
        }
        public void Initialize()
        {
            if(effectProperties==null)effectProperties=new MaterialPropertyBlock();
            if(particleFrames==null)particleFrames=AnimationCurve.Linear(0,0,1,1);
            if(profiles==null||profiles.Length!=14||ownership==null||ownership.Length!=3||reactions==null||reactions.Length!=6||ring==null||crystal==null||domeMesh==null||statusShell==null||audioPool==null||targetCamera==null||gameData==null||healthBar==null)
                throw new InvalidOperationException("ElementalVisuals 필수 참조가 없습니다.");
            foreach(var profile in profiles)if(profile==null||profile.Body==null||profile.Mesh==null||profile.Particles==null)throw new InvalidOperationException("원소 표현 참조가 없습니다.");
            lightIndex=Array.FindIndex(profiles,profile=>profile.ElementId=="light");crystalIndex=Array.FindIndex(profiles,profile=>profile.ElementId=="crystal");
            lightningIndex=Array.FindIndex(profiles,profile=>profile.ElementId=="lightning");iceIndex=Array.FindIndex(profiles,profile=>profile.ElementId=="ice");
            if(lightIndex<0||crystalIndex<0)throw new InvalidOperationException("빛·결정 프로필이 없습니다.");
            if(pool!=null)return;
            var rules=new GameRules(new GameDataDocument(gameData.text));maxHealth=new float[rules.Elements.Count];for(int index=0;index<maxHealth.Length;index++)maxHealth[index]=rules.Elements[index].Hp;
            waterIndex=rules.ElementIndex("water");fireIndex=rules.ElementIndex("fire");smokeIndex=rules.ElementIndex("smoke");windIndex=rules.ElementIndex("wind");earthIndex=rules.ElementIndex("earth");decayIndex=rules.ElementIndex("decay");magnetismIndex=rules.ElementIndex("magnetism");
            unitFootprint = Mathf.Max(0.1f, unitFootprintRadius);
            // 몸체가 커져도 바닥의 소유자 링은 인접 유닛과 구분돼야 한다.
            unitRingRadius = Mathf.Min(unitFootprint, rules.MinimumSeparation * 0.5f);
            bodyRadii = new float[profiles.Length];
            for (int index = 0; index < profiles.Length; index++)
            {
                // 셰이더의 최대 가로 변형보다 작은 여백은 허용하지 않는다.
                bodyRadii[index] = HorizontalRadius(profiles[index].Mesh) + Mathf.Max(.18f, profiles[index].DeformationPadding);
            }
            ghostProperties = Spectral(ghostOpacity);
            // 입자를 그리지 않을 때는 가느다란 보조 메시가 본체 역할을 한다.
            particleFallbackProperties = new MaterialPropertyBlock();
            particleFallbackProperties.SetFloat(CoreOpacity, 1);
            particleGhostProperties = Spectral(ghostOpacity);
            particleGhostProperties.SetFloat(CoreOpacity, 1);
            previewProperties = Spectral(previewOpacity);
            previewProperties.SetFloat("_OwnershipTint", 1);
            towerPreviewProperties = Spectral(previewOpacity);
            unfinishedProperties = Spectral(unfinishedOpacity);
            domeProperties = Spectral(ghostOpacity * 0.5f);
            frozenShellProperties = new MaterialPropertyBlock();
            frozenShellProperties.SetFloat("_Frost", 1);
            matrices = new Matrix4x4[counts.Length][];
            bodyUnitIds = new int[profiles.Length][];
            bodySquadIds = new byte[profiles.Length][];
            bodyFootprints = new float[profiles.Length][];
            for (int index = 0; index < matrices.Length; index++)
            {
                int capacity = index < 14 ? Protocol.MaxUnits * 2 : Protocol.MaxUnits;
                if (index >= 14 && index <= 16) capacity = Protocol.MaxUnits * 2 + GameplayCodec.MaxBuildings;
                else if (index == 19 || index == GhostRings) capacity += GameplayCodec.MaxBuildings;
                else if (index == GhostBuildings || index == Domes) capacity = GameplayCodec.MaxBuildings;
                else if (index == StatusIce || index == StatusShield) capacity = Protocol.MaxUnits;
                else if (index == Candidates) capacity = GameplayCodec.MaxObjects;
                else if (index == UnitShadows) capacity = Protocol.MaxUnits;
                else if (index >= OwnershipStart) capacity = 128;
                else if (index >= Projectiles) capacity = Mathf.Clamp(maxEffects, 1, 1024);
                matrices[index] = new Matrix4x4[capacity];
                if (index < profiles.Length)
                {
                    bodyUnitIds[index] = new int[capacity];
                    bodySquadIds[index] = new byte[capacity];
                    bodyFootprints[index] = new float[capacity];
                }
            }
            InitializeOwnership();
            InitializeFacilities();
            maxEffects=Mathf.Clamp(maxEffects,1,1024);maxParticles=Mathf.Max(64,maxParticles);
            poolRoot=new GameObject("Combat VFX Pool");poolRoot.transform.SetParent(transform,false);pool=new Effect[maxEffects];
            for(int index=0;index<pool.Length;index++)
            {
                var go=new GameObject("Effect "+index);go.transform.SetParent(poolRoot.transform,false);
                var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var main=ps.main;main.playOnAwake=false;main.loop=false;main.simulationSpace=ParticleSystemSimulationSpace.World;main.maxParticles=256;main.useUnscaledTime=false;
                var emission=ps.emission;emission.enabled=false;
                var renderer=ps.GetComponent<ParticleSystemRenderer>();renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
                renderer.SetActiveVertexStreams(effectStreams);
                var color=ps.colorOverLifetime;color.enabled=true;color.color=new ParticleSystem.MinMaxGradient(new Gradient{colorKeys=new[]{new GradientColorKey(Color.white,0),new GradientColorKey(Color.white,1)},alphaKeys=new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(1,0.08f),new GradientAlphaKey(0.7f,0.5f),new GradientAlphaKey(0,1)}});
                pool[index]=new Effect{System=ps,Renderer=renderer};
            }
            InitializeGlow();
            audioPool.Initialize();
            bodyLayers=new ParticleSystem[profiles.Length];bodyRenderers=new ParticleSystemRenderer[profiles.Length];
            for(int index=0;index<profiles.Length;index++)
            {
                var go=new GameObject(profiles[index].ElementId+" Body Particles");go.transform.SetParent(poolRoot.transform,false);var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var main=ps.main;main.playOnAwake=false;main.loop=false;main.simulationSpace=ParticleSystemSimulationSpace.World;main.maxParticles=bodyParticles.Length;main.startSpeed=0;var emission=ps.emission;emission.enabled=false;
                var profile = profiles[index];
                main.startSize3D = profile.ParticleBody || Mathf.Abs(profile.ParticleAspect - 1) > .001f;
                var renderer=ps.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=profiles[index].Particles;renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
                ConfigureParticleSheet(ps,profiles[index],profiles[index].ParticleColumns>1&&profiles[index].ParticleRows>1);
                ConfigureBodyParticleRenderer(renderer, profiles[index]);
                bodyLayers[index]=ps;bodyRenderers[index]=renderer;
            }
            InitializeLocalLights();
            if(!renderingSubscribed){RenderPipelineManager.beginCameraRendering+=BeginCamera;renderingSubscribed=true;}
        }
        internal static bool IsRecallSuccess(long previousTick, byte previousSquad, long end, long previousReady, long tick, byte squad, long ready) =>
            previousSquad > 0 && squad == 0 && tick > previousTick && tick >= end && ready > tick && ready > previousReady;

        public void Observe(GameplayFrame frame)
        {
            observation=frame;
            if (frame == null) { ResetRecall(); return; }
            ownerColors.Observe(frame);
            if (frame.PlayerId != recallPlayer || frame.Tick < recallTick) ResetRecall();
            if (IsRecallSuccess(recallTick, recallSquad, recallEnd, recallReady, frame.Tick, frame.RecallSquad, frame.RecallReadyTick))
            {
                arrivalPosition = new Vector3(frame.BasePosition.X, .16f, frame.BasePosition.Y);
                arrivalUntil = Now + recallArrivalDuration;
            }
            if (frame.RecallSquad != 0 && (recallSquad != frame.RecallSquad || recallEnd != frame.RecallEndTick)) recallStart = frame.Tick;
            recallPlayer = frame.PlayerId; recallTick = frame.Tick; recallSquad = frame.RecallSquad;
            recallEnd = frame.RecallEndTick; recallReady = frame.RecallReadyTick;
        }

        private void ResetRecall() { recallTick = -1; recallPlayer = -1; recallSquad = 0; recallEnd = recallReady = recallStart = 0; arrivalUntil = 0; }
        public void RenderGallery(int unitCount,float seconds,bool emit)
        {
            if(pool==null)Initialize();
#if UNITY_EDITOR
            RefreshSceneViewCameras();
#endif
            Array.Clear(counts,0,counts.Length);MeasurePool();
            int columns=unitCount<=14?7:38;float spacing=unitCount<=14?6:1.45f;
            float footprint = unitCount <= 14 ? 1.4f : unitFootprint;
            for(int index=0;index<unitCount;index++)
            {
                int element=index%profiles.Length;var profile=profiles[element];Vector3 position=transform.position+new Vector3((index%columns-(columns-1)*0.5f)*spacing,bodyHeight,8-index/columns*spacing);
                float cycle = Mathf.Repeat(seconds, 12);
                Vector3 velocity = unitCount <= 14 && cycle >= 2 && cycle < 4 ? Vector3.right * 3 : Vector3.zero;
                if (velocity != Vector3.zero) position.x += Mathf.Sin(seconds * 3 + index) * .35f;
                float attack = unitCount <= 14 && cycle >= 4 && cycle < 6 ? Mathf.Max(0, Mathf.Sin(seconds * 9)) : 0;
                float hit = unitCount <= 14 && cycle >= 6 && cycle < 8 ? Mathf.Max(0, Mathf.Sin(seconds * 8)) : 0;
                float conversion = unitCount <= 14 && cycle >= 8 && cycle < 10 ? Mathf.Max(0, Mathf.Sin(seconds * 6)) : 0;
                AddBodyPose(element, index + 1, BodyPose(element, index + 1, position, velocity, seconds,
                    footprint, attack, hit, conversion), footprint);
                matrices[14+index%3][counts[14+index%3]++]=Matrix4x4.TRS(position-Vector3.up*(bodyHeight-0.1f),Quaternion.identity,Vector3.one*1.15f);
                if(emit)
                {
                    var entity = new ObservedEntityId(ObservedEntityKind.Unit,index+1);
                    Emit(profile,position,24,false,entity,Vector3.up*0.6f,profile.Lifetime);
                    if (attack > 0) Emit(profile, position + Vector3.right * 2, 14, false, entity, Vector3.zero, .2f,
                        lineOrigin:position, cue:SimEventKind.Attack, source:entity);
                }
            }
            DrawBodyParticles(seconds);
            DrawBodyMeshes();
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
            if (conversion) EmitOutcome(SimEventKind.Converted, point, default, default, PlayerColors.Get(element % PlayerPalette.Count));
            else Emit(profiles[element],point,96,true,default,Vector3.up,1.1f,cue:SimEventKind.Death);
        }
        public void SimulatePreview(float seconds){if(pool!=null)foreach(Effect effect in pool)effect.System.Simulate(seconds,false,false);}
        public void Render(SnapshotTimeline timeline,int owner,System.Numerics.Vector2 predicted,double tick,byte selectedSquad=1)
        {
            if(pool==null)return;
#if UNITY_EDITOR
            RefreshSceneViewCameras();
#endif
            Array.Clear(counts,0,counts.Length);MeasurePool();
            Array.Clear(squadHasBounds, 0, squadHasBounds.Length);
            motionVersion++;
            float seconds = Now;
            var offset=predicted-timeline.SampleSquadAnchor(selectedSquad,tick);bool trail=Now>=nextTrail;if(trail)nextTrail=Now+trailInterval;
            int displayCount = CollectDisplayUnits(timeline, owner, offset, tick, selectedSquad);
            float minimumFootprint = unitFootprint * Mathf.Clamp(crowdedFootprintMinimum, .2f, 1f);
            for (int index = 0; index < displayCount; index++)
            {
                ref DisplayUnit item = ref displayUnits[index];
                NetworkUnit unit = item.Unit;
                var location = item.Position;
                float footprint = FitDisplayFootprint(index, minimumFootprint);
                Vector3 position=new Vector3(location.X,bodyHeight,location.Y);ElementArtProfile profile=profiles[unit.Element];
                MotionState motion = unit.Ghost ? default : TrackMotion(unit.Id, unit.Element, position, seconds);
                Matrix4x4 pose = unit.Ghost ? BodyPose(unit.Element, unit.Id, position, Vector3.zero, 0, footprint) :
                    RuntimeBodyPose(unit.Element, unit.Id, position, motion, seconds, footprint,
                        Pulse(seconds, motion.AttackAt, profile.AttackDuration), Pulse(seconds, motion.HitAt, profile.AttackDuration),
                        Pulse(seconds, motion.ConversionAt, .55f));
                AddBodyPose(unit.Element, unit.Id, pose, footprint, unit.Ghost,
                    unit.OwnerId == owner && !unit.IsReinforcing ? unit.SquadId : (byte)0);
                AddUnitOwnership(unit.OwnerId, unit.Ghost,
                    Matrix4x4.TRS(new Vector3(location.X, .12f, location.Y), Quaternion.identity, Vector3.one * Mathf.Min(unitRingRadius, footprint)));
                if (!unit.Ghost && groundShadow != null)
                    matrices[UnitShadows][counts[UnitShadows]++] = Matrix4x4.TRS(new Vector3(location.X, .025f, location.Y), Quaternion.identity, Vector3.one * (footprint * .85f));
                if(unit.Health<maxHealth[unit.Element])
                {
                    float ratio=Mathf.Clamp01(unit.Health/maxHealth[unit.Element]);
                    matrices[19][counts[19]++]=Matrix4x4.TRS(position+new Vector3((ratio-1)*0.6f,1,-0.8f),Quaternion.identity,new Vector3(ratio*1.2f,0.055f,0.11f));
                }
                if (unit.Ghost) continue;
                if((unit.StatusMask&(1<<4))!=0)matrices[StatusIce][counts[StatusIce]++]=StatusShellPose(unit.Element,pose,footprint);
                if(unit.Shield>0)matrices[StatusShield][counts[StatusShield]++]=StatusShellPose(unit.Element,pose,footprint);
                if(trail&&(unit.StatusMask!=0||profile.Emission>0&&motion.Velocity.sqrMagnitude>.04f))
                    Emit(unit.StatusMask!=0?profiles[StatusProfile(unit.StatusMask)]:profile,position,unit.StatusMask!=0?10:3,false,
                        new ObservedEntityId(ObservedEntityKind.Unit,unit.Id),-motion.Velocity*.15f+Vector3.up*.2f,.4f);
            }
            CullMotion();
            UpdateLocalLights(displayCount);
            if(observation!=null&&observation.SpatialReady)
            {
                foreach(BuildingView building in observation.Buildings) RenderBuilding(building, owner);
                foreach(CoreView core in observation.Cores)matrices[17][counts[17]++]=Matrix4x4.TRS(new Vector3(core.Position.X,0.7f+Mathf.Sin(Time.time*2+core.Id)*0.1f,core.Position.Y),Quaternion.Euler(0,Time.time*40,15),Vector3.one*0.42f);
                foreach (ConversionCandidateView candidate in observation.Candidates)
                {
                    if (candidate.EndTick <= observation.Tick) continue;
                    float size = Mathf.Clamp(candidateSize, .1f, 2);
                    matrices[Candidates][counts[Candidates]++] = Matrix4x4.TRS(
                        new Vector3(candidate.Position.X, size * .5f, candidate.Position.Y),
                        Quaternion.Euler(0, 45, 0), new Vector3(size, size * .35f, size));
                }
                foreach(AreaView area in observation.Areas)
                {
                    // 지속 영역은 최신 관측에 있는 동안만 그린다. 재관측 때 동일 ID로 복구된다.
                    DrawOne(ring,reactions[(int)area.Kind],Matrix4x4.TRS(new Vector3(area.Position.X,0.15f,area.Position.Y),Quaternion.identity,Vector3.one*area.Radius));
                }
            }
            DrawBodyParticles(seconds);
            if (groundShadow != null) DrawBatch(constructionRangeFill, groundShadow, UnitShadows);
            DrawBodyMeshes();
            // 전체 얼음·자기장 본체를 재사용하면 한 유닛이 여러 원소처럼 보인다.
            DrawBatch(domeMesh,statusShell,StatusIce,frozenShellProperties);
            DrawBatch(domeMesh,statusShell,StatusShield);
            DrawBatch(crystal,profiles[lightIndex].Body,17);
            DrawBatch(crystal, profiles[crystalIndex].Body, Candidates, unfinishedProperties);
            DrawOwnership();
            DrawSquads(owner);
            DrawBatch(healthBar,ownership[0],19);
            DrawEffects();
        }

        private void AddBodyPose(int element, int unitId, Matrix4x4 pose, float footprint, bool ghost = false, byte squadId = 0)
        {
            int group = ghost ? GhostUnits + element : element;
            int slot = counts[group]++;
            matrices[group][slot] = pose;
            if (!ghost)
            {
                bodyUnitIds[element][slot] = unitId;
                bodySquadIds[element][slot] = squadId;
                bodyFootprints[element][slot] = footprint;
            }
        }

        private void DrawBodyMeshes()
        {
            for (int element = 0; element < profiles.Length; element++)
            {
                var profile = profiles[element];
                bool particleFallback = profile.ParticleBody && !particleBodiesEnabled;
                // 바람의 가는 흐름이 먼지 입자에 묻히지 않도록 소용돌이 면도 함께 그린다.
                if (!profile.ParticleBody || particleFallback || element == windIndex)
                {
                    DrawBatch(profile.Mesh, profile.UnitBody, element, particleFallback ? particleFallbackProperties : null);
                    for (int unit = 0; unit < counts[element]; unit++)
                    {
                        byte squad = bodySquadIds[element][unit];
                        if (squad >= 1 && squad <= 4)
                            IncludeSquadBounds(squad, ProjectedMeshBounds(profile.Mesh, matrices[element][unit]));
                    }
                }
                var ghost = profile.ParticleBody ? particleGhostProperties : ghostProperties;
                DrawBatch(profile.Mesh, profile.Body, GhostUnits + element, ghost);
            }
        }

        private void RenderBuilding(BuildingView building, int owner)
        {
            bool ghost = building.GhostEndTick > 0;
            Vector3 center = new Vector3(building.Position.X, 0.08f, building.Position.Y);
            Matrix4x4 footprint = Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * building.Radius);
            MaterialPropertyBlock translucent = ghost ? ghostProperties : building.IsUnderConstruction ? unfinishedProperties : null;
            int slot = ownerColors.Slot(building.OwnerId);
            MaterialPropertyBlock tint = ownershipProperties[slot][ghost ? OwnerGhostBuilding : building.IsUnderConstruction ? OwnerUnfinished : OwnerBuilding];
            if (building.IsUnderConstruction && !ghost) DrawOne(ring, profiles[lightIndex].Body, footprint, ownershipProperties[slot][OwnerUnfinished]);
            else AddOwnership(building.OwnerId, ghost || building.Kind == BuildingKind.Dome ? OwnerGhostRing : OwnerRing, footprint);
            Matrix4x4 model = BuildingModel(building.Kind, center, building.Radius);
            if (building.Kind == BuildingKind.Dome)
            {
                if (building.IsUnderConstruction) DrawOne(domeMesh, profiles[lightIndex].Body, model, tint);
                else AddOwnership(building.OwnerId, OwnerDome, model);
            }
            else if (DrawFacility(building.Kind, center, building.Radius, facilityProperties[slot][ghost ? 1 : building.IsUnderConstruction ? 2 : 0], !ghost && !building.IsUnderConstruction))
                DrawFacilityCore(building, center, ghost);
            else if (IsTower(building.Kind) && ValidElement(building.TowerElement))
                RenderTower(building.TowerElement, center, building.Radius, translucent, tint);
            else if (building.IsUnderConstruction && !ghost) DrawOne(crystal, profiles[crystalIndex].Body, model, tint);
            else AddOwnership(building.OwnerId, ghost ? OwnerGhostBuilding : OwnerBuilding, model);
            if (building.MaxHealth <= 0) return;
            float ratio = Mathf.Clamp01(building.Health / building.MaxHealth);
            float width = Mathf.Clamp(building.Radius, 1.8f, 5);
            float height = FacilityHeight(building.Kind, building.Radius) + .35f;
            Vector3 offset = new Vector3((ratio-1)*width*0.5f, height, -building.Radius*0.55f);
            matrices[19][counts[19]++] = Matrix4x4.TRS(center+offset, Quaternion.identity, new Vector3(ratio*width, 0.12f, 0.16f));
        }

        internal void RenderConstructionPreview(BuildingKind kind, int element, Vector3 center, float radius, float towerRange, bool available)
        {
            if (previewProperties == null) return;
            Material body = profiles[lightIndex].Body;
            Color tint = available ? new Color(0.35f, 1, 0.2f) : new Color(1, 0.4f, 0.32f);
            previewProperties.SetColor(SecondColor, Color.black);
            previewProperties.SetColor(BaseColor, tint);
            center.y = 0.13f;
            DrawConstructionOutline(center, radius, tint);
            facilityPreview.SetColor(TeamColor, tint);
            if (DrawFacility(kind, center, radius, facilityPreview, false))
            {
                if (IsTower(kind)) DrawConstructionOutline(center, towerRange, tint);
                return;
            }
            if (IsTower(kind) && ValidElement(element))
            {
                DrawConstructionOutline(center, towerRange, tint);
                RenderTower(element, center, radius, towerPreviewProperties, previewProperties);
            }
            else DrawOne(kind == BuildingKind.Dome ? domeMesh : crystal, body,
                    BuildingModel(kind, center, radius), previewProperties);
        }

        private static bool IsTower(BuildingKind kind) => kind == BuildingKind.NatureTower || kind == BuildingKind.EnergyTower;
        private bool ValidElement(int element) => element >= 0 && element < profiles.Length;
        private float TowerStandHeight => towerHeight * 0.45f;
        private float TowerHeadHeight => (towerHeight + TowerStandHeight) * 0.5f;

        internal void RenderTowerRange(BuildingView tower, float range)
        {
            Vector3 center = new Vector3(tower.Position.X, 0.13f, tower.Position.Y);
            DrawOne(constructionRangeOutline, ownership[0], Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * range), ownershipProperties[ownerColors.Slot(tower.OwnerId)][OwnerRing]);
        }

        private void RenderTower(int element, Vector3 center, float radius, MaterialPropertyBlock translucent, MaterialPropertyBlock standProperties = null)
        {
            float standHeight = TowerStandHeight;
            var stand = Matrix4x4.TRS(center + Vector3.up * (standHeight * 0.5f), Quaternion.identity,
                new Vector3(radius * 0.9f, standHeight, radius * 0.9f));
            DrawOne(crystal, profiles[crystalIndex].Body, stand, standProperties ?? translucent);

            ElementArtProfile profile = profiles[element];
            Bounds bounds = profile.Mesh.bounds;
            // 원소 프로필이 구형 메시를 공유해도 타워 꼭대기의 실루엣은 구분한다.
            Vector2 shape = profile.Style switch
            {
                7 => new Vector2(0.82f, 0.82f),
                9 => new Vector2(0.82f, 1.15f),
                10 => new Vector2(1.05f, 0.92f),
                11 => new Vector2(1.32f, 0.42f),
                12 => new Vector2(1.12f, 0.65f),
                13 => new Vector2(0.63f, 1.15f),
                _ => Vector2.one
            };
            float horizontal = radius * 0.62f * shape.x / bodyRadii[element];
            float vertical = (towerHeight - standHeight) * 0.85f * shape.y / bounds.size.y;
            var scale = new Vector3(horizontal, vertical, horizontal);
            Quaternion rotation = Quaternion.Euler(0, (element * 137.5f) % 360, 0);
            Vector3 focus = center + Vector3.up * TowerHeadHeight;
            Matrix4x4 ornament = Matrix4x4.TRS(focus - rotation * Vector3.Scale(bounds.center, scale), rotation, scale);
            DrawOne(profile.Mesh, profile.Body, ornament, translucent);
        }

        internal bool TryHoverBuilding(ReadOnlySpan<BuildingView> buildings, Ray ray, out BuildingView hovered)
        {
            hovered = default;
            if (ray.direction.y >= 0 || !new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float ground)) return false;
            var direction = new Vector2(ray.direction.x, ray.direction.z);
            float horizontalSquared = direction.sqrMagnitude;
            float nearest = float.PositiveInfinity;
            foreach (BuildingView building in buildings)
            {
                // 자기권과 일반 시설이 겹칠 때는 안쪽 시설의 설명을 우선한다.
                bool dome = building.Kind == BuildingKind.Dome;
                if (dome && nearest < float.PositiveInfinity && hovered.Kind != BuildingKind.Dome) continue;
                float top = Mathf.Max(0, ground + VisualBuildingHeight(building) / ray.direction.y);
                var offset = new Vector2(building.Position.X - ray.origin.x, building.Position.Y - ray.origin.z);
                float closest = horizontalSquared > 0 ? Mathf.Clamp(Vector2.Dot(offset, direction) / horizontalSquared, top, ground) : top;
                if ((offset - direction * closest).sqrMagnitude > building.Radius * building.Radius) continue;
                if (closest >= nearest && (dome || hovered.Kind != BuildingKind.Dome)) continue;
                hovered = building;
                nearest = closest;
            }
            return nearest < float.PositiveInfinity;
        }

        internal float VisualBuildingHeight(BuildingView building) => building.Kind == BuildingKind.Dome
            ? building.Radius * 0.425f : FacilityHeight(building.Kind, building.Radius);

        private static Matrix4x4 BuildingModel(BuildingKind kind, Vector3 center, float radius)
        {
            if (kind == BuildingKind.Dome)
                return Matrix4x4.TRS(center, Quaternion.identity, new Vector3(radius*2, radius*0.85f, radius*2));
            float height = BuildingHeight(kind);
            return Matrix4x4.TRS(center+Vector3.up*(height*0.5f), Quaternion.Euler(0,(int)kind*45,0),
                new Vector3(radius*0.7f,height,radius*0.7f));
        }

        private static float BuildingHeight(BuildingKind kind) => kind switch
        {
            BuildingKind.Base => 3.6f, BuildingKind.Collector => 1.1f,
            BuildingKind.EnergyForge => 3, _ => 2.4f
        };

        private static MaterialPropertyBlock Spectral(float opacity)
        {
            // 특정 원소의 셰이더 분기가 불투명도를 덮어쓰지 않는 스타일을 쓴다.
            var properties = new MaterialPropertyBlock();
            properties.SetFloat(ElementStyle, 7);
            properties.SetFloat(Opacity, opacity);
            return properties;
        }

        public void Play(NetworkEvent entry)
        {
            SimEvent data=entry.Data;if(data.Kind==SimEventKind.CommandRejected||data.Kind==SimEventKind.CommandApplied)return;
            ReactBody(data);
            Vector3 point=new Vector3(data.Position.X,bodyHeight,data.Position.Y);
            var entity=new ObservedEntityId(data.EntityKind,data.EntityId);
            var source = new ObservedEntityId(data.SourceKind, data.SourceId);
            if (IsOutcome(data.Kind))
            {
                EmitOutcome(data.Kind, point, entity, source, ownerColors.Get(data.OwnerId));
                audioPool.Play(data.Kind, point, entity);
                return;
            }
            ElementArtProfile profile=profiles[data.Element>=0&&data.Element<profiles.Length?data.Element:lightIndex];
            bool critical=data.Kind==SimEventKind.Death;
            int amount=critical?64:data.Kind==SimEventKind.Reaction?80:data.Kind==SimEventKind.Impact?8:24;
            Vector3 velocity=Vector3.up*(critical?1.4f:0.6f);
            Material material=data.Kind==SimEventKind.Reaction&&data.Detail>=0&&data.Detail<6?reactions[data.Detail]:null;
            if(data.Kind==SimEventKind.Attack&&data.HasSourcePosition)
            {
                float sourceHeight = data.SourceKind == ObservedEntityKind.Building ? BuildingAttackHeight(data.SourceId) : bodyHeight;
                Vector3 start=new Vector3(data.SourcePosition.X,sourceHeight,data.SourcePosition.Y);Vector3 direction=point-start;
                // 확정된 공격만 빠르게 재생하며 투사체 도착으로 피해를 다시 판정하지 않는다.
                float duration = Mathf.Clamp(direction.magnitude / Mathf.Max(1, profile.ProjectileSpeed), .08f, .28f);
                Emit(profile,point,Mathf.Clamp(Mathf.CeilToInt(direction.magnitude*2),8,28),false,entity,Vector3.zero,
                    duration,lineOrigin:start,cue:data.Kind,source:source);
            }
            else Emit(profile,point,amount,critical,entity,velocity,profile.Lifetime,
                material,reaction:data.Kind==SimEventKind.Reaction?data.Detail:-1,cue:data.Kind,source:source);
            audioPool.Play(data.Kind,point,entity);
        }
        private void Emit(ElementArtProfile profile,Vector3 point,int amount,bool critical,ObservedEntityId entity,Vector3 velocity,float lifetime,Material material=null,int reaction=-1,Vector3? lineOrigin=null,SimEventKind? cue=null,ObservedEntityId source=default)
        {
            if(!critical){amount=Mathf.RoundToInt(amount*profile.Emission);if(amount<=0)return;}
            Effect effect=AcquireEffect(critical,cue,ref amount,out bool append);
            if(effect==null)return;
            if(append)material=profiles[lightIndex].Particles;
            bool fragment=material==null&&reaction<0&&!lineOrigin.HasValue&&profile.FragmentMesh!=null&&
                (profile.Style==0||profile.Style==2||profile.Style==4||profile.Style==5||profile.Style==8||profile.Style==9||profile.Style==11||profile.Style==12||profile.Style==13);
            // 풀에서 다른 물질로 재사용할 때 메시·재질·중력·회전 상태를 모두 덮어쓴다.
            effect.Renderer.renderMode=fragment?ParticleSystemRenderMode.Mesh:ParticleSystemRenderMode.Billboard;
            effect.Renderer.mesh=fragment?profile.FragmentMesh:null;
            effect.Renderer.alignment=fragment?ParticleSystemRenderSpace.World:ParticleSystemRenderSpace.View;
            effect.Renderer.SetPropertyBlock(null);
            if(fragment&&profile.Style==11)
            {
                // CPU 입자 메시의 월드 위치는 공전시키지 않고 원래 입자 궤적을 따른다.
                effectProperties.Clear();effectProperties.SetFloat(OrbitSpeed,0);effect.Renderer.SetPropertyBlock(effectProperties);
            }
            effect.Renderer.sharedMaterial=material!=null?material:fragment?profile.Body:profile.Particles;
            var particleMain=effect.System.main;
            particleMain.maxParticles=append?maxParticles:256;
            particleMain.startRotation3D=fragment;
            particleMain.startSize3D=!fragment&&material==null&&Mathf.Abs(profile.ParticleAspect-1)>.001f;
            particleMain.gravityModifier=fragment?(profile.Style==0?.65f:.35f):0;
            var tumble=effect.System.rotationOverLifetime;tumble.enabled=fragment;tumble.separateAxes=true;
            tumble.x=.7f;tumble.y=1.1f;tumble.z=.4f;
            effect.Entity=entity;effect.Critical=critical;effect.Started=Now;effect.Lifetime=lifetime;effect.Until=Now+lifetime;effect.Reaction=reaction;effect.Position=point;
            effect.Source=source;effect.Cue=cue;effect.Element=Array.IndexOf(profiles,profile);effect.Projectile=lineOrigin.HasValue&&!append;
            effect.From=lineOrigin.GetValueOrDefault(point);
            ConfigureParticleSheet(effect.System,profile,material==null&&!fragment&&profile.ParticleColumns>1&&profile.ParticleRows>1);
            effect.System.Play();var parameters=new ParticleSystem.EmitParams{position=point,startColor=material!=null||fragment?Color.white:profile.Tint,startLifetime=lifetime,startSize=critical?0.65f:0.25f,velocity=velocity};
            for(int index=0;index<amount;index++)
            {
                // 반복 가능한 표시 노이즈이며 게임 판정의 난수와 공유하지 않는다.
                float angle=index*2.399963f;float radius=Mathf.Sqrt((index+0.5f)/amount)*0.45f;
                Vector3 offset=new Vector3(Mathf.Cos(angle),Mathf.Sin(angle*1.7f)*0.5f,Mathf.Sin(angle))*radius;
                if(reaction==2){offset*=2;parameters.startSize=1.2f;velocity=Vector3.up*1.6f;}
                if(reaction==3){offset*=3;velocity=Vector3.up;parameters.startSize=0.7f;}
                if(reaction==4){float orbit=index/(float)amount*Mathf.PI*5;offset=new Vector3(Mathf.Cos(orbit),index/(float)amount,Mathf.Sin(orbit))*1.6f;velocity=new Vector3(-offset.z,0.3f,offset.x)*2;parameters.startSize=0.3f;}
                if(reaction==5){offset=offset.normalized*0.5f;velocity=offset*6;parameters.startSize=0.55f;}
                parameters.position=point+offset;parameters.velocity=velocity+offset*(critical?2:0.8f);
                if(lineOrigin.HasValue)
                {
                    Vector3 direction=point-lineOrigin.Value;
                    parameters.position=lineOrigin.Value+direction*(index/(float)amount*.16f)+offset*.15f;
                    parameters.velocity=direction/lifetime;
                    parameters.startLifetime=lifetime*(1-index/(float)amount*.16f);
                }
                if(reaction<0)
                {
                    float grain=(index*17%31)/31f;
                    parameters.startSize = lineOrigin.HasValue ? .12f + grain * .16f : critical ? .15f + grain * .38f : .1f + grain * .24f;
                    if(fragment)
                    {
                        parameters.rotation3D=new Vector3(index*1.17f,index*2.39f,index*.71f);
                        parameters.startSize*=profile.Style==0?.45f:profile.Style==2||profile.Style==13?.65f:1;
                    }
                    else if(profile.Style==1||profile.Style==3)
                        parameters.startSize*=profile.Style==1?2.3f:2;
                    if(cue==SimEventKind.Death)parameters.velocity=offset*(profile.Style==3?1.2f:3.8f)+Vector3.up*.5f;
                    if(cue==SimEventKind.Impact)parameters.velocity=offset*2.5f+Vector3.up*.3f;
                    if(!fragment&&(profile.Style==1||profile.Style==3))parameters.velocity+=Vector3.up*(profile.Style==1?1.1f:.45f);
                    if(!critical&&!lineOrigin.HasValue&&cue==null)
                    {
                        float fit=Mathf.Min(1,unitFootprint/(offset.magnitude+parameters.startSize*.5f));
                        parameters.position=point+offset*fit;parameters.startSize*=fit;
                    }
                }
                if(particleMain.startSize3D)
                {
                    float height=parameters.startSize;
                    parameters.startSize3D=new Vector3(height*Mathf.Clamp(profile.ParticleAspect,.1f,4),height,height);
                }
                effect.System.Emit(parameters,1);
            }
            auxiliaryParticles+=amount;
        }
        private void DrawEffects()
        {
            foreach(Effect effect in pool)
            {
                if(effect.Until<=Now)continue;
                float phase=Mathf.Clamp01((Now-effect.Started)/effect.Lifetime);
                if(effect.Projectile&&effect.Element>=0)
                {
                    Vector3 position=Vector3.Lerp(effect.From,effect.Position,phase);
                    float arc = profiles[effect.Element].Style == 6 || profiles[effect.Element].Style == 7 ? 0 : .18f;
                    position.y+=Mathf.Sin(phase*Mathf.PI)*arc;
                    matrices[Projectiles+effect.Element][counts[Projectiles+effect.Element]++]=Matrix4x4.TRS(position,
                        Quaternion.Euler(0,phase*240,0),Vector3.one*(.22f+Mathf.Sin(phase*Mathf.PI)*.06f));
                }
                if(effect.Reaction<0)continue;
                int profile=effect.Reaction==0?lightningIndex:effect.Reaction==1?iceIndex:effect.Reaction==5?lightIndex:-1;
                if(profile<0)continue;float size=effect.Reaction==5?0.7f+phase*3:effect.Reaction==1?1.8f:1.2f;
                effectProperties.Clear();effectProperties.SetFloat("_Opacity",1-phase);if(effect.Reaction==5)effectProperties.SetColor("_BaseColor",new Color(0.7f,0.25f,1));
                var matrix = Matrix4x4.TRS(effect.Position, Quaternion.Euler(0, phase * 90, 0), Vector3.one * size);
                DrawOne(profiles[profile].Mesh, profiles[profile].Body, matrix, effectProperties);
            }
            for(int element=0;element<profiles.Length;element++)DrawBatch(profiles[element].Mesh,profiles[element].Body,Projectiles+element);
        }
        private static float HorizontalRadius(Mesh mesh)
        {
            if (!mesh.isReadable)
            {
                Vector3 bound = mesh.bounds.center;
                Vector3 extents = mesh.bounds.extents;
                return new Vector2(Mathf.Abs(bound.x) + extents.x, Mathf.Abs(bound.z) + extents.z).magnitude;
            }
            float squared = 0;
            foreach (Vector3 vertex in mesh.vertices)
                squared = Mathf.Max(squared, vertex.x * vertex.x + vertex.z * vertex.z);
            return Mathf.Sqrt(squared);
        }

        private Matrix4x4 StatusShellPose(int element, Matrix4x4 body, float footprint)
        {
            Bounds bounds = profiles[element].Mesh.bounds;
            Vector3 center = body.MultiplyPoint3x4(bounds.center);
            float height = Mathf.Max(footprint, bounds.extents.y * body.GetColumn(1).magnitude);
            Vector3 radius = domeMesh.bounds.extents;
            Vector3 scale = new Vector3(footprint / radius.x, height / radius.y, footprint / radius.z);
            return Matrix4x4.TRS(center, Quaternion.identity, scale);
        }

        private int StatusProfile(ushort mask)
        {
            if((mask&((1<<4)|(1<<1)))!=0)return iceIndex;if((mask&(1<<2))!=0)return fireIndex;if((mask&(1<<3))!=0)return decayIndex;
            if((mask&((1<<6)|(1<<8)))!=0)return smokeIndex;if((mask&((1<<5)|(1<<9)))!=0)return earthIndex;if((mask&(1<<7))!=0)return windIndex;return waterIndex;
        }
        private void MeasurePool(){ActiveEffects=auxiliaryParticles=0;foreach(Effect effect in pool){if(effect.Until>Now)ActiveEffects++;auxiliaryParticles+=effect.System.particleCount;}PeakEffects=Mathf.Max(PeakEffects,ActiveEffects);}
#if UNITY_EDITOR
        private void RefreshSceneViewCameras()
        {
            sceneViewCameras.Clear();
            CollectSceneViewCameras?.Invoke(this, sceneViewCameras);
        }
#endif

        private void DrawBatch(Mesh mesh, Material material, int group, MaterialPropertyBlock properties = null)
        {
            for (int offset = 0; offset < counts[group]; offset += draw.Length)
            {
                int count = Math.Min(draw.Length, counts[group] - offset);
                Array.Copy(matrices[group], offset, draw, 0, count);
                int layer = gameObject.layer;
                Graphics.DrawMeshInstanced(mesh, 0, material, draw, count, properties, ShadowCastingMode.Off, false, layer, targetCamera);
#if UNITY_EDITOR
                foreach (var camera in sceneViewCameras)
                    Graphics.DrawMeshInstanced(mesh, 0, material, draw, count, properties, ShadowCastingMode.Off, false, layer, camera);
#endif
            }
        }

        private void DrawOne(Mesh mesh, Material material, Matrix4x4 matrix, MaterialPropertyBlock properties = null)
        {
            int layer = gameObject.layer;
            Graphics.DrawMesh(mesh, matrix, material, layer, targetCamera, 0, properties, ShadowCastingMode.Off, false);
#if UNITY_EDITOR
            foreach (var camera in sceneViewCameras)
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, 0, properties, ShadowCastingMode.Off, false);
#endif
        }

        private void DrawSquads(int owner)
        {
            if (observation == null || observation.PlayerId != owner) return;
            int count = 0;
            foreach (SquadView squad in observation.Squads)
            {
                if (squad.Id < 1 || squad.Id > 4 || squad.JoinedCount <= 0 || recallSquad != squad.Id) continue;
                float radius = squadRingRadius;
                if (recallSquad == squad.Id && recallEnd > recallStart)
                    radius *= Mathf.Lerp(1, .2f, Mathf.Clamp01((float)(recallTick - recallStart) / (recallEnd - recallStart)));
                Vector3 center = new Vector3(squad.Position.X, squadIndicatorHeight, squad.Position.Y);
                draw[count++] = Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * radius);
            }
            if (arrivalUntil > Now)
            {
                float progress = 1 - (arrivalUntil - Now) / recallArrivalDuration;
                draw[count++] = Matrix4x4.TRS(new Vector3(arrivalPosition.x, squadIndicatorHeight, arrivalPosition.z), Quaternion.identity, Vector3.one * squadRingRadius * Mathf.Lerp(.2f, 2, progress));
            }
            DrawSquadBatch(ring, ownership[0], count);
            count = 0;
            foreach (SquadView squad in observation.Squads)
            {
                if (squad.Id < 1 || squad.Id > 4 || squad.JoinedCount <= 0 || !squadHasBounds[squad.Id - 1]) continue;
                Bounds body = squadScreenBounds[squad.Id - 1];
                Vector3 screen = new Vector3(body.center.x, body.max.y, body.center.z);
                Vector3 anchor = targetCamera.ScreenToWorldPoint(screen);
                Bounds marker = ProjectedMeshBounds(crystal, Matrix4x4.TRS(anchor, Quaternion.identity, Vector3.one * squadMarkSize));
                float gap = targetCamera.WorldToScreenPoint(anchor + targetCamera.transform.up * squadMarkSize).y - screen.y;
                screen.x += screen.x - marker.center.x;
                screen.y += screen.y - marker.min.y + gap;
                anchor = targetCamera.ScreenToWorldPoint(screen);
                for (int mark = 0; mark < squad.Id; mark++)
                {
                    float offset = (mark - (squad.Id - 1) * .5f) * squadMarkSize * 2.6f;
                    Vector3 point = anchor + targetCamera.transform.right * offset;
                    draw[count++] = Matrix4x4.TRS(point, Quaternion.identity, Vector3.one * squadMarkSize);
                }
            }
            DrawSquadBatch(crystal, profiles[lightIndex].Body, count);
        }

        // 이미 보간·예측·밀집 크기가 적용된 몸체를 화면 좌표로 합쳐 카메라 위쪽에 표식을 붙인다.
        private void IncludeSquadBounds(byte squadId, Bounds bounds)
        {
            if (squadId < 1 || squadId > 4 || bounds.max.z <= targetCamera.nearClipPlane) return;
            int index = squadId - 1;
            if (squadHasBounds[index]) squadScreenBounds[index].Encapsulate(bounds);
            else { squadScreenBounds[index] = bounds; squadHasBounds[index] = true; }
        }

        private Bounds ProjectedMeshBounds(Mesh mesh, Matrix4x4 pose)
        {
            Bounds local = mesh.bounds;
            var result = new Bounds(targetCamera.WorldToScreenPoint(pose.MultiplyPoint3x4(local.min)), Vector3.zero);
            for (int corner = 1; corner < 8; corner++)
            {
                Vector3 point = local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1,
                    (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                result.Encapsulate(targetCamera.WorldToScreenPoint(pose.MultiplyPoint3x4(point)));
            }
            return result;
        }

        private void DrawSquadBatch(Mesh mesh, Material material, int count)
        {
            if (count == 0) return;
            Graphics.DrawMeshInstanced(mesh, 0, material, draw, count, null, ShadowCastingMode.Off, false, gameObject.layer, targetCamera);
#if UNITY_EDITOR
            foreach (var camera in sceneViewCameras)
                Graphics.DrawMeshInstanced(mesh, 0, material, draw, count, null, ShadowCastingMode.Off, false, gameObject.layer, camera);
#endif
        }

        private void BeginCamera(ScriptableRenderContext context, UnityEngine.Camera camera)
        {
            if (pool == null) return;
            bool hidden = camera != targetCamera;
#if UNITY_EDITOR
            hidden = hidden && !sceneViewCameras.Contains(camera);
#endif
            foreach (Effect effect in pool) effect.Renderer.forceRenderingOff = hidden;
            foreach (var renderer in bodyRenderers) renderer.forceRenderingOff = hidden;
        }
        public void Forget(ObservedEntityId entity)
        {
            if(entity.Kind==ObservedEntityKind.Unit)motions.Remove(entity.Id);
            if(pool==null)return;foreach(Effect effect in pool)if(effect.Aggregated||effect.Entity.Equals(entity)||effect.Source.Equals(entity))ClearEffect(effect);
            audioPool.Forget(entity);
            ForgetLocalLight(entity);
        }
        public void Clear()
        {
            observation=null;ResetRecall();ownerColors.Clear();motions.Clear();unseenMotion.Clear();if(bodyLayers!=null)foreach(var layer in bodyLayers)if(layer!=null)layer.Clear();if(pool!=null)foreach(Effect effect in pool)ClearEffect(effect);
            ClearLocalLights();
            if(audioPool!=null)audioPool.Clear();ActiveEffects=auxiliaryParticles=bodyParticleCount=0;
        }
        private void OnDisable()
        {
            if (renderingSubscribed)
            {
                RenderPipelineManager.beginCameraRendering -= BeginCamera;
                renderingSubscribed = false;
            }
#if UNITY_EDITOR
            sceneViewCameras.Clear();
#endif
            Clear();
            ReleaseGlow();
            ReleaseOwnership();
            if (poolRoot != null)
            {
                if (Application.isPlaying) Destroy(poolRoot);
                else DestroyImmediate(poolRoot);
            }
            poolRoot = null;
            pool = null;
            bodyLayers = null;
            bodyRenderers = null;
            particleFallbackProperties = null;
            particleGhostProperties = null;
            frozenShellProperties = null;
            matrices = null;
            bodyUnitIds = null;
            bodySquadIds = null;
            bodyFootprints = null;
            facilityProperties = null;
            facilityPreview = null;
        }
    }
}
