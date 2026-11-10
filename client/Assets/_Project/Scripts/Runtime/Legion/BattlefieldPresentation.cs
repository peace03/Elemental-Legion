using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ElementalLegion.Client.Legion
{
    /// <summary>판정과 무관한 전장 지면·외곽 암석·조명만 소유한다.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class BattlefieldPresentation : MonoBehaviour
    {
        [Header("전장 표면")]
        [SerializeField, Tooltip("현무암 표면과 젖은 부분을 표시하는 전용 재질입니다.")] private Material groundMaterial;
        [SerializeField, Tooltip("외곽 암석에 사용할 전용 재질입니다.")] private Material rockMaterial;
        [SerializeField, Range(16, 192), Tooltip("지면 한 변의 최대 분할 수입니다. 초기화 때만 메시를 만듭니다.")] private int groundSegments = 128;
        [SerializeField, Range(0, .2f), Tooltip("0m 아래로 파이는 지면 요철의 깊이(m)입니다. 이동 판정에는 영향을 주지 않습니다.")] private float reliefDepth = .075f;
        [SerializeField, Range(0, 96), Tooltip("맵 외곽에만 표시할 낮은 장식 암석 수입니다. 충돌체는 만들지 않습니다.")] private int borderRockCount = 64;
        [SerializeField, Range(.2f, 4), Tooltip("외곽 암석의 대표 반경(m)입니다.")] private float borderRockRadius = 1.6f;
        [SerializeField, Tooltip("전장 표현을 재현할 난수 시드입니다. 게임 난수와 무관합니다.")] private int decorationSeed = 104729;
        [Header("전용 조명")]
        [SerializeField, Tooltip("암석의 위쪽 면을 읽는 약간 따뜻한 주광입니다.")] private Color keyColor = new Color(1, .9f, .76f);
        [SerializeField, Range(0, 3), Tooltip("전용 주광의 밝기입니다. 기존 씬 조명은 변경하지 않습니다.")] private float keyIntensity = 1.25f;
        [SerializeField, Tooltip("주광의 회전 각도입니다.")] private Vector3 keyEuler = new Vector3(48, -32, 0);
        [SerializeField, Tooltip("그늘에서 윤곽을 읽는 차가운 보조광입니다.")] private Color fillColor = new Color(.39f, .6f, .86f);
        [SerializeField, Range(0, 1), Tooltip("그림자를 만들지 않는 보조광의 밝기입니다.")] private float fillIntensity = .24f;
        [SerializeField, Range(0, 1), Tooltip("주광 그림자의 어두운 정도입니다.")] private float shadowStrength = .8f;

        private GameObject runtimeRoot;
        private Mesh groundMesh;
        private Mesh rockMesh;
        private float initializedSize;
        private UnityEngine.Camera initializedCamera;

        public Material GroundMaterial => groundMaterial;

        /// <summary>서버 규칙에서 받은 맵 한 변 길이(m)로 [0, mapSize] 지면을 만든다. 카메라 설정은 변경하지 않는다.</summary>
        public void Initialize(UnityEngine.Camera camera, float mapSize)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (!float.IsFinite(mapSize) || mapSize <= 0) throw new ArgumentOutOfRangeException(nameof(mapSize));
            if (groundMaterial == null || rockMaterial == null)
                throw new InvalidOperationException("BattlefieldPresentation의 groundMaterial과 rockMaterial을 연결하세요.");
            if (runtimeRoot != null && initializedSize == mapSize && initializedCamera == camera) return;
            Release();
            initializedCamera = camera;
            initializedSize = mapSize;
            runtimeRoot = new GameObject("Battlefield presentation") { hideFlags = HideFlags.DontSave, layer = gameObject.layer };
            // 서버의 XZ 월드 좌표와 일치시키며 프리팹 루트의 변환에 영향을 받지 않는다.
            runtimeRoot.transform.SetParent(transform, true);
            groundMesh = BuildGround(mapSize);
            AddSurface("Basalt ground", groundMesh, groundMaterial, false);
            if (borderRockCount > 0)
            {
                rockMesh = BuildBorderRocks(mapSize);
                AddSurface("Border basalt", rockMesh, rockMaterial, true);
            }
            AddLight("Battlefield key", keyColor, keyIntensity, keyEuler, true);
            AddLight("Battlefield fill", fillColor, fillIntensity, new Vector3(32, 145, 0), false);
        }

        /// <summary>생성한 표시물만 정리하고 빌려 쓴 재질·카메라·기존 조명은 유지한다.</summary>
        public void Release()
        {
            if (runtimeRoot != null)
            {
                runtimeRoot.SetActive(false);
                ReleaseObject(runtimeRoot);
            }
            ReleaseObject(groundMesh);
            ReleaseObject(rockMesh);
            runtimeRoot = null;
            groundMesh = null;
            rockMesh = null;
            initializedCamera = null;
            initializedSize = 0;
        }

        private void OnDisable() => Release();
        private void OnDestroy() => Release();

        private void AddSurface(string label, Mesh mesh, Material material, bool castShadows)
        {
            var surface = new GameObject(label, typeof(MeshFilter), typeof(MeshRenderer)) { layer = gameObject.layer };
            surface.transform.SetParent(runtimeRoot.transform, false);
            surface.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = surface.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.receiveShadows = true;
            renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        private void AddLight(string label, Color color, float intensity, Vector3 euler, bool shadows)
        {
            if (intensity <= 0) return;
            var source = new GameObject(label, typeof(Light)) { layer = gameObject.layer };
            source.transform.SetParent(runtimeRoot.transform, false);
            source.transform.rotation = Quaternion.Euler(euler);
            var light = source.GetComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = Mathf.Max(0, intensity);
            light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            light.shadowStrength = Mathf.Clamp01(shadowStrength);
            light.shadowBias = .035f;
            light.shadowNormalBias = .15f;
        }

        private Mesh BuildGround(float size)
        {
            int count = Mathf.Clamp(groundSegments, 16, 192);
            var vertices = new Vector3[(count + 1) * (count + 1)];
            var uv = new Vector2[vertices.Length];
            var indices = new int[count * count * 6];
            float depth = Mathf.Clamp(reliefDepth, 0, .2f);
            for (int z = 0; z <= count; z++)
            for (int x = 0; x <= count; x++)
            {
                int index = z * (count + 1) + x;
                float px = size * x / count, pz = size * z / count;
                // PerlinNoise는 0~1을 조금 벗어날 수 있으므로 지면 높이의 상·하한을 보장한다.
                float height = -depth * Mathf.Clamp01(Mathf.PerlinNoise(px * .17f + 13.8f, pz * .17f + 39.1f));
                vertices[index] = new Vector3(px, height, pz);
                uv[index] = new Vector2(px, pz);
                if (x == count || z == count) continue;
                int triangle = (z * count + x) * 6;
                indices[triangle] = index;
                indices[triangle + 1] = index + count + 1;
                indices[triangle + 2] = index + 1;
                indices[triangle + 3] = index + 1;
                indices[triangle + 4] = index + count + 1;
                indices[triangle + 5] = index + count + 2;
            }
            var mesh = new Mesh { name = "Battlefield ground runtime", hideFlags = HideFlags.DontSave };
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = indices;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Mesh BuildBorderRocks(float size)
        {
            var random = new System.Random(decorationSeed);
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var indices = new List<int>();
            int count = Mathf.Clamp(borderRockCount, 0, 96);
            for (int rock = 0; rock < count; rock++)
            {
                float radius = Mathf.Clamp(borderRockRadius, .2f, 4) * (.55f + (float)random.NextDouble() * .75f);
                radius = Mathf.Min(radius, size * .025f);
                float along = radius + (size - radius * 2) * (float)random.NextDouble();
                float edge = radius * .7f;
                Vector3 center = (rock % 4) switch
                {
                    0 => new Vector3(along, 0, edge),
                    1 => new Vector3(size - edge, 0, along),
                    2 => new Vector3(along, 0, size - edge),
                    _ => new Vector3(edge, 0, along)
                };
                var ring = new Vector3[6];
                for (int side = 0; side < 6; side++)
                {
                    float angle = (side + (float)random.NextDouble() * .3f) * Mathf.PI / 3;
                    float length = radius * (.65f + (float)random.NextDouble() * .3f);
                    ring[side] = center + new Vector3(Mathf.Cos(angle) * length, -.1f, Mathf.Sin(angle) * length);
                }
                Vector3 peak = center + new Vector3(radius * .13f, radius * (.25f + (float)random.NextDouble() * .4f), -radius * .16f);
                for (int side = 0; side < 6; side++)
                {
                    int first = vertices.Count;
                    vertices.Add(ring[side]); vertices.Add(peak); vertices.Add(ring[(side + 1) % 6]);
                    for (int index = first; index < first + 3; index++)
                    {
                        Vector3 point = vertices[index];
                        uv.Add(new Vector2(point.x, point.z));
                        indices.Add(index);
                    }
                }
            }
            var mesh = new Mesh { name = "Battlefield border runtime", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            return mesh;
        }

        private static void ReleaseObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
