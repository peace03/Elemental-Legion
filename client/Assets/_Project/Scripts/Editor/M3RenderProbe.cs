using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ElementalLegion.Client.FogOfWar;
using ElementalLegion.Client.Legion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Client.Editor
{
    /// <summary>Play에 들어가지 않는 1080p 전체 카메라 비교다. 별도 Player 수용 결과와 구분한다.</summary>
    public static class M3RenderProbe
    {
        private static Scene original,fixture;
        private static GameObject root;
        private static ElementalVisuals visuals;
        private static UnityEngine.Camera camera;
        private static RenderTexture target;
        private static CustomSampler sampler;
        private static Recorder recorder;
        private static readonly List<double> cpu=new List<double>(),gpu=new List<double>();
        private static int frame,units;
        private static long allocated;
        private static bool originalGpu,emitEffects;
        private static bool allocationCounterAvailable;
        private static Unity.Profiling.ProfilerRecorder editorAllocations;
        private static readonly List<long> gcSamples=new List<long>();
        private static string path;
        private static bool combat,recordFrames;
        private static bool legacyMarkers;
        private static ParticleSystem legacySystem;
        private static readonly ParticleSystem.Particle[] legacyParticles=new ParticleSystem.Particle[Protocol.MaxUnits];
        private static SimulationWorld world;
        private static SnapshotTimeline timeline;
        private static UnitSnapshotAssembler unitsReplica;
        private static GameplayReplicationState gameplaySender;
        private static GameplayReplica gameplayReplica;
        private static FogReplicationState fogSender;
        private static FogStateReplica fogReplica;
        private static readonly NetDataWriter writer=new NetDataWriter();
        private static readonly ObservedEntityId[] forgotten=new ObservedEntityId[FogProtocol.ForgetPerChunk];
        private static GameRules rules;
        public static bool Running=>root!=null;
        public static void Start(int count=14,bool effects=true,string output="Temp/M3Work/gallery-probe.json",bool fight=false,bool record=false,bool legacy=false)
        {
            if(root!=null||EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("M3 표본이 실행 중이거나 Play 상태입니다.");
            units=count;emitEffects=effects;path=output;Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));cpu.Clear();gpu.Clear();frame=0;allocated=0;
            gcSamples.Clear();long allocationStart=GC.GetAllocatedBytesForCurrentThread();var allocationCheck=new byte[65536];allocationCounterAvailable=GC.GetAllocatedBytesForCurrentThread()-allocationStart>=65536;GC.KeepAlive(allocationCheck);
            combat=fight;recordFrames=record;legacyMarkers=legacy;if(recordFrames)Directory.CreateDirectory(Path.ChangeExtension(path,null)+"-frames");
            original=SceneManager.GetActiveScene();fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);SceneManager.SetActiveScene(fixture);
            try
            {
                root=M3GalleryBuilder.CreateFixture();camera=root.GetComponentInChildren<UnityEngine.Camera>();camera.enabled=false;
                if(count>14){camera.orthographicSize=31;root.transform.Find("Gallery Labels").gameObject.SetActive(false);camera.transform.localPosition=new Vector3(0,53,-30);camera.transform.LookAt(root.transform.position+new Vector3(0,0,-9));}
                root.GetComponent<GalleryFog>().Initialize();visuals=root.GetComponent<ElementalVisuals>();visuals.Initialize();
                if(legacyMarkers)
                {
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);var serialized=new SerializedObject(prefab.GetComponent<LegionPresentation>());var source=(ParticleSystem)serialized.FindProperty("markers").objectReferenceValue;
                    legacySystem=UnityEngine.Object.Instantiate(source,root.transform);legacySystem.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);legacySystem.Play();
                    int columns=count<=14?7:38;float spacing=count<=14?6:1.45f;
                    for(int index=0;index<count;index++){legacyParticles[index].position=root.transform.position+new Vector3((index%columns-(columns-1)*0.5f)*spacing,0.3f,8-index/columns*spacing);legacyParticles[index].startColor=index%3==0?new Color(0.2f,0.9f,1):index%3==1?new Color(1,0.35f,0.15f):new Color(0.8f,0.8f,0.9f);legacyParticles[index].startSize=1.3f;legacyParticles[index].startLifetime=legacyParticles[index].remainingLifetime=1000000;}
                    legacySystem.SetParticles(legacyParticles,count);
                }
                if(combat)
                {
                    rules=new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                    var setup=new WorldSetup();
                    setup.Players.Add(new PlayerSpawn(1,"water",new System.Numerics.Vector2(810),new System.Numerics.Vector2(836,840),3));
                    setup.Players.Add(new PlayerSpawn(2,"fire",new System.Numerics.Vector2(870),new System.Numerics.Vector2(844,840),3));
                    for(int element=0;element<rules.Elements.Count;element++)
                    {setup.Units.Add(new UnitSpawn(1,rules.Elements[element].Id,new System.Numerics.Vector2(836,837+element*0.45f)));setup.Units.Add(new UnitSpawn(2,rules.Elements[element].Id,new System.Numerics.Vector2(844,837+element*0.45f)));}
                    world=new SimulationWorld(rules,setup,1);timeline=new SnapshotTimeline(5);unitsReplica=new UnitSnapshotAssembler(rules);
                    gameplaySender=new GameplayReplicationState(rules);gameplayReplica=new GameplayReplica(rules);gameplayReplica.BindPlayer(1);fogSender=new FogReplicationState(rules);fogReplica=new FogStateReplica(rules);
                    camera.orthographicSize=13;camera.transform.position=new Vector3(840,35,820);camera.transform.LookAt(new Vector3(840,0,840));
                }
                target=new RenderTexture(1920,1080,24){name="M3 Full Camera Probe"};target.Create();
                sampler=CustomSampler.Create("M3 Entire Camera",true);recorder=sampler.GetRecorder();recorder.enabled=true;
                originalGpu=UnityEditorInternal.ProfilerDriver.profileGPU;UnityEditorInternal.ProfilerDriver.profileGPU=true;
                editorAllocations=Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Memory,"GC Allocated In Frame",1);
                EditorApplication.update+=Update;
            }
            catch{Cleanup();throw;}
        }
        private static void Update()
        {
            try
            {
                bool sampled=frame>=60;
                if(sampled&&recorder.gpuSampleBlockCount>0&&recorder.gpuElapsedNanoseconds>0)gpu.Add(recorder.gpuElapsedNanoseconds/1000000.0);
                if(sampled&&editorAllocations.Valid)gcSamples.Add(editorAllocations.LastValue);
                long bytes=GC.GetAllocatedBytesForCurrentThread();long start=Stopwatch.GetTimestamp();sampler.Begin();
                if(combat)RenderCombat();else if(!legacyMarkers)visuals.RenderGallery(units,frame/60f,emitEffects&&frame%60==0);visuals.SimulatePreview(1/60f);
                RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});sampler.End();
                if(sampled){cpu.Add((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);allocated+=GC.GetAllocatedBytesForCurrentThread()-bytes;}
                if(frame==100||frame==160)Capture(Path.ChangeExtension(path,frame==100?".png":".second.png"));
                if(recordFrames&&frame%2==0)Capture(Path.Combine(Path.ChangeExtension(path,null)+"-frames",(frame/2).ToString("D4")+".jpg"));
                if(++frame<300)return;
                cpu.Sort();gpu.Sort();var result=new
                {
                    mode="Editor offscreen entire RenderRequest + CPU presentation; Player acceptance separate",resolution="1920x1080",units,effects=emitEffects,
                    gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),cpuSamples=cpu.Count,gpuSamples=gpu.Count,
                    cpuP95Ms=P95(cpu),entireCameraGpuP95Ms=P95(gpu),allocatedPerSample=allocationCounterAvailable?(double?)(allocated/(double)cpu.Count):null,
                    allocationCounterAvailable,editorWholeFrameGcP95Bytes=gcSamples.Count>0?(long?)gcSamples.OrderBy(value=>value).ElementAt((int)Math.Ceiling(gcSamples.Count*0.95)-1):null,
                    gcScope="Editor whole-frame counter includes Editor/recording; not isolated Player GC",
                    activeVfx=visuals.ActiveEffects,peakVfx=visuals.PeakEffects,activeParticles=visuals.ActiveParticles,decorationDrops=visuals.DecorationDrops,
                    combat,recording=recordFrames,legacyMarkers,combatTicks=world?.Metrics.Tick,attacks=world?.Metrics.Attacks,deaths=world?.Metrics.Deaths,conversions=world?.Metrics.Conversions,drops=world?.Metrics.Drops,
                    fullPlayerFrameP95="pending manual Play/Windows Player; offscreen is not a 60fps verdict"
                };
                File.WriteAllText(path,Newtonsoft.Json.JsonConvert.SerializeObject(result,Newtonsoft.Json.Formatting.Indented));Cleanup();
            }
            catch(Exception exception){File.WriteAllText(path,exception.ToString());Cleanup();UnityEngine.Debug.LogException(exception);}
        }
        private static void RenderCombat()
        {
            if(frame%3==0){world.Step();gameplaySender.Capture(world.GetView(1));}
            WorldView view=world.GetView(1);
            if(frame%6==0)
            {
                fogSender.Prepare(view);
                for(int part=0;part<fogSender.ForgetChunkCount;part++)
                {
                    fogSender.WriteForget(writer,part);if(FogProtocol.TryReadEntityForget(new NetDataReader(writer.CopyData()),forgotten,out long at,out _,out _,out int count))for(int index=0;index<count;index++)
                    {visuals.Forget(forgotten[index]);gameplayReplica.Forget(forgotten[index],at);if(forgotten[index].Kind==ObservedEntityKind.Unit)timeline.Forget(forgotten[index].Id,at);}
                }
                for(int part=0;part<fogSender.ChunkCount;part++){fogSender.WriteChunk(writer,part);fogReplica.TryAccept(new NetDataReader(writer.CopyData()));}fogSender.Commit();root.GetComponentInChildren<FogPresentation>().Apply(fogReplica);
                gameplaySender.Prepare(view,fogSender.Revision);for(int part=0;part<gameplaySender.ChunkCount;part++){gameplaySender.WriteChunk(writer,part);gameplayReplica.TryAccept(new NetDataReader(writer.CopyData()),frame/60.0);}gameplaySender.Commit();
                while(gameplayReplica.TryPublish(fogReplica,out GameplayFrame data))
                {
                    visuals.Observe(data);foreach(NetworkEvent entry in data.Events)
                    {
                        if(entry.Data.Kind==SimEventKind.Death&&entry.Data.EntityId>0)timeline.ResolveDeath(entry.Data.EntityId,entry.Tick);
                        if(entry.Data.Kind==SimEventKind.Converted&&entry.Data.EntityId>0&&entry.Data.OwnerId>0)timeline.ResolveConversion(entry.Data.EntityId,entry.Tick,entry.Data.OwnerId,entry.Data.Amount);
                        if(emitEffects&&data.SpatialReady)visuals.Play(entry);
                    }
                }
                for(int part=0;part<Protocol.ChunkCount(view.Units.Count);part++){Protocol.WriteUnits(writer,view,rules.MapSize,part,fogSender.Revision);if(unitsReplica.TryAccept(new NetDataReader(writer.CopyData())))timeline.Push(unitsReplica);}
            }
            visuals.Render(timeline,1,view.LegionPosition,Math.Max(0,view.Tick-2));
        }
        private static double? P95(List<double> samples)=>samples.Count==0?(double?)null:samples[(int)Math.Ceiling(samples.Count*0.95)-1];
        private static void Capture(string output)
        {
            var previous=RenderTexture.active;RenderTexture.active=target;var texture=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0);texture.Apply();File.WriteAllBytes(output,Path.GetExtension(output)==".jpg"?texture.EncodeToJPG(85):texture.EncodeToPNG());UnityEngine.Object.DestroyImmediate(texture);RenderTexture.active=previous;
        }
        private static void Cleanup()
        {
            EditorApplication.update-=Update;if(recorder!=null)recorder.enabled=false;UnityEditorInternal.ProfilerDriver.profileGPU=originalGpu;
            if(editorAllocations.Valid)editorAllocations.Dispose();
            gameplayReplica?.Dispose();gameplayReplica=null;world=null;
            if(root!=null){foreach(var fog in root.GetComponentsInChildren<FogPresentation>(true))fog.ReleaseResources();UnityEngine.Object.DestroyImmediate(root);}
            if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);}if(fixture.IsValid())EditorSceneManager.CloseScene(fixture,true);if(original.IsValid()&&original.isLoaded)SceneManager.SetActiveScene(original);root=null;
        }
    }
}
