using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    /// <summary>시설의 결정 구체와 유동선 메시, 발광 재질을 생성한다.</summary>
    public static class FacilityEnergyArtSetup
    {
        public const string NucleusMeshPath = "Assets/_Project/Meshes/FacilityNucleus.asset";
        public const string NucleusMaterialPath = "Assets/_Project/Materials/FacilityNucleus.mat";
        public const string CohesionMeshPath = "Assets/_Project/Meshes/FacilityCohesionCurrents.asset";
        public const string CohesionMaterialPath = "Assets/_Project/Materials/FacilityCohesionCurrents.mat";
        public const string IntakeMaterialPath = "Assets/_Project/Materials/FacilityIntake.mat";
        public const string GroundGlowMaterialPath = "Assets/_Project/Materials/FacilityGroundGlow.mat";

        private const int CurrentCount = 16;
        private const int CurrentSegments = 30;
        private const int TubeSides = 4;

        /// <summary>Editor에서 생성 에셋을 갱신하며 기존 참조와 재질의 색·강도 조정을 보존한다.</summary>
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Play 중에는 시설 발광 자산을 구성할 수 없습니다.");

            Shader crystal = LoadShader("FacilityCrystal");
            Shader energy = LoadShader("FacilityEnergy");
            Shader groundGlow = LoadShader("FacilityGroundGlow");
            CheckAssetType<Mesh>(NucleusMeshPath);
            CheckAssetType<Mesh>(CohesionMeshPath);
            CheckAssetType<Material>(NucleusMaterialPath);
            CheckAssetType<Material>(CohesionMaterialPath);
            CheckAssetType<Material>(IntakeMaterialPath);
            CheckAssetType<Material>(GroundGlowMaterialPath);

            SaveNucleus();
            SaveCurrents();
            SaveMaterial(NucleusMaterialPath, crystal, false);
            SaveMaterial(CohesionMaterialPath, energy, false);
            SaveMaterial(IntakeMaterialPath, energy, true);
            SaveMaterial(GroundGlowMaterialPath, groundGlow, false);
        }

        private static Shader LoadShader(string name)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Shaders/" + name + ".shader");
            if (shader == null || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("시설 발광 셰이더 컴파일을 확인하세요. " + name);
            return shader;
        }

        private static void CheckAssetType<T>(string path) where T : UnityEngine.Object
        {
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null && !(existing is T))
                throw new InvalidOperationException("시설 발광 자산 경로에 다른 종류의 자산이 있습니다. " + path);
        }

        private static void SaveNucleus()
        {
            const float golden = 1.618034f;
            var corners = new[]
            {
                new Vector3(-1, golden, 0), new Vector3(1, golden, 0),
                new Vector3(-1, -golden, 0), new Vector3(1, -golden, 0),
                new Vector3(0, -1, golden), new Vector3(0, 1, golden),
                new Vector3(0, -1, -golden), new Vector3(0, 1, -golden),
                new Vector3(golden, 0, -1), new Vector3(golden, 0, 1),
                new Vector3(-golden, 0, -1), new Vector3(-golden, 0, 1)
            };
            int[] faces =
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };
            var vertices = new List<Vector3>(240);
            var facetData = new List<Vector4>(240);
            var indices = new List<int>(240);
            for (int face = 0; face < faces.Length; face += 3)
            {
                Vector3 a = corners[faces[face]].normalized;
                Vector3 b = corners[faces[face + 1]].normalized;
                Vector3 c = corners[faces[face + 2]].normalized;
                Vector3 ab = (a + b).normalized;
                Vector3 bc = (b + c).normalized;
                Vector3 ca = (c + a).normalized;
                AddFacet(a, ab, ca);
                AddFacet(ab, b, bc);
                AddFacet(ca, bc, c);
                AddFacet(ab, bc, ca);
            }
            SaveMesh(NucleusMeshPath, vertices, facetData, indices);

            void AddFacet(Vector3 a, Vector3 b, Vector3 c)
            {
                int first = vertices.Count;
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), a) <= 0f)
                    throw new InvalidOperationException("결정 구체의 삼각 면 방향이 뒤집혔습니다.");

                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
                // 꼭짓점마다 다른 좌표를 넣어 셰이더에서 면의 세 경계를 그린다.
                float brightness = Mathf.Lerp(.52f, 1.3f, Mathf.Repeat(first * .173f, 1f));
                facetData.Add(new Vector4(1, 0, 0, brightness));
                facetData.Add(new Vector4(0, 1, 0, brightness));
                facetData.Add(new Vector4(0, 0, 1, brightness));
                indices.Add(first);
                indices.Add(first + 1);
                indices.Add(first + 2);
            }
        }

        private static void SaveCurrents()
        {
            var vertices = new List<Vector3>(CurrentCount * (CurrentSegments + 1) * TubeSides);
            var flowData = new List<Vector4>(vertices.Capacity);
            var colors = new List<Color>(vertices.Capacity);
            var indices = new List<int>(CurrentCount * CurrentSegments * TubeSides * 6);
            for (int current = 0; current < CurrentCount; current++)
            {
                int first = vertices.Count;
                float phase = Mathf.Repeat(current * .618034f, 1f);
                float width = Mathf.Lerp(.010f, .022f, phase);
                float brightness = Mathf.Lerp(.32f, .82f, phase);
                for (int ring = 0; ring <= CurrentSegments; ring++)
                {
                    float t = (float)ring / CurrentSegments;
                    Vector3 center = CurrentCenter(current, phase, t);
                    Vector3 tangent = (CurrentCenter(current, phase, t + .002f) - center).normalized;
                    Vector3 side = Vector3.Cross(tangent, Vector3.up).normalized;
                    Vector3 up = Vector3.Cross(tangent, side);
                    float radius = width * Mathf.Lerp(.35f, 1f, Mathf.Sin(t * Mathf.PI * .86f));
                    for (int point = 0; point < TubeSides; point++)
                    {
                        float angle = point * (2f * Mathf.PI / TubeSides);
                        vertices.Add(center + (side * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius);
                        flowData.Add(new Vector4(t, phase, 0, 0));
                        colors.Add(new Color(1, 1, 1, brightness));
                    }
                    if (ring == 0) continue;
                    int previous = first + (ring - 1) * TubeSides;
                    int next = previous + TubeSides;
                    for (int point = 0; point < TubeSides; point++)
                    {
                        int after = (point + 1) % TubeSides;
                        indices.Add(previous + point);
                        indices.Add(previous + after);
                        indices.Add(next + point);
                        indices.Add(previous + after);
                        indices.Add(next + after);
                        indices.Add(next + point);
                    }
                }
            }

            foreach (Vector3 point in vertices)
            {
                if (point.y < .30f || point.y > 1.54f || point.x * point.x + point.z * point.z > .70f * .70f)
                    throw new InvalidOperationException("응집로 유동선이 프레임 내부 범위를 벗어났습니다.");
            }
            SaveMesh(CohesionMeshPath, vertices, flowData, indices, colors);
        }

        private static Vector3 CurrentCenter(int current, float phase, float t)
        {
            // 상단은 가늘게 모이고 하단에서 빠르게 휘어 바닥 가까이 감긴다.
            float y = Mathf.Lerp(1.45f - .29f * phase, .34f + .10f * phase, t);
            float radius = Mathf.Lerp(.095f, .17f, phase) + t * t * Mathf.Lerp(.27f, .48f, phase);
            float turns = .28f * t + .92f * t * t * t;
            float angle = 2f * Mathf.PI * ((float)current / CurrentCount + turns);
            return new Vector3(radius * Mathf.Cos(angle), y, radius * Mathf.Sin(angle));
        }

        private static void SaveMesh(string path, List<Vector3> vertices, List<Vector4> uv, List<int> indices, List<Color> colors = null)
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool create = mesh == null;
            if (create) mesh = new Mesh { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            else mesh.Clear(false);
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            if (colors != null) mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            if (create) AssetDatabase.CreateAsset(mesh, path);
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssetIfDirty(mesh);
        }

        private static void SaveMaterial(string path, Shader shader, bool spark)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = material == null;
            if (create) material = new Material(shader);
            else material.shader = shader;
            material.enableInstancing = !spark;
            if (material.HasProperty("_Spark"))
            {
                material.SetFloat("_Spark", spark ? 1f : 0f);
                if (spark)
                {
                    material.EnableKeyword("_FACILITY_SPARK");
                    if (create)
                    {
                        material.SetColor("_BaseColor", new Color(1.8f, .65f, .12f));
                        material.SetColor("_PeakColor", new Color(5.0f, 3.8f, 2.4f));
                    }
                }
                else material.DisableKeyword("_FACILITY_SPARK");
            }
            if (create) AssetDatabase.CreateAsset(material, path);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
        }
    }
}
