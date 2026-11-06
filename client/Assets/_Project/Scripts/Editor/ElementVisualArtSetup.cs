using System;
using System.Collections.Generic;
using System.IO;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using UnityEditor;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    /// <summary>14종의 표현 메시와 기존 재질·프로필만 갱신한다. 씬과 프리팹은 변경하지 않는다.</summary>
    public static partial class ElementVisualArtSetup
    {
        public const string MeshFolder = "Assets/_Project/Meshes/Elements";
        private const string ReflectionPath="Assets/_Project/Art/Surfaces/studio_small_08/studio_small_08_1k.hdr";
        private static readonly Color[] Primary = {
            new Color(.06f,.38f,.64f), new Color(1,.24f,.025f), new Color(.72f,.62f,.42f),
            new Color(.16f,.2f,.24f), new Color(.57f,.78f,.87f), new Color(.9f,.84f,.72f),
            new Color(.48f,.57f,1), new Color(1,.82f,.48f), new Color(.7f,.62f,.8f),
            new Color(.62f,.57f,.41f), new Color(.008f,.006f,.013f), new Color(.65f,.61f,.53f),
            new Color(.32f,.35f,.38f), new Color(.36f,.51f,.12f)
        };
        private static readonly Color[] Secondary = {
            new Color(.48f,.94f,1), new Color(1,.77f,.18f), new Color(.97f,.88f,.68f),
            new Color(.42f,.47f,.52f), new Color(.75f,.95f,1), new Color(.65f,.52f,.31f),
            new Color(.88f,.7f,1), new Color(1,.95f,.66f), new Color(.87f,.65f,1),
            new Color(1,.76f,.08f), new Color(.9f,.7f,1.1f), new Color(.92f,.83f,.61f),
            new Color(.42f,.57f,1), new Color(.69f,.77f,.24f)
        };

        /// <summary>기존 자산 참조를 유지하면서 시안용 메시와 표현 기본값을 저장한다. Play 중에는 호출할 수 없다.</summary>
        [MenuItem("Elemental Legion/Configure Element Visual Art")]
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Play 중에는 원소 자산을 구성하지 않습니다.");
            var data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
            if (data == null) throw new InvalidOperationException("공유 원소 데이터를 찾을 수 없습니다.");
            var rules = new GameRules(new GameDataDocument(data.text));
            var profiles = new ElementArtProfile[rules.Elements.Count];
            var materials = new Material[profiles.Length];
            var matterShader=Shader.Find("ElementalLegion/ElementMatterParticles");
            if(matterShader==null||ShaderUtil.ShaderHasError(matterShader))throw new InvalidOperationException("불·연기 플립북 셰이더를 먼저 확인하세요.");
            // 누락된 필수 참조 때문에 일부 자산만 갱신되는 일을 먼저 막는다.
            for (int index = 0; index < profiles.Length; index++)
            {
                string id = rules.Elements[index].Id;
                profiles[index] = AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder + "/" + id + ".asset");
                materials[index] = AssetDatabase.LoadAssetAtPath<Material>(M3SceneSetup.MaterialFolder + "/" + id + ".mat");
                var profile = profiles[index];
                if (profile == null || materials[index] == null || profile.Body != materials[index] ||
                    profile.ElementId != id || profile.Style < 0 || profile.Style >= Primary.Length)
                    throw new InvalidOperationException("원소 프로필과 몸체 재질 연결을 먼저 확인하세요: " + id);
                if (materials[index].shader == null || materials[index].shader.name != "ElementalLegion/ElementBody" || ShaderUtil.ShaderHasError(materials[index].shader))
                    throw new InvalidOperationException("원소 셰이더 컴파일을 먼저 확인하세요: " + id);
            }
            Directory.CreateDirectory(MeshFolder);
            AssetDatabase.Refresh();
            PrepareSurfaceImports();
            for (int index = 0; index < profiles.Length; index++)
            {
                var profile = profiles[index];
                int style = profile.Style;
                Mesh mesh=SaveMesh(BuildMesh(profile.ElementId,style),MeshFolder+"/"+profile.ElementId+".asset");
                Mesh fragment=SaveMesh(BuildFragment(profile.ElementId,style),MeshFolder+"/"+profile.ElementId+"-debris.asset");
                var material = materials[index];
                material.SetColor("_BaseColor", Primary[style]);
                material.SetColor("_SecondColor", Secondary[style]);
                material.SetFloat("_Element", style);
                material.SetFloat("_Flow", style == 1 || style == 6 ? 1.35f : .85f);
                ConfigureSurface(material,style);
                material.enableInstancing = true;
                EditorUtility.SetDirty(material);
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("mesh").objectReferenceValue = mesh;
                serialized.FindProperty("fragmentMesh").objectReferenceValue = fragment;
                serialized.FindProperty("solidUnitBody").objectReferenceValue = style is 5 or 9 or 13
                    ? ConfigureSolidUnitBody(profile.ElementId, material) : null;
                serialized.FindProperty("particleBody").boolValue = style is 1 or 2 or 3;
                serialized.FindProperty("tint").colorValue = style==1?Color.white:style==3?new Color(.65f,.7f,.76f):style==11?new Color(.9f,.95f,1):Color.Lerp(Primary[style],Secondary[style],.55f);
                Set(serialized, "hoverAmplitude", style == 5 ? .08f : .06f);
                Set(serialized, "motionFrequency", style == 6 ? 3.5f : 1.7f);
                Set(serialized, "spinSpeed", style==1||style==3?4:style==2||style==7||style==10||style==11?24:9);
                Set(serialized, "pulseAmplitude", style == 10 ? .035f : .06f);
                Set(serialized, "attackRecoil", .18f);
                Set(serialized, "attackDuration", .22f);
                Set(serialized, "projectileSpeed", style == 6 || style == 7 ? 32 : 24);
                serialized.FindProperty("bodyParticleCount").intValue = style switch { 1 => 20, 2 => 18, 3 => 22, 6 => 10, _ => 6 };
                Set(serialized, "deformationPadding", .18f);
                Set(serialized, "movementStretch",style==1||style==2||style==3||style==7?1.15f:.65f);
                Set(serialized, "particleTrailLength",style==0||style==1||style==2||style==3||style==7?1.5f:.55f);
                Set(serialized, "bodyParticleSize", style switch { 1 => 1.45f, 2 => 1.25f, 3 => 1.4f, _ => .75f });
                Set(serialized, "bodyParticleOpacity", style switch { 1 => .9f, 2 => .48f, 3 => .9f, _ => .65f });
                serialized.FindProperty("particleColumns").intValue=style==1?12:style==3||style==2?5:1;
                serialized.FindProperty("particleRows").intValue=style==1?6:style==3||style==2?5:1;
                Set(serialized,"particleAspect",style==1?.5f:1);
                if(style==1||style==2||style==3||style==0||style==7||style==10||style==11)
                {
                    string texture=style==1?"Fire & Explosion Effects/Textures/MediumFlame03.tif":style==3||style==2?"Fire & Explosion Effects/Textures/SmokePuff01.tif":"Legacy Particles/Textures/WaterDropParticle.tif";
                    if(style==1||style==2||style==3||style==7||style==10||style==11)
                    {
                        var particle=profile.Particles;
                        particle.shader=matterShader;
                        bool glow=style==7||style==10||style==11;
                        particle.SetFloat("_Element",glow?7:style);
                        particle.SetColor("_BaseColor",style==1?new Color(1,.12f,.008f):style==7?new Color(1.8f,1.4f,.65f):style==10?new Color(1.3f,.7f,.28f):style==11?new Color(.8f,.9f,1.05f):style==2?new Color(.48f,.38f,.24f):new Color(.035f,.039f,.046f));
                        particle.SetColor("_SecondColor",style==1?new Color(1,.83f,.28f):style==7?new Color(2.8f,2.5f,1.8f):style==10?new Color(2.1f,1.45f,.65f):style==11?new Color(1.1f,1.2f,1.3f):style==2?new Color(.95f,.82f,.58f):new Color(.39f,.43f,.48f));
                        particle.SetFloat("_EmissionStrength",style==1?1.7f:style==7?2.4f:style==10?1.6f:style==11?1.2f:0);
                        particle.SetFloat("_Density",style==1?1.8f:style==2?1.7f:glow?1:1.85f);
                        particle.SetFloat("_SoftDepth",.24f);
                        particle.SetFloat("_SrcBlend",(float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        particle.SetFloat("_DstBlend",(float)(style==1||glow?UnityEngine.Rendering.BlendMode.One:UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
                        particle.SetTexture("_NormalMap",style==3||style==2?RequiredTexture("Assets/UnityTechnologies/ParticlePack/EffectExamples/Fire & Explosion Effects/Textures/SmokePuffNormal.tif"):null);
                        particle.renderQueue=3000;particle.enableInstancing=true;
                    }
                    profile.Particles.SetTexture("_BaseMap",RequiredTexture("Assets/UnityTechnologies/ParticlePack/EffectExamples/"+texture));
                    EditorUtility.SetDirty(profile.Particles);
                    AssetDatabase.SaveAssetIfDirty(profile.Particles);
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(mesh);
                AssetDatabase.SaveAssetIfDirty(fragment);
                AssetDatabase.SaveAssetIfDirty(material);
                AssetDatabase.SaveAssetIfDirty(profile);
            }
        }

        private static Material ConfigureSolidUnitBody(string id, Material source)
        {
            string path = M3SceneSetup.MaterialFolder + "/" + id + "SolidBody.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(source) { name = id + "SolidBody" };
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = source.shader;
                material.CopyPropertiesFromMaterial(source);
            }

            // 일반 고체의 뒷면이 앞면을 덮지 않게 하고 유령 재질은 투명으로 유지한다.
            material.SetFloat("_DepthWrite", 1);
            material.SetOverrideTag("RenderType", "Opaque");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        private static void Set(SerializedObject target, string field, float value) => target.FindProperty(field).floatValue = value;

        private static Mesh SaveMesh(Mesh generated,string path)
        {
            try
            {
                Mesh mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if(mesh==null)
                {
                    mesh=generated;AssetDatabase.CreateAsset(mesh,path);generated=null;
                }
                else
                {
                    // CopySerialized만 쓰면 CPU 채널과 GPU 버퍼의 형상이 달라질 수 있다.
                    mesh.Clear();mesh.indexFormat=generated.indexFormat;
                    mesh.vertices=generated.vertices;mesh.normals=generated.normals;mesh.tangents=generated.tangents;
                    mesh.uv=generated.uv;mesh.colors=generated.colors;mesh.triangles=generated.triangles;
                    mesh.bounds=generated.bounds;mesh.name=generated.name;
                    mesh.UploadMeshData(false);EditorUtility.SetDirty(mesh);
                }
                return mesh;
            }
            finally{if(generated!=null)UnityEngine.Object.DestroyImmediate(generated);}
        }

        private static void PrepareSurfaceImports()
        {
            foreach(string id in new[]{"rock_08","mossy_rock"})
            {
                foreach(string kind in new[]{"diff","nor_gl","rough"})
                {
                    string path=SurfacePath(id,kind);
                    var importer=AssetImporter.GetAtPath(path) as TextureImporter;
                    if(importer==null)throw new InvalidOperationException("표면 자료가 없습니다: "+path);
                    var type=kind=="nor_gl"?TextureImporterType.NormalMap:TextureImporterType.Default;
                    bool srgb=kind=="diff";
                    if(importer.textureType!=type||importer.sRGBTexture!=srgb||importer.flipGreenChannel)
                    {
                        importer.textureType=type;importer.sRGBTexture=srgb;importer.flipGreenChannel=false;
                        importer.SaveAndReimport();
                    }
                }
            }
            var reflection=AssetImporter.GetAtPath(ReflectionPath) as TextureImporter;
            if(reflection==null)throw new InvalidOperationException("원소 반사 자료가 없습니다: "+ReflectionPath);
            var settings=new TextureImporterSettings();reflection.ReadTextureSettings(settings);
            var platform=reflection.GetDefaultPlatformTextureSettings();
            if(settings.textureShape!=TextureImporterShape.TextureCube||settings.cubemapConvolution!=TextureImporterCubemapConvolution.Specular||
                !settings.mipmapEnabled||settings.sRGBTexture||platform.format!=TextureImporterFormat.RGBAHalf)
            {
                settings.textureShape=TextureImporterShape.TextureCube;settings.generateCubemap=TextureImporterGenerateCubemap.AutoCubemap;
                settings.cubemapConvolution=TextureImporterCubemapConvolution.Specular;settings.seamlessCubemap=true;
                settings.mipmapEnabled=true;settings.sRGBTexture=false;reflection.SetTextureSettings(settings);
                platform.format=TextureImporterFormat.RGBAHalf;platform.maxTextureSize=256;reflection.SetPlatformTextureSettings(platform);
                reflection.textureCompression=TextureImporterCompression.Uncompressed;
                reflection.SaveAndReimport();
            }
        }

        private static string SurfacePath(string id,string kind)=>"Assets/_Project/Art/Surfaces/"+id+"/"+id+"_"+kind+"_1k.jpg";
        private static Texture RequiredTexture(string path)=>AssetDatabase.LoadAssetAtPath<Texture>(path)??throw new InvalidOperationException("표현 텍스처가 없습니다: "+path);

        private static void ConfigureSurface(Material material,int style)
        {
            bool stone=style==5||style==8||style==9||style==11||style==12||style==13;
            string surface=style==13?"mossy_rock":"rock_08";
            material.SetTexture("_SurfaceTex",stone?RequiredTexture(SurfacePath(surface,"diff")):null);
            material.SetTexture("_RoughnessTex",stone?RequiredTexture(SurfacePath(surface,"rough")):null);
            material.SetTexture("_NormalMap",stone?RequiredTexture(SurfacePath(surface,"nor_gl")):
                style==0?RequiredTexture("Assets/UnityTechnologies/ParticlePack/EffectExamples/Water Effects/Textures/WaterNormal.tif"):null);
            material.SetFloat("_SurfaceScale",style==13?1.6f:style==0?2.8f:1.4f);
            material.SetFloat("_SurfaceExposure", style switch { 5 => 1.8f, 11 => 1.65f, 9 or 12 or 13 => 1.35f, _ => 1 });
            material.SetFloat("_NormalStrength",style==0?.3f:style==4?.06f:style==8?.12f:.85f);
            material.SetFloat("_Roughness",style==0?.08f:style==4||style==8?.12f:style==12?.32f:.92f);
            material.SetFloat("_Metallic",style==12?.68f:0);
            material.SetFloat("_Transmission",style==0?.72f:style==4?.48f:style==8?.35f:0);
            material.SetFloat("_Absorption",style==0?.45f:.7f);
            material.SetFloat("_AmbientFill",style==10?0:style==13?.45f:style==0?.4f:.32f);
            material.SetFloat("_SurfaceReflection",style==0?.65f:style==4||style==8?.25f:.08f);
            material.SetFloat("_EmissionStrength", style switch { 6 or 10 => 2.2f, 7 => 1.7f, 11 => 2.3f, 9 or 12 => 1.2f, _ => .8f });
            material.SetFloat("_CoreOpacity",style==1?.09f:style==3?.06f:style==2?.29f:style==0?.65f:style==4?.84f:1);
            material.SetFloat("_Opacity",1);
            material.SetFloat("_OrbitSpeed",.75f);
            material.SetFloat("_Distortion",style==0?.016f:.008f);
            bool reflective=style==0||style==4||style==8||style==12;
            var reflection=reflective?AssetDatabase.LoadAssetAtPath<Cubemap>(ReflectionPath):null;
            if(reflective&&reflection==null)throw new InvalidOperationException("HDR 반사를 Cubemap으로 임포트하지 못했습니다.");
            material.SetTexture("_ReflectionCube",reflection);
            material.SetFloat("_ReflectionStrength",style==0?.55f:style==12?.4f:reflective?.35f:0);
            material.SetFloat("_ReflectionMipCount",reflection!=null?reflection.mipmapCount-1:0);
        }

        private static Vector3 Orbit(float angle,float radius,float y) => new Vector3(Mathf.Cos(angle)*radius,y,Mathf.Sin(angle)*radius);
        /// <summary>작은 형상들을 인스턴싱용 메시 하나로 결합한다.</summary>
        private sealed partial class Shape
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<Color> colors = new List<Color>();
            private readonly List<int> indices = new List<int>();

            private void Vertex(Vector3 point,Vector2 uv,float accent,float phase)
            {
                vertices.Add(point);uvs.Add(uv);colors.Add(new Color(accent,Mathf.Repeat(phase,1),0,1));
            }
            private void Triangle(int a,int b,int c)
            {
                if(Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]).sqrMagnitude<1e-14f) return;
                indices.Add(a);indices.Add(b);indices.Add(c);
            }
            private void Grid(int start,int rows,int columns)
            {
                for(int y=0;y<rows;y++) for(int x=0;x<columns;x++)
                {
                    int a=start+y*(columns+1)+x,b=a+columns+1;
                    Triangle(a,a+1,b);Triangle(a+1,b+1,b);
                }
            }
            private void Surface(Func<float,float,Vector3> point,int columns,int rows,float accent,float phase)
            {
                int start=vertices.Count;
                for(int y=0;y<=rows;y++) for(int x=0;x<=columns;x++) Vertex(point(x/(float)columns,y/(float)rows),new Vector2(x/(float)columns,y/(float)rows),accent,phase);
                Grid(start,rows,columns);
            }
            public void Tube(Func<float,Vector3> path,Func<float,float> radius,int segments,int sides,float accent,float aspect=1)
            {
                int start=vertices.Count;
                for(int j=0;j<=segments;j++)
                {
                    float t=j/(float)segments;
                    Vector3 center=path(t), tangent=(path(Mathf.Min(1,t+.002f))-path(Mathf.Max(0,t-.002f))).normalized;
                    Vector3 axis=Vector3.Cross(tangent,Mathf.Abs(tangent.y)<.95f?Vector3.up:Vector3.right).normalized;
                    Vector3 other=Vector3.Cross(tangent,axis);
                    for(int k=0;k<=sides;k++)
                    {
                        float a=k*Mathf.PI*2/sides;
                        Vertex(center+(axis*Mathf.Cos(a)+other*Mathf.Sin(a)*aspect)*radius(t),new Vector2(t,k/(float)sides),accent,accent);
                    }
                }
                Grid(start,segments,sides);
            }
            private void Face(Vector3 a,Vector3 b,Vector3 c,float accent,float phase)
            {
                int start=vertices.Count;
                Vertex(a,new Vector2(0,0),accent,phase);Vertex(b,new Vector2(.5f,1),accent,phase);Vertex(c,new Vector2(1,0),accent,phase);Triangle(start,start+1,start+2);
            }
            public Mesh Create(string name,int minimumVertices=3)
            {
                if(vertices.Count<minimumVertices || vertices.Count>2500) throw new InvalidOperationException(name+"의 메시 정점 예산을 확인하세요: "+vertices.Count);
                foreach(Vector3 vertex in vertices)
                    if(!float.IsFinite(vertex.x)||!float.IsFinite(vertex.y)||!float.IsFinite(vertex.z)) throw new InvalidOperationException(name+"의 메시 좌표가 유효하지 않습니다.");
                var mesh=new Mesh { name=name };
                try
                {
                    mesh.SetVertices(vertices);mesh.SetUVs(0,uvs);mesh.SetColors(colors);mesh.SetTriangles(indices,0);
                    mesh.RecalculateNormals();
                    Vector3[] normals=mesh.normals;
                    // 물방울·잎 끝의 중복 정점에도 유효한 법선을 제공한다.
                    for(int j=0;j<normals.Length;j++) if(normals[j].sqrMagnitude<.0001f) normals[j]=Vector3.up;
                    mesh.normals=normals;
                    mesh.RecalculateTangents();
                    mesh.RecalculateBounds();
                    // GPU 변형도 카메라 절두체 판정에 포함한다. CPU footprint는 원래 정점 반경을 쓴다.
                    Bounds bounds=mesh.bounds;bounds.Expand(.36f);mesh.bounds=bounds;
                    return mesh;
                }
                catch { UnityEngine.Object.DestroyImmediate(mesh);throw; }
            }
        }
    }
}
