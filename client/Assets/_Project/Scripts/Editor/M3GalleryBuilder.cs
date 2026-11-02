using System;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.FogOfWar;
using ElementalLegion.Shared.Data;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ElementalLegion.Client.Editor
{
    public static class M3GalleryBuilder
    {
        public const string ScenePath="Assets/_Project/Scenes/ElementGallery.unity";
        [MenuItem("Elemental Legion/Create M3 Effect Gallery")]
        public static void Create()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("Play 중에는 구성할 수 없습니다.");
            if(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath)!=null)
            {
                if(SceneManager.GetSceneByPath(ScenePath).isLoaded)throw new InvalidOperationException("열린 갤러리 씬의 변경을 먼저 보존하세요.");
                Scene active=SceneManager.GetActiveScene();Scene existing=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Additive);
                try
                {
                    foreach(GameObject item in existing.GetRootGameObjects())
                    {
                        var visuals=item.GetComponent<ElementalVisuals>();if(visuals!=null){Set(visuals,"targetCamera",item.GetComponentInChildren<UnityEngine.Camera>(true));Set(visuals,"gameData",AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json"));Set(visuals,"healthBar",AssetDatabase.LoadAssetAtPath<Mesh>("Assets/_Project/Meshes/UnitHealthBar.asset"));var serialized=new SerializedObject(visuals);serialized.FindProperty("bodySize").floatValue=1.6f;serialized.ApplyModifiedPropertiesWithoutUndo();}
                        foreach(var text in item.GetComponentsInChildren<TextMeshPro>(true))text.fontSize=text.text.StartsWith("14 ")?10.4f:text.transform.localPosition.z< -9?6:8;
                    }
                    EditorSceneManager.SaveScene(existing);
                }
                finally{EditorSceneManager.CloseScene(existing,true);if(active.IsValid()&&active.isLoaded)SceneManager.SetActiveScene(active);}
                return;
            }
            Scene original=SceneManager.GetActiveScene();Scene scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);SceneManager.SetActiveScene(scene);
            try{CreateFixture();EditorSceneManager.SaveScene(scene,ScenePath);AssetDatabase.SaveAssets();}
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid()&&original.isLoaded)SceneManager.SetActiveScene(original);}
        }
        public static GameObject CreateFixture()
        {
            GameObject prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);
            var root=new GameObject("Element Gallery");root.transform.position=new Vector3(840,0,840);
            var audio=root.AddComponent<CombatAudio>();EditorUtility.CopySerialized(prefab.GetComponent<CombatAudio>(),audio);
            var visuals=root.AddComponent<ElementalVisuals>();EditorUtility.CopySerialized(prefab.GetComponent<ElementalVisuals>(),visuals);Set(visuals,"audioPool",audio);
            var gallery=root.AddComponent<EffectGallery>();Set(gallery,"visuals",visuals);
            var cameraGo=new GameObject("Gallery Camera",typeof(UnityEngine.Camera),typeof(AudioListener));cameraGo.transform.SetParent(root.transform,false);cameraGo.transform.localPosition=new Vector3(0,43,-22);cameraGo.transform.rotation=Quaternion.LookRotation(new Vector3(0,-43,22));
            var camera=cameraGo.GetComponent<UnityEngine.Camera>();camera.orthographic=true;camera.orthographicSize=19;camera.nearClipPlane=0.1f;camera.farClipPlane=200;camera.backgroundColor=new Color(0.015f,0.025f,0.035f);camera.clearFlags=CameraClearFlags.SolidColor;
            Set(visuals,"targetCamera",camera);
            var fog=cameraGo.AddComponent<FogPresentation>();EditorUtility.CopySerialized(prefab.GetComponentInChildren<FogPresentation>(true),fog);Set(fog,"targetCamera",camera);
            var galleryFog=root.AddComponent<GalleryFog>();Set(galleryFog,"gameData",AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json"));Set(galleryFog,"fog",fog);
            var sun=new GameObject("Gallery Sun",typeof(Light));sun.transform.SetParent(root.transform,false);sun.transform.rotation=Quaternion.Euler(45,-35,0);var light=sun.GetComponent<Light>();light.type=LightType.Directional;light.color=new Color(0.8f,0.87f,1);light.intensity=1;Set(fog,"directionalLight",light);
            var ground=GameObject.CreatePrimitive(PrimitiveType.Plane);ground.name="Gallery Ground";ground.transform.SetParent(root.transform,false);ground.transform.localScale=Vector3.one*14;
            string materialPath="Assets/_Project/Materials/Elements/GalleryGround.mat";var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));material.SetColor("_BaseColor",new Color(0.07f,0.09f,0.11f));material.SetFloat("_Smoothness",0.35f);AssetDatabase.CreateAsset(material,materialPath);}ground.GetComponent<MeshRenderer>().sharedMaterial=material;
            var data=new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);
            var labels=new GameObject("Gallery Labels");labels.transform.SetParent(root.transform,false);
            for(int index=0;index<14;index++)Label(labels.transform,(string)data.Root["elements"][index]["displayName"],new Vector3((index%7-3)*6,0.25f,6.1f-index/7*6),2);
            string[] reactions={"감전","빙결","증기","화염 확산","모래폭풍","플라즈마 폭발"};
            for(int index=0;index<6;index++)Label(labels.transform,reactions[index],new Vector3((index-2.5f)*6,0.25f,-9.5f),1.5f);
            Label(labels.transform,"14 원소 · 6 시너지 | 전투 효과 갤러리",new Vector3(0,0.2f,13.5f),2.6f);
            return root;
        }
        private static void Label(Transform parent,string caption,Vector3 position,float fontSize)
        {var go=new GameObject(caption,typeof(TextMeshPro));go.transform.SetParent(parent,false);go.transform.localPosition=position;go.transform.rotation=Quaternion.Euler(90,0,0);var text=go.GetComponent<TextMeshPro>();text.font=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Settings/UI/LegionUiSDF.asset");text.fontSize=fontSize*4;text.alignment=TextAlignmentOptions.Center;text.text=caption;text.rectTransform.sizeDelta=new Vector2(40,3);text.color=new Color(0.75f,0.84f,0.9f);}
        private static void Set(UnityEngine.Object target,string field,UnityEngine.Object value){var serialized=new SerializedObject(target);serialized.FindProperty(field).objectReferenceValue=value;serialized.ApplyModifiedPropertiesWithoutUndo();}
    }
}
