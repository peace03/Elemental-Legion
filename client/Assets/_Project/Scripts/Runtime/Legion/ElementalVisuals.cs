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
        [SerializeField,Min(0.1f),Tooltip("전장 원소 몸체와 소유자 링의 최대 가로 반경(m)입니다.")] private float unitFootprintRadius=0.6f;
        [SerializeField,Min(0),Tooltip("몸체 중심 높이(m)입니다.")] private float bodyHeight=1.1f;
        [SerializeField,Min(0.01f),Tooltip("이동 흔적 방출 간격(초)입니다.")] private float trailInterval=0.12f;
        [SerializeField,Range(0,1),Tooltip("유령 원소와 기지의 불투명도입니다.")] private float ghostOpacity=0.25f;
        [SerializeField,Range(0,1),Tooltip("건설 미리보기의 불투명도입니다.")] private float previewOpacity=0.4f;
        [SerializeField,Range(0,1),Tooltip("건설 중인 시설의 불투명도입니다.")] private float unfinishedOpacity=0.48f;
        [SerializeField,Min(0.1f),Tooltip("타워 받침과 원소 장식을 합친 높이(m)입니다.")] private float towerHeight=4f;
        private const int GhostUnits=20, GhostBuildings=34, GhostRings=35, Domes=36;
        private static readonly int Opacity=Shader.PropertyToID("_Opacity"), ElementStyle=Shader.PropertyToID("_Element");
        private static readonly int BaseColor=Shader.PropertyToID("_BaseColor"), SecondColor=Shader.PropertyToID("_SecondColor");
        private Matrix4x4[][] matrices;
        private readonly int[] counts=new int[Domes+1];
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
        private MaterialPropertyBlock ghostProperties, previewProperties, towerPreviewProperties, unfinishedProperties, domeProperties;
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
        private float[] maxHealth, bodyRadii;
        private float unitFootprint;
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
            unitFootprint = Mathf.Min(unitFootprintRadius, rules.MinimumSeparation * 0.5f);
            bodyRadii = new float[profiles.Length];
            for (int index = 0; index < profiles.Length; index++)
            {
                bodyRadii[index] = HorizontalRadius(profiles[index].Mesh);
                // ElementBody 셰이더의 가로 정점 변형까지 포함한다.
                if (index == waterIndex) bodyRadii[index] += 0.165f;
                if (index == fireIndex || index == smokeIndex) bodyRadii[index] += 0.13f;
            }
            ghostProperties = Spectral(ghostOpacity);
            previewProperties = Spectral(previewOpacity);
            towerPreviewProperties = Spectral(previewOpacity);
            unfinishedProperties = Spectral(unfinishedOpacity);
            domeProperties = Spectral(ghostOpacity * 0.5f);
            matrices = new Matrix4x4[counts.Length][];
            for (int index = 0; index < matrices.Length; index++)
            {
                int capacity = index < 14 ? Protocol.MaxUnits * 2 : Protocol.MaxUnits;
                if (index >= 14 && index <= 16) capacity = Protocol.MaxUnits * 2 + GameplayCodec.MaxBuildings;
                else if (index == 19 || index == GhostRings) capacity += GameplayCodec.MaxBuildings;
                else if (index == GhostBuildings || index == Domes) capacity = GameplayCodec.MaxBuildings;
                matrices[index] = new Matrix4x4[capacity];
            }
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
                int group = unit.Ghost ? GhostUnits + unit.Element : unit.Element;
                matrices[group][counts[group]++]=Matrix4x4.TRS(position,Quaternion.Euler(0,angle,0),BodyScale(unit.Element, scale, scale*(profile.Style==1?1.8f:1.15f)));
                int relation=unit.OwnerId==owner?0:unit.OwnerId<0?2:1;
                int ringGroup = unit.Ghost ? GhostRings : 14 + relation;
                matrices[ringGroup][counts[ringGroup]++]=Matrix4x4.TRS(new Vector3(location.X,0.12f,location.Y),Quaternion.identity,Vector3.one*unitFootprint);
                if(unit.Health<maxHealth[unit.Element])
                {
                    float ratio=Mathf.Clamp01(unit.Health/maxHealth[unit.Element]);
                    matrices[19][counts[19]++]=Matrix4x4.TRS(position+new Vector3((ratio-1)*0.6f,1,-0.8f),Quaternion.identity,new Vector3(ratio*1.2f,0.055f,0.11f));
                }
                if (unit.Ghost) continue;
                if(unit.Shield>0)matrices[14+relation][counts[14+relation]++]=Matrix4x4.TRS(position,Quaternion.Euler(72,Time.time*25,0),Vector3.one*unitFootprint);
                if((unit.StatusMask&(1<<4))!=0)matrices[iceIndex][counts[iceIndex]++]=Matrix4x4.TRS(position,Quaternion.Euler(0,angle,0),BodyScale(iceIndex, bodySize*1.15f, bodySize*1.15f));
                if(unit.Shield>0)matrices[magnetismIndex][counts[magnetismIndex]++]=Matrix4x4.TRS(position,Quaternion.identity,BodyScale(magnetismIndex, bodySize*1.2f, bodySize*1.2f));
                if(trail&&(unit.StatusMask!=0||profile.Emission>0))Emit(unit.StatusMask!=0?profiles[StatusProfile(unit.StatusMask)]:profile,position,unit.StatusMask!=0?10:3,false,new ObservedEntityId(ObservedEntityKind.Unit,unit.Id),Vector3.up*0.3f,0.45f);
            }
            if(observation!=null&&observation.SpatialReady)
            {
                foreach(BuildingView building in observation.Buildings) RenderBuilding(building, owner);
                foreach(CoreView core in observation.Cores)matrices[17][counts[17]++]=Matrix4x4.TRS(new Vector3(core.Position.X,0.7f+Mathf.Sin(Time.time*2+core.Id)*0.1f,core.Position.Y),Quaternion.Euler(0,Time.time*40,15),Vector3.one*0.42f);
                foreach(AreaView area in observation.Areas)
                {
                    // 지속 영역은 최신 관측에 있는 동안만 그린다. 재관측 때 동일 ID로 복구된다.
                    DrawOne(ring,reactions[(int)area.Kind],Matrix4x4.TRS(new Vector3(area.Position.X,0.15f,area.Position.Y),Quaternion.identity,Vector3.one*area.Radius));
                }
            }
            for(int element=0;element<14;element++)
            {
                DrawBatch(profiles[element].Mesh,profiles[element].Body,element);
                DrawBatch(profiles[element].Mesh,profiles[element].Body,GhostUnits+element,ghostProperties);
            }
            DrawBodyParticles(Now, unitFootprint);
            for(int relation=0;relation<3;relation++)DrawBatch(ring,ownership[relation],14+relation);
            DrawBatch(crystal,profiles[lightIndex].Body,17);DrawBatch(crystal,profiles[crystalIndex].Body,18);
            DrawBatch(crystal,profiles[crystalIndex].Body,GhostBuildings,ghostProperties);
            DrawBatch(ring,profiles[lightIndex].Body,GhostRings,ghostProperties);
            DrawBatch(profiles[waterIndex].Mesh,profiles[lightIndex].Body,Domes,domeProperties);
            DrawBatch(healthBar,ownership[0],19);
            DrawEffects();
        }

        private void RenderBuilding(BuildingView building, int owner)
        {
            bool ghost = building.GhostEndTick > 0;
            Vector3 center = new Vector3(building.Position.X, 0.08f, building.Position.Y);
            int ringGroup = ghost || building.Kind == BuildingKind.Dome ? GhostRings : building.OwnerId == owner ? 14 : 15;
            Matrix4x4 footprint = Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * building.Radius);
            MaterialPropertyBlock translucent = ghost ? ghostProperties : building.IsUnderConstruction ? unfinishedProperties : null;
            if (building.IsUnderConstruction && !ghost) DrawOne(ring, profiles[lightIndex].Body, footprint, unfinishedProperties);
            else matrices[ringGroup][counts[ringGroup]++] = footprint;
            Matrix4x4 model = BuildingModel(building.Kind, center, building.Radius);
            if (building.Kind == BuildingKind.Dome)
            {
                if (building.IsUnderConstruction) DrawOne(profiles[waterIndex].Mesh, profiles[lightIndex].Body, model, translucent);
                else matrices[Domes][counts[Domes]++] = model;
            }
            else if (IsTower(building.Kind) && ValidElement(building.TowerElement))
                RenderTower(building.TowerElement, center, building.Radius, translucent);
            else if (building.IsUnderConstruction && !ghost) DrawOne(crystal, profiles[crystalIndex].Body, model, translucent);
            else
            {
                int group = ghost ? GhostBuildings : 18;
                matrices[group][counts[group]++] = model;
            }
            if (building.MaxHealth <= 0) return;
            float ratio = Mathf.Clamp01(building.Health / building.MaxHealth);
            float width = Mathf.Clamp(building.Radius, 1.8f, 5);
            float height = IsTower(building.Kind) ? towerHeight + 0.35f : building.Kind == BuildingKind.Base ? 4.2f : 3;
            Vector3 offset = new Vector3((ratio-1)*width*0.5f, height, -building.Radius*0.55f);
            matrices[19][counts[19]++] = Matrix4x4.TRS(center+offset, Quaternion.identity, new Vector3(ratio*width, 0.12f, 0.16f));
        }

        internal void RenderConstructionPreview(BuildingKind kind, int element, Vector3 center, float radius, float buildRadius, float towerRange,
            System.Numerics.Vector2 basePosition, bool available)
        {
            if (previewProperties == null) return;
            Material body = profiles[lightIndex].Body;
            Color tint = available ? new Color(0.33f, 0.95f, 0.8f) : new Color(1, 0.4f, 0.32f);
            previewProperties.SetColor(BaseColor, new Color(0.35f, 0.56f, 0.62f));
            previewProperties.SetColor(SecondColor, Color.black);
            var origin = new Vector3(basePosition.X, 0.09f, basePosition.Y);
            DrawOne(ring, body, Matrix4x4.TRS(origin, Quaternion.identity, Vector3.one*buildRadius), previewProperties);
            previewProperties.SetColor(BaseColor, tint);
            center.y = 0.13f;
            DrawOne(ring, body, Matrix4x4.TRS(center, Quaternion.identity, Vector3.one*radius), previewProperties);
            if (IsTower(kind) && ValidElement(element))
            {
                DrawOne(ring, body, Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * towerRange), previewProperties);
                RenderTower(element, center, radius, towerPreviewProperties);
            }
            else DrawOne(kind == BuildingKind.Dome ? profiles[waterIndex].Mesh : crystal, body,
                    BuildingModel(kind, center, radius), previewProperties);
        }

        private static bool IsTower(BuildingKind kind) => kind == BuildingKind.NatureTower || kind == BuildingKind.EnergyTower;
        private bool ValidElement(int element) => element >= 0 && element < profiles.Length;
        private float TowerStandHeight => towerHeight * 0.45f;
        private float TowerHeadHeight => (towerHeight + TowerStandHeight) * 0.5f;

        internal void RenderTowerRange(BuildingView tower, float range)
        {
            Vector3 center = new Vector3(tower.Position.X, 0.13f, tower.Position.Y);
            DrawOne(ring, ownership[0], Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * range));
        }

        private void RenderTower(int element, Vector3 center, float radius, MaterialPropertyBlock translucent)
        {
            float standHeight = TowerStandHeight;
            var stand = Matrix4x4.TRS(center + Vector3.up * (standHeight * 0.5f), Quaternion.identity,
                new Vector3(radius * 0.9f, standHeight, radius * 0.9f));
            DrawOne(crystal, profiles[crystalIndex].Body, stand, translucent);

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
            ? building.Radius * 0.425f : IsTower(building.Kind) ? towerHeight : BuildingHeight(building.Kind);

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
                float sourceHeight = data.SourceKind == ObservedEntityKind.Building ? TowerHeadHeight + 0.08f : bodyHeight;
                Vector3 start=new Vector3(data.SourcePosition.X,sourceHeight,data.SourcePosition.Y);Vector3 direction=point-start;
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

        private Vector3 BodyScale(int element, float horizontal, float height)
        {
            float width = Mathf.Min(horizontal, unitFootprint / bodyRadii[element]);
            return new Vector3(width, height, width);
        }

        private void DrawBodyParticles(float seconds, float footprint = float.PositiveInfinity)
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
                    float size=(profile.Style==1?1.6f:profile.Style==3?1.4f+phase:profile.Style==0?0.25f:0.13f)*profile.Size;
                    float fit = Mathf.Min(1, footprint / (radius + size * 0.5f));
                    radius *= fit;
                    Vector3 offset=new Vector3(Mathf.Cos(angle)*radius,(phase-0.3f)*(profile.Style==1?2:profile.Style==3?2.5f:0.6f),Mathf.Sin(angle)*radius);
                    ref ParticleSystem.Particle particle=ref bodyParticles[written++];particle.position=center+offset;particle.startColor=profile.Style==3?new Color(0.48f,0.5f,0.53f,0.5f):profile.Style==1?new Color(1,0.55f,0.12f,0.8f):new Color(profile.Tint.r,profile.Tint.g,profile.Tint.b,0.65f);
                    particle.startSize=size*fit;particle.startLifetime=1;particle.remainingLifetime=1-phase;particle.randomSeed=(uint)(unit*17+index+1);
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
        private void DrawBatch(Mesh mesh,Material material,int group,MaterialPropertyBlock properties=null)
        {
            for(int offset=0;offset<counts[group];offset+=draw.Length)
            {int count=Math.Min(draw.Length,counts[group]-offset);Array.Copy(matrices[group],offset,draw,0,count);Graphics.DrawMeshInstanced(mesh,0,material,draw,count,properties,ShadowCastingMode.Off,false,gameObject.layer,targetCamera);}
        }
        private void DrawOne(Mesh mesh,Material material,Matrix4x4 matrix,MaterialPropertyBlock properties=null)=>Graphics.DrawMesh(mesh,matrix,material,gameObject.layer,targetCamera,0,properties,ShadowCastingMode.Off,false);
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
