using System;
using ElementalLegion.Shared.Sim;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    /// <summary>제공한 시설의 메시·재질과 지면 기준 배치를 보관한다.</summary>
    public sealed class FacilityModelCatalog : ScriptableObject
    {
        public const string AssetPath = "Assets/_Project/Settings/Rendering/FacilityModels.asset";

        public enum PartMotion : byte { None, IgnitionCrystal, IgnitionRingLower, IgnitionRingMiddle, IgnitionRingUpper }

        [Serializable]
        public struct Part
        {
            public Mesh Mesh;
            public Material Material;
            public Matrix4x4 LocalMatrix;
            public int Submesh;
            public PartMotion Motion;
        }

        [Serializable]
        public sealed class Model
        {
            public BuildingKind Kind;
            public float HorizontalRadius;
            public float Height;
            public Part[] Parts;
        }

        [SerializeField, Tooltip("지면이 0이고 수평 중심이 원점인 시설 5종입니다.")]
        private Model[] models = Array.Empty<Model>();
        public int Count => models.Length;

        public bool TryGet(BuildingKind kind, out Model model)
        {
            if (kind == BuildingKind.EnergyTower) kind = BuildingKind.NatureTower;
            foreach (Model entry in models)
                if (entry != null && entry.Kind == kind) { model = entry; return true; }
            model = null;
            return false;
        }

#if UNITY_EDITOR
        /// <summary>가져오기 도구가 검증한 조합을 저장한다. 실행 중에는 갱신하지 않는다.</summary>
        public void SetImportedModels(Model[] imported) => models = imported ?? throw new ArgumentNullException(nameof(imported));
#endif
    }
}
