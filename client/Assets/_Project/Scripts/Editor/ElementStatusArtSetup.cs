using System;
using UnityEditor;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    /// <summary>원소 본체와 분리된 보호막·빙결 외피의 공용 재질을 구성한다.</summary>
    public static class ElementStatusArtSetup
    {
        public const string MaterialPath = "Assets/_Project/Materials/UnitStatusShell.mat";

        /// <summary>기존 재질 참조를 보존하고 외피 기본값을 저장한다. Play 중에는 호출할 수 없다.</summary>
        [MenuItem("Elemental Legion/Configure Element Status Art")]
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Play 중에는 상태 외피 재질을 구성하지 않습니다.");
            Shader shader = Shader.Find("ElementalLegion/ElementStatusShell");
            if (shader == null || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("상태 외피 셰이더 컴파일을 먼저 확인하세요.");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "UnitStatusShell" };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            material.shader = shader;
            material.SetColor("_Color", new Color(.5f, .88f, 1, 1));
            material.SetFloat("_Opacity", .45f);
            material.SetFloat("_Frost", 0);
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
        }
    }
}
