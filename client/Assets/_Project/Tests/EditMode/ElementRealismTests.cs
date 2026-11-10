using System.Reflection;
using System.Linq;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>사실주의 생성 메시와 이동 시 늘어남·공유 입자 상한을 검증한다.</summary>
    public sealed class ElementRealismTests
    {
        [Test]
        public void EveryGeneratedMeshHasFiniteSurfaceTangentsAndReservedBounds()
        {
            var build=typeof(ElementVisualArtSetup).GetMethod("BuildMesh",BindingFlags.NonPublic|BindingFlags.Static);
            for(int style=0;style<14;style++)
            {
                Mesh mesh=(Mesh)build.Invoke(null,new object[]{"realism-test-"+style,style});
                try
                {
                    Assert.That(mesh.vertexCount,Is.InRange(3,2500),"style "+style);
                    Assert.That(mesh.subMeshCount,Is.EqualTo(1));
                    Assert.That(mesh.tangents.Length,Is.EqualTo(mesh.vertexCount));
                    Assert.That(mesh.triangles.Length,Is.GreaterThan(0));
                    Vector3[] points=mesh.vertices;
                    Vector4[] tangents=mesh.tangents;
                    var phases=new System.Collections.Generic.HashSet<float>();
                    if(style==11)foreach(Color color in mesh.colors)phases.Add(color.g);
                    if(style==11)Assert.That(phases.Count,Is.GreaterThanOrEqualTo(4),"공전 암편의 위상은 부품마다 달라야 합니다.");
                    for(int index=0;index<points.Length;index++)
                    {
                        Assert.That(float.IsFinite(points[index].sqrMagnitude),Is.True);
                        Assert.That(float.IsFinite(tangents[index].sqrMagnitude),Is.True);
                        Assert.That(mesh.bounds.Contains(points[index]+Vector3.right*.179f),Is.True,"GPU displacement: "+style);
                        Assert.That(mesh.bounds.Contains(points[index]-Vector3.right*.179f),Is.True,"GPU displacement: "+style);
                        if(style==11)
                        {
                            float radius=new Vector2(points[index].x,points[index].z).magnitude+.179f;
                            Assert.That(mesh.bounds.extents.x,Is.GreaterThanOrEqualTo(radius));
                            Assert.That(mesh.bounds.extents.z,Is.GreaterThanOrEqualTo(radius));
                        }
                    }
                }
                finally{Object.DestroyImmediate(mesh);}
            }
        }

        [Test]
        public void WaterHasABroadCoreAndTwoSurfacesAroundItsRollingCrest()
        {
            var build=typeof(ElementVisualArtSetup).GetMethod("BuildMesh",BindingFlags.NonPublic|BindingFlags.Static);
            Mesh mesh=(Mesh)build.Invoke(null,new object[]{"water-volume-test",0});
            try
            {
                Vector3[] points=mesh.vertices;
                Color[] details=mesh.colors;
                var core=new Bounds();
                var crest=new Bounds();
                bool hasCore=false,hasCrest=false;
                var surfaces=new System.Collections.Generic.HashSet<float>();
                for(int index=0;index<points.Length;index++)
                {
                    if(details[index].r<.1f)
                    {
                        if(!hasCore){core=new Bounds(points[index],Vector3.zero);hasCore=true;}
                        core.Encapsulate(points[index]);
                    }
                    if(details[index].r<.6f||details[index].r>.75f)continue;
                    if(!hasCrest){crest=new Bounds(points[index],Vector3.zero);hasCrest=true;}
                    crest.Encapsulate(points[index]);surfaces.Add(details[index].g);
                }
                Assert.That(hasCore&&hasCrest,Is.True);
                Assert.That(core.size.x,Is.GreaterThan(.55f),"주 유체가 외곽 물방울보다 얇아지면 안 됩니다.");
                Assert.That(core.size.z,Is.GreaterThan(.55f));
                Assert.That(crest.size.x,Is.GreaterThan(.55f));
                Assert.That(crest.size.y,Is.GreaterThan(.4f));
                Assert.That(crest.size.z,Is.GreaterThan(.4f));
                Assert.That(surfaces.Count,Is.EqualTo(2),"말린 마루는 서로 구분된 두 표면을 가져야 합니다.");
            }
            finally{Object.DestroyImmediate(mesh);}
        }

        [TestCase(.08f)]
        [TestCase(.35f)]
        [TestCase(1f)]
        public void LargeParticlesAndMovingTrailsRemainInsideTheirAssignedFootprint(float footprint)
        {
            var root=new GameObject("Body particle boundary fixture");
            try
            {
                var visuals=root.AddComponent<ElementalVisuals>();
                var write=typeof(ElementalVisuals).GetMethod("WriteBodyParticles",BindingFlags.NonPublic|BindingFlags.Instance);
                var particles=(ParticleSystem.Particle[])typeof(ElementalVisuals).GetField("bodyParticles",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(visuals);
                var rules=new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                Vector3 center=new Vector3(7,2,-3);
                Matrix4x4 pose=Matrix4x4.TRS(center,Quaternion.Euler(0,37,0),new Vector3(.8f,2,1.6f));
                foreach(var element in rules.Elements)
                {
                    var profile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder+"/"+element.Id+".asset"));
                    try
                    {
                        var settings=new SerializedObject(profile);
                        settings.FindProperty("bodyParticleSize").floatValue=2;
                        settings.FindProperty("particleTrailLength").floatValue=1.5f;
                        settings.ApplyModifiedPropertiesWithoutUndo();
                        foreach(float seconds in new[]{.01f,.4f,11.9f})
                        {
                            object[] arguments={profile,pose,int.MaxValue,24,seconds,footprint,Vector3.right,Vector3.up,0};
                            write.Invoke(visuals,arguments);
                            Assert.That((int)arguments[8],Is.EqualTo(24));
                            for(int index=0;index<24;index++)
                            {
                                Vector3 offset=particles[index].position-center;
                                Vector3 size=particles[index].startSize3D;
                                float cornerRadius=Mathf.Sqrt(size.x*size.x+size.y*size.y)*.5f;
                                Assert.That(float.IsFinite(offset.sqrMagnitude+size.sqrMagnitude),Is.True,element.Id);
                                Assert.That(size.x,Is.GreaterThan(0));
                                Assert.That(size.y,Is.GreaterThan(0));
                                Assert.That(new Vector2(offset.x,offset.z).magnitude+cornerRadius,
                                    Is.LessThanOrEqualTo(footprint+.0001f),element.Id+": "+index);
                            }
                        }
                    }
                    finally{Object.DestroyImmediate(profile);}
                }
            }
            finally{Object.DestroyImmediate(root);}
        }

        [Test]
        public void LightningSideBranchesConnectAlongAWideMainDischarge()
        {
            var fork=typeof(ElementVisualArtSetup).GetMethod("LightningFork",BindingFlags.NonPublic|BindingFlags.Static);
            Vector3 Point(float time,int branch)=>(Vector3)fork.Invoke(null,new object[]{time,branch});
            Assert.That((Point(1,0)-Point(0,0)).magnitude,Is.GreaterThan(.85f));
            var roots=new Bounds(Point(0,1),Vector3.zero);
            for(int branch=1;branch<9;branch++)
            {
                Vector3 root=Point(0,branch),end=Point(1,branch);
                roots.Encapsulate(root);
                Assert.That(float.IsFinite(root.sqrMagnitude+end.sqrMagnitude),Is.True);
                Assert.That((end-root).magnitude,Is.GreaterThan(.18f));
                float nearest=float.MaxValue;
                for(int sample=0;sample<=100;sample++)
                    nearest=Mathf.Min(nearest,(Point(sample*.01f,0)-root).magnitude);
                Assert.That(nearest,Is.LessThan(.015f),"분기는 주 방전에서 끊어지면 안 됩니다.");
            }
            Assert.That(roots.size.x,Is.GreaterThan(.5f),"모든 가지를 한 핵에 모으면 작은 전선 공처럼 보입니다.");
        }

        [Test]
        public void SmokeBodyPuffsRiseAndExpandOverTheirLifetime()
        {
            GameObject root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var smokeProfile = AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder + "/smoke.asset");
                Assert.That(smokeProfile.ParticleBody, Is.True);
                visuals.RenderGallery(14, .4f, false);
                var smoke = root.GetComponentsInChildren<ParticleSystem>(true).Single(layer => layer.name == "smoke Body Particles");
                var first = new ParticleSystem.Particle[smokeProfile.BodyParticleCount];
                var second = new ParticleSystem.Particle[first.Length];
                int initialCount = smoke.GetParticles(first);
                Assert.That(initialCount, Is.GreaterThanOrEqualTo(8));

                visuals.RenderGallery(14, .72f, false);
                int nextCount = smoke.GetParticles(second);
                int risingAndGrowing = 0;
                for (int i = 0; i < initialCount; i++)
                {
                    for (int j = 0; j < nextCount; j++)
                    {
                        if (first[i].randomSeed != second[j].randomSeed || first[i].remainingLifetime <= second[j].remainingLifetime) continue;
                        if (second[j].position.y > first[i].position.y + .02f && second[j].startSize > first[i].startSize)
                            risingAndGrowing++;
                        break;
                    }
                }
                Assert.That(risingAndGrowing, Is.GreaterThanOrEqualTo(2), "연기 입자가 수명 동안 상승하며 팽창해야 합니다.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(64)]
        [TestCase(96)]
        [TestCase(32768)]
        public void MovingBodiesElongateAndTheirParticlesRespectTheSharedBudget(int budget)
        {
            GameObject root=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals=root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var settings=new SerializedObject(visuals);
                settings.FindProperty("maxParticles").intValue=budget;
                settings.ApplyModifiedPropertiesWithoutUndo();
                var layers=(ParticleSystem[])typeof(ElementalVisuals).GetField("bodyLayers",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(visuals);
                Assert.That(layers.Length,Is.EqualTo(14));
                visuals.RenderGallery(14,3,false);
                var matrices=(Matrix4x4[][])typeof(ElementalVisuals).GetField("matrices",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(visuals);
                for(int element=0;element<14;element++)
                {
                    float width=((Vector3)matrices[element][0].GetColumn(0)).magnitude;
                    float length=((Vector3)matrices[element][0].GetColumn(2)).magnitude;
                    Assert.That(length,Is.GreaterThan(width),"element "+element);
                }
                Assert.That(visuals.ActiveParticles,Is.InRange(1,budget));
                visuals.RenderGallery(988,3,false);
                Assert.That(visuals.ActiveParticles,Is.InRange(0,budget));
                visuals.Clear();
                Assert.That(visuals.ActiveParticles,Is.Zero);
            }
            finally{Object.DestroyImmediate(root);}
        }

        [TestCase("fire",12,6)]
        [TestCase("smoke",5,5)]
        public void ReusedEffectsResetDebrisRenderingBeforePlayingFlipbooks(string id,int columns,int rows)
        {
            GameObject root=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var visuals=root.GetComponent<ElementalVisuals>();
                var settings=new SerializedObject(visuals);
                settings.FindProperty("maxEffects").intValue=1;
                settings.ApplyModifiedPropertiesWithoutUndo();
                visuals.Initialize();
                var rules=new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                visuals.GalleryOutcome(rules.ElementIndex("earth"),false);
                ParticleSystem active=null;
                foreach(var system in root.GetComponentsInChildren<ParticleSystem>(true))if(system.particleCount>0)active=system;
                Assert.That(active,Is.Not.Null);
                var renderer=active.GetComponent<ParticleSystemRenderer>();
                Assert.That(renderer.renderMode,Is.EqualTo(ParticleSystemRenderMode.Mesh));
                Assert.That(renderer.mesh,Is.Not.Null);
                Assert.That(active.main.gravityModifier.constant,Is.GreaterThan(0));
                visuals.Clear();
                visuals.GalleryOutcome(rules.ElementIndex(id),false);
                Assert.That(active.particleCount,Is.GreaterThan(0));
                Assert.That(renderer.renderMode,Is.EqualTo(ParticleSystemRenderMode.Billboard));
                Assert.That(renderer.mesh,Is.Null);
                Assert.That(active.main.gravityModifier.constant,Is.Zero);
                Assert.That(active.rotationOverLifetime.enabled,Is.False);
                Assert.That(active.main.startSize3D,Is.EqualTo(id=="fire"));
                var sample=new ParticleSystem.Particle[256];int samples=active.GetParticles(sample);
                if(id=="fire")for(int index=0;index<samples;index++)
                    Assert.That(sample[index].startSize3D.x/sample[index].startSize3D.y,Is.EqualTo(.5f).Within(.001f));
                Assert.That(active.textureSheetAnimation.enabled,Is.True);
                Assert.That(active.textureSheetAnimation.numTilesX,Is.EqualTo(columns));
                Assert.That(active.textureSheetAnimation.numTilesY,Is.EqualTo(rows));
                if(id=="fire")
                {
                    visuals.Clear();visuals.GalleryOutcome(rules.ElementIndex("smoke"),false);
                    Assert.That(active.main.startSize3D,Is.False,"불의 세로 크기 모드가 연기로 새면 안 됩니다.");
                    samples=active.GetParticles(sample);
                    for(int index=0;index<samples;index++)
                        Assert.That(sample[index].startSize3D.x,Is.EqualTo(sample[index].startSize3D.y).Within(.001f));
                }
            }
            finally{Object.DestroyImmediate(root);}
        }

        [Test]
        public void OnlyTheLastThirdOfObservedBodyParticlesTrailBehindAndFade()
        {
            GameObject root=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            ElementArtProfile profile=null;
            try
            {
                var visuals=root.GetComponent<ElementalVisuals>();
                var settings=new SerializedObject(visuals);
                var entry=settings.FindProperty("profiles").GetArrayElementAtIndex(0);
                profile=Object.Instantiate((ElementArtProfile)entry.objectReferenceValue);
                entry.objectReferenceValue=profile;settings.ApplyModifiedPropertiesWithoutUndo();
                var art=new SerializedObject(profile);art.FindProperty("particleTrailLength").floatValue=0;art.ApplyModifiedPropertiesWithoutUndo();
                visuals.Initialize();visuals.RenderGallery(14,3,false);
                ParticleSystem layer=null;
                foreach(var system in root.GetComponentsInChildren<ParticleSystem>(true))
                    if(system.name==profile.ElementId+" Body Particles")layer=system;
                Assert.That(layer,Is.Not.Null);
                var before=new ParticleSystem.Particle[12];int count=layer.GetParticles(before);
                Assert.That(count,Is.GreaterThan(2));
                art.Update();art.FindProperty("particleTrailLength").floatValue=1;art.ApplyModifiedPropertiesWithoutUndo();
                visuals.RenderGallery(14,3,false);
                var after=new ParticleSystem.Particle[12];Assert.That(layer.GetParticles(after),Is.EqualTo(count));
                var matrices=(Matrix4x4[][])typeof(ElementalVisuals).GetField("matrices",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(visuals);
                Vector3 forward=((Vector3)matrices[0][0].GetColumn(2)).normalized;
                int tailStart=count-Mathf.Max(1,count/3);
                for(int index=0;index<count;index++)
                {
                    Vector3 delta=after[index].position-before[index].position;
                    if(index<tailStart)Assert.That(delta.sqrMagnitude,Is.LessThan(.000001f));
                    else
                    {
                        Assert.That(Vector3.Dot(delta,forward),Is.LessThan(0));
                        Assert.That(delta.magnitude,Is.LessThanOrEqualTo(1));
                        Assert.That(after[index].startColor.a,Is.LessThanOrEqualTo(before[index].startColor.a));
                        Assert.That(after[index].startSize,Is.LessThan(before[index].startSize));
                    }
                }
            }
            finally{Object.DestroyImmediate(root);if(profile!=null)Object.DestroyImmediate(profile);}
        }

        [Test]
        public void SavedMeshesRenderTheSameSilhouetteAsTheirCpuChannels()
        {
            var fixture=new GameObject("Mesh GPU regression fixture");
            Material white=null;
            RenderTexture target=null;
            Texture2D pixels=null;
            RenderTexture previousTarget=RenderTexture.active;
            try
            {
                var cameraObject=new GameObject("Mesh GPU regression camera");
                cameraObject.transform.SetParent(fixture.transform,false);
                var camera=cameraObject.AddComponent<UnityEngine.Camera>();
                camera.enabled=false;camera.orthographic=true;camera.orthographicSize=1.1f;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.cullingMask=1<<31;
                camera.transform.position=new Vector3(1,.55f,-2.5f);camera.transform.LookAt(Vector3.zero);
                target=new RenderTexture(64,64,24,RenderTextureFormat.ARGB32);target.Create();
                pixels=new Texture2D(64,64,TextureFormat.RGB24,false);
                white=new Material(Shader.Find("Universal Render Pipeline/Unlit"));white.SetColor("_BaseColor",Color.white);
                var body=new GameObject("Mesh GPU regression body");body.layer=31;
                body.transform.SetParent(fixture.transform,false);
                var filter=body.AddComponent<MeshFilter>();body.AddComponent<MeshRenderer>().sharedMaterial=white;
                var rules=new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                foreach(var definition in rules.Elements)
                {
                    Mesh asset=AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder+"/"+definition.Id+".asset").Mesh;
                    var rebuilt=new Mesh();
                    try
                    {
                        rebuilt.vertices=asset.vertices;rebuilt.normals=asset.normals;rebuilt.tangents=asset.tangents;
                        rebuilt.uv=asset.uv;rebuilt.colors=asset.colors;rebuilt.triangles=asset.triangles;rebuilt.bounds=asset.bounds;
                        filter.sharedMesh=asset;
                        bool[] expected=Silhouette(camera,target,pixels);
                        filter.sharedMesh=rebuilt;
                        bool[] actual=Silhouette(camera,target,pixels);
                        Assert.That(actual,Is.EqualTo(expected),definition.Id+"의 CPU 채널과 GPU 형상이 다릅니다.");
                    }
                    finally{filter.sharedMesh=null;Object.DestroyImmediate(rebuilt);}
                }
            }
            finally
            {
                RenderTexture.active=previousTarget;
                Object.DestroyImmediate(fixture);
                if(pixels!=null)Object.DestroyImmediate(pixels);
                if(target!=null){target.Release();Object.DestroyImmediate(target);}
                if(white!=null)Object.DestroyImmediate(white);
            }
        }

        private static bool[] Silhouette(UnityEngine.Camera camera,RenderTexture target,Texture2D pixels)
        {
            RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest{destination=target});
            RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,64,64),0,0);pixels.Apply();
            Color32[] colors=pixels.GetPixels32();var mask=new bool[colors.Length];
            int visible=0;
            for(int index=0;index<colors.Length;index++)
            {
                mask[index]=colors[index].r>20;
                if(mask[index])visible++;
            }
            Assert.That(visible,Is.GreaterThan(0),"검증 카메라가 메시를 보지 못했습니다.");
            return mask;
        }
    }
}
