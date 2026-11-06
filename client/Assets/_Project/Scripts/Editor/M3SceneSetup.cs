using System;
using System.IO;
using System.Linq;
using ElementalLegion.Client.Bootstrap;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    /// <summary>기존 Legion 프리팹에 M3 참조를 명시적으로 추가한다. Renderer Feature와 사용자 씬은 보존한다.</summary>
    public static class M3SceneSetup
    {
        public const string ArtFolder="Assets/_Project/Settings/Elements";
        public const string MaterialFolder="Assets/_Project/Materials/Elements";
        private static readonly Color Panel=new Color(0.025f,0.04f,0.055f,0.96f);
        private static readonly int[] Styles={5,4,1,3,0,2,6,10,12,8,11,7,9,13};
        private static readonly Color[] Colors={new Color(0.38f,0.24f,0.12f),new Color(0.4f,0.75f,0.9f),new Color(1,0.16f,0.025f),new Color(0.2f,0.22f,0.24f),new Color(0.04f,0.35f,0.62f),new Color(0.65f,0.75f,0.78f),new Color(0.15f,0.45f,1),new Color(0.035f,0.015f,0.055f),new Color(0.1f,0.8f,0.72f),new Color(0.5f,0.35f,0.8f),new Color(0.06f,0.04f,0.1f),new Color(1,0.84f,0.4f),new Color(0.65f,0.85f,0.16f),new Color(0.2f,0.75f,0.32f)};
        [MenuItem("Elemental Legion/Configure M3 Combat")]
        public static void Configure()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("Play 중에는 자산을 구성하지 않습니다.");
            Directory.CreateDirectory(ArtFolder);Directory.CreateDirectory(MaterialFolder);Directory.CreateDirectory("Assets/_Project/Meshes");AssetDatabase.Refresh();
            Shader bodyShader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Shaders/ElementBody.shader");
            if(bodyShader==null||ShaderUtil.ShaderHasError(bodyShader))throw new InvalidOperationException("원소 셰이더 컴파일을 먼저 확인하세요.");
            var data=new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);var rules=new GameRules(data);
            Mesh sphere=PrimitiveMesh(PrimitiveType.Sphere,"ElementSphere"),rock=CrystalMesh(),ring=RingMesh();
            var art=new ElementArtProfile[rules.Elements.Count];
            for(int index=0;index<art.Length;index++)
            {
                string id=rules.Elements[index].Id;
                Texture texture=TextureFor(id);
                Material body=MaterialAsset(MaterialFolder+"/"+id+".mat",bodyShader);
                body.SetColor("_BaseColor",Colors[index]);body.SetColor("_SecondColor",Color.Lerp(Colors[index],Color.white,0.8f));body.SetFloat("_Element",Styles[index]);body.SetTexture("_NoiseTex",texture);body.enableInstancing=true;EditorUtility.SetDirty(body);
                if(id=="water")body.SetTexture("_NoiseTex",AssetDatabase.LoadAssetAtPath<Texture>("Assets/UnityTechnologies/ParticlePack/EffectExamples/Water Effects/Textures/WaterNormal.tif"));
                if(id=="fire")body.SetColor("_SecondColor",new Color(1,0.65f,0.065f));
                Material particle=ParticleMaterial(id+"Particles",texture,Color.white);
                string path=ArtFolder+"/"+id+".asset";var profile=AssetDatabase.LoadAssetAtPath<ElementArtProfile>(path);
                if(profile==null)
                {
                    profile=ScriptableObject.CreateInstance<ElementArtProfile>();AssetDatabase.CreateAsset(profile,path);
                    Set(profile,"elementId",id);Set(profile,"style",Styles[index]);Set(profile,"body",body);Set(profile,"particles",particle);
                    Set(profile,"mesh",id=="earth"||id=="ice"||id=="crystal"?rock:sphere);Set(profile,"tint",Colors[index]);
                }
                if(id=="lightning")Set(profile,"mesh",LightningMesh());
                if(id=="earth")Set(profile,"mesh",RockMesh(sphere));
                art[index]=profile;
            }
            Material[] ownership=new Material[3];Color[] ownerColors={new Color(0.05f,0.75f,0.95f),new Color(1,0.22f,0.08f),new Color(0.65f,0.65f,0.65f)};
            for(int index=0;index<3;index++){ownership[index]=MaterialAsset(MaterialFolder+"/Owner"+index+".mat",Shader.Find("Universal Render Pipeline/Unlit"));ownership[index].SetColor("_BaseColor",ownerColors[index]);ownership[index].enableInstancing=true;EditorUtility.SetDirty(ownership[index]);}
            var reactions=new Material[6];string[] reactionTextures={"spark_01","trace_01","smoke_01","fire_01","scorch_01","star_01"};
            Color[] reactionColors={new Color(0.3f,0.65f,1),new Color(0.6f,0.9f,1),new Color(0.7f,0.8f,0.85f),new Color(1,0.35f,0.05f),new Color(0.65f,0.43f,0.2f),new Color(0.75f,0.4f,1)};
            for(int index=0;index<6;index++)reactions[index]=ParticleMaterial("Reaction"+index,KenneyTexture(reactionTextures[index]),reactionColors[index]);
            GameObject root=PrefabUtility.LoadPrefabContents(ClientSceneBuilder.PrefabPath);
            try
            {
                var audio=root.GetComponent<CombatAudio>()??root.AddComponent<CombatAudio>();var visuals=root.GetComponent<ElementalVisuals>()??root.AddComponent<ElementalVisuals>();
                SetArray(audio,"clips",new[]{"impactMetal_light_000","impactSoft_medium_000","impactBell_heavy_000","impactMining_000","impactGlass_light_000","impactWood_light_000","impactGeneric_light_000"}.Select(name=>AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/ThirdParty/Kenney/ImpactSounds/Audio/"+name+".ogg")).ToArray());
                SetArray(visuals,"profiles",art);SetArray(visuals,"ownership",ownership);SetArray(visuals,"reactions",reactions);Set(visuals,"ring",ring);Set(visuals,"crystal",rock);Set(visuals,"domeMesh",sphere);Set(visuals,"audioPool",audio);
                Set(visuals,"targetCamera",root.GetComponentInChildren<UnityEngine.Camera>(true));
                Set(visuals,"gameData",AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json"));Set(visuals,"healthBar",PrimitiveMesh(PrimitiveType.Cube,"UnitHealthBar"));
                Set(root.GetComponent<LegionPresentation>(),"visuals",visuals);
                Transform canvas=root.GetComponentInChildren<Canvas>(true).transform;
                CombatHud hud=root.GetComponentInChildren<CombatHud>(true)??CreateHud(canvas,audio);
                var bootstrap=root.GetComponent<ClientBootstrap>();Set(bootstrap,"hud",hud);Set(bootstrap,"visuals",visuals);Set(bootstrap,"audioPool",audio);
                Transform grid=root.transform.Find("MovementGrid");if(grid!=null)grid.gameObject.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root,ClientSceneBuilder.PrefabPath);
            }
            finally{PrefabUtility.UnloadPrefabContents(root);}
            var font=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");
            font.atlasPopulationMode=AtlasPopulationMode.Dynamic;
            var fontReference=new SerializedObject(font);fontReference.FindProperty("m_SourceFontFile").objectReferenceValue=AssetDatabase.LoadAssetAtPath<Font>("Assets/ThirdParty/NotoSansKR/NotoSansKR-Regular.otf");fontReference.ApplyModifiedPropertiesWithoutUndo();
            string glyphs="핵인구예약최대수비대구성양성진행중미해금소필요부족집결명령실패상한도달원또는가되지않았습니다초음량전체효과음소거공격피전향사망회복전투효과갤러리";
            foreach(var token in data.Root["elements"])glyphs+=(string)token["displayName"];
            foreach(string source in new[]{"Assets/_Project/Scripts/Runtime/UI/CombatHud.cs","Assets/_Project/Scripts/Editor/M3GalleryBuilder.cs"})glyphs+=File.ReadAllText(source);
            glyphs=new string(glyphs.Where(character=>!char.IsControl(character)).Distinct().ToArray());
            string additions=new string(glyphs.Where(character=>!font.HasCharacter(character)).ToArray());
            if(additions.Length>0)font.TryAddCharacters(additions,out _);
            if(!font.HasCharacters(glyphs))throw new InvalidOperationException("M3 글리프 누락: "+new string(glyphs.Where(character=>!font.HasCharacter(character)).ToArray()));
            font.atlasPopulationMode=AtlasPopulationMode.Static;EditorUtility.SetDirty(font);
            AssetDatabase.SaveAssets();
        }
        private static CombatHud CreateHud(Transform parent,CombatAudio audio)
        {
            var go=new GameObject("CombatHud",typeof(RectTransform),typeof(CombatHud));go.transform.SetParent(parent,false);Stretch((RectTransform)go.transform);
            var content=new GameObject("Connected",typeof(RectTransform));content.transform.SetParent(go.transform,false);Stretch((RectTransform)content.transform);
            var top=PanelObject("Economy",content.transform,new Vector2(1,1),new Vector2(-24,-24),new Vector2(752,116));
            TMP_Text economy=Text("EconomyText",top.transform,18,new Vector2(16,-12),new Vector2(720,30));
            TMP_Text composition=Text("Composition",top.transform,15,new Vector2(16,-48),new Vector2(720,56));
            var bottom=PanelObject("Training",content.transform,new Vector2(0.5f,0),new Vector2(0,20),new Vector2(1232,166));
            TMP_Text feedback=Text("Feedback",bottom.transform,15,new Vector2(14,-8),new Vector2(650,24));
            var rally=Button("Rally","G · 수비대 집결",bottom.transform,new Vector2(1014,-5),new Vector2(200,28),out _);
            var buttons=new UnityEngine.UI.Button[14];var labels=new TMP_Text[14];
            for(int index=0;index<14;index++)buttons[index]=Button("Train"+index,"",bottom.transform,new Vector2(14+index%7*173,-42-index/7*58),new Vector2(164,50),out labels[index]);
            var hud=go.GetComponent<CombatHud>();Set(hud,"content",content);Set(hud,"economy",economy);Set(hud,"composition",composition);Set(hud,"feedback",feedback);Set(hud,"rally",rally);SetArray(hud,"train",buttons);SetArray(hud,"labels",labels);
            var volume=PanelObject("AudioControls",content.transform,new Vector2(1,0),new Vector2(-24,200),new Vector2(280,146));
            var controls=volume.AddComponent<CombatAudioControls>();Set(controls,"audioPool",audio);
            var sliders=new UnityEngine.UI.Slider[3];var mutes=new UnityEngine.UI.Toggle[3];string[] names={"전체","효과음","UI"};
            for(int index=0;index<3;index++)
            {
                Text("Name"+index,volume.transform,14,new Vector2(12,-10-index*42),new Vector2(55,28)).text=names[index];
                GameObject sliderObject=UnityEngine.UI.DefaultControls.CreateSlider(new UnityEngine.UI.DefaultControls.Resources());sliderObject.name="Volume"+index;sliderObject.transform.SetParent(volume.transform,false);Place((RectTransform)sliderObject.transform,new Vector2(0,1),new Vector2(68,-10-index*42),new Vector2(142,28));sliders[index]=sliderObject.GetComponent<UnityEngine.UI.Slider>();
                GameObject toggleObject=UnityEngine.UI.DefaultControls.CreateToggle(new UnityEngine.UI.DefaultControls.Resources());toggleObject.name="Mute"+index;toggleObject.transform.SetParent(volume.transform,false);Place((RectTransform)toggleObject.transform,new Vector2(0,1),new Vector2(224,-10-index*42),new Vector2(28,28));mutes[index]=toggleObject.GetComponent<UnityEngine.UI.Toggle>();var old=toggleObject.GetComponentInChildren<UnityEngine.UI.Text>();if(old!=null)UnityEngine.Object.DestroyImmediate(old.gameObject);
            }
            SetArray(controls,"volumes",sliders);SetArray(controls,"mutes",mutes);return hud;
        }
        private static GameObject PanelObject(string name,Transform parent,Vector2 anchor,Vector2 position,Vector2 size)
        {var go=new GameObject(name,typeof(RectTransform),typeof(UnityEngine.UI.Image));go.transform.SetParent(parent,false);Place((RectTransform)go.transform,anchor,position,size);go.GetComponent<UnityEngine.UI.Image>().color=Panel;return go;}
        private static UnityEngine.UI.Button Button(string name,string caption,Transform parent,Vector2 position,Vector2 size,out TMP_Text label)
        {var go=PanelObject(name,parent,new Vector2(0,1),position,size);go.GetComponent<UnityEngine.UI.Image>().color=new Color(0.09f,0.19f,0.23f);var button=go.AddComponent<UnityEngine.UI.Button>();button.targetGraphic=go.GetComponent<UnityEngine.UI.Image>();label=Text("Label",go.transform,14,new Vector2(8,-3),size-new Vector2(16,6));label.text=caption;return button;}
        private static TMP_Text Text(string name,Transform parent,int size,Vector2 position,Vector2 dimensions)
        {var go=new GameObject(name,typeof(RectTransform),typeof(TextMeshProUGUI));go.transform.SetParent(parent,false);Place((RectTransform)go.transform,new Vector2(0,1),position,dimensions);var text=go.GetComponent<TextMeshProUGUI>();text.font=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");text.fontSize=size;text.color=new Color(0.88f,0.94f,0.96f);text.raycastTarget=false;return text;}
        private static void Place(RectTransform rect,Vector2 anchor,Vector2 position,Vector2 size){rect.anchorMin=rect.anchorMax=rect.pivot=anchor;rect.anchoredPosition=position;rect.sizeDelta=size;}
        private static void Stretch(RectTransform rect){rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;}
        private static Texture KenneyTexture(string name)=>AssetDatabase.LoadAssetAtPath<Texture>("Assets/ThirdParty/Kenney/ParticlePack/PNG (Transparent)/"+name+".png");
        private static Texture TextureFor(string id)
        {
            string pack="Assets/UnityTechnologies/ParticlePack/EffectExamples/";
            string path=id=="water"?"Legacy Particles/Textures/WaterDropParticle.tif":id=="fire"?"Fire & Explosion Effects/Textures/LargeFlame02.tif":id=="smoke"?"Fire & Explosion Effects/Textures/SmokePuff01.tif":null;
            return path==null?KenneyTexture(id=="lightning"?"spark_01":id=="ice"||id=="crystal"?"star_01":"circle_01"):AssetDatabase.LoadAssetAtPath<Texture>(pack+path);
        }
        private static Material ParticleMaterial(string name,Texture texture,Color tint)
        {
            var material=MaterialAsset(MaterialFolder+"/"+name+".mat",Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            material.SetTexture("_BaseMap",texture);material.SetColor("_BaseColor",tint);material.SetFloat("_Surface",1);material.SetFloat("_SrcBlend",(float)UnityEngine.Rendering.BlendMode.SrcAlpha);material.SetFloat("_DstBlend",(float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);material.SetFloat("_ZWrite",0);material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");material.renderQueue=3000;material.enableInstancing=true;EditorUtility.SetDirty(material);return material;
        }
        private static Material MaterialAsset(string path,Shader shader){var material=AssetDatabase.LoadAssetAtPath<Material>(path);if(material==null){material=new Material(shader);AssetDatabase.CreateAsset(material,path);}return material;}
        private static Mesh PrimitiveMesh(PrimitiveType primitive,string name)
        {string path="Assets/_Project/Meshes/"+name+".asset";Mesh mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(mesh!=null)return mesh;var go=GameObject.CreatePrimitive(primitive);mesh=UnityEngine.Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);mesh.name=name;UnityEngine.Object.DestroyImmediate(go);AssetDatabase.CreateAsset(mesh,path);return mesh;}
        private static Mesh CrystalMesh()
        {
            string path="Assets/_Project/Meshes/ElementCrystal.asset";var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(mesh!=null)return mesh;
            Vector3[] corners={Vector3.up,Vector3.down,Vector3.left,Vector3.forward,Vector3.right,Vector3.back};int[] indices={0,2,3,0,3,4,0,4,5,0,5,2,1,3,2,1,4,3,1,5,4,1,2,5};
            mesh=new Mesh{name="ElementCrystal"};var vertices=new Vector3[indices.Length];var triangles=new int[indices.Length];var uv=new Vector2[indices.Length];
            for(int index=0;index<indices.Length;index++){vertices[index]=corners[indices[index]];triangles[index]=index;uv[index]=new Vector2(vertices[index].x,vertices[index].y);}
            mesh.vertices=vertices;mesh.triangles=triangles;mesh.uv=uv;mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        private static Mesh LightningMesh()
        {
            string path="Assets/_Project/Meshes/LightningArcs.asset";var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(mesh!=null)return mesh;
            var vertices=new System.Collections.Generic.List<Vector3>();var indices=new System.Collections.Generic.List<int>();
            for(int branch=0;branch<5;branch++)for(int segment=0;segment<12;segment++)
            {
                float angle=branch*Mathf.PI*2/5;Vector3 axis=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));
                float a=segment/12f,b=(segment+1)/12f;
                Vector3 left=axis*(Mathf.Sin(segment*13.3f+branch)*0.24f+0.2f)+Vector3.up*(a*2-1);
                Vector3 right=axis*(Mathf.Sin((segment+1)*13.3f+branch)*0.24f+0.2f)+Vector3.up*(b*2-1);
                Vector3 width=Vector3.Cross((right-left).normalized,axis)*0.018f;int start=vertices.Count;
                vertices.Add(left-width);vertices.Add(left+width);vertices.Add(right-width);vertices.Add(right+width);
                indices.AddRange(new[]{start,start+1,start+2,start+2,start+1,start+3});
            }
            mesh=new Mesh{name="LightningArcs"};mesh.SetVertices(vertices);mesh.SetTriangles(indices,0);mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        private static Mesh RockMesh(Mesh sphere)
        {
            string path="Assets/_Project/Meshes/EarthFragments.asset";var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(mesh!=null)return mesh;
            mesh=UnityEngine.Object.Instantiate(sphere);mesh.name="EarthFragments";Vector3[] vertices=mesh.vertices;
            for(int index=0;index<vertices.Length;index++){Vector3 point=vertices[index];float noise=Mathf.PerlinNoise(point.x*7.3f+5,point.y*8.1f+point.z*3.4f+7);vertices[index]=Vector3.Scale(point,new Vector3(1.1f,0.8f,1))*(0.65f+noise*0.7f);}
            mesh.vertices=vertices;mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        private static Mesh RingMesh()
        {
            string path="Assets/_Project/Meshes/OwnershipRing.asset";var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);bool create=mesh==null;if(create)mesh=new Mesh{name="OwnershipRing"};const int segments=64;var vertices=new Vector3[(segments+1)*2];var indices=new int[segments*6];
            for(int index=0;index<=segments;index++){float angle=index*Mathf.PI*2/segments;vertices[index*2]=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));vertices[index*2+1]=vertices[index*2]*0.94f;if(index==segments)continue;int at=index*6,start=index*2;indices[at]=start;indices[at+1]=start+2;indices[at+2]=start+1;indices[at+3]=start+1;indices[at+4]=start+2;indices[at+5]=start+3;}
            for(int index=0;index<indices.Length;index+=3){int swap=indices[index+1];indices[index+1]=indices[index+2];indices[index+2]=swap;}
            mesh.Clear();mesh.vertices=vertices;mesh.triangles=indices;mesh.uv=vertices.Select(vertex=>new Vector2(vertex.x*0.5f+0.5f,vertex.z*0.5f+0.5f)).ToArray();mesh.RecalculateNormals();mesh.RecalculateBounds();if(create)AssetDatabase.CreateAsset(mesh,path);else EditorUtility.SetDirty(mesh);return mesh;
        }
        private static void SetArray(UnityEngine.Object target,string field,UnityEngine.Object[] values){var serialized=new SerializedObject(target);var property=serialized.FindProperty(field);property.arraySize=values.Length;for(int index=0;index<values.Length;index++)property.GetArrayElementAtIndex(index).objectReferenceValue=values[index];serialized.ApplyModifiedPropertiesWithoutUndo();}
        private static void Set(UnityEngine.Object target,string field,UnityEngine.Object value){var serialized=new SerializedObject(target);serialized.FindProperty(field).objectReferenceValue=value;serialized.ApplyModifiedPropertiesWithoutUndo();}
        private static void Set(UnityEngine.Object target,string field,string value){var serialized=new SerializedObject(target);serialized.FindProperty(field).stringValue=value;serialized.ApplyModifiedPropertiesWithoutUndo();}
        private static void Set(UnityEngine.Object target,string field,int value){var serialized=new SerializedObject(target);serialized.FindProperty(field).intValue=value;serialized.ApplyModifiedPropertiesWithoutUndo();}
        private static void Set(UnityEngine.Object target,string field,Color value){var serialized=new SerializedObject(target);serialized.FindProperty(field).colorValue=value;serialized.ApplyModifiedPropertiesWithoutUndo();}
    }
}
