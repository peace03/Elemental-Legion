using System;
using ElementalLegion.Shared.Net;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    /// <summary>관측된 유닛을 하나의 파티클 버퍼로 표시한다. 내 군단만 예측 중심에 맞춰 이동시킨다.</summary>
    public sealed class LegionPresentation : MonoBehaviour
    {
        [SerializeField, Tooltip("군단 중심과 개발용 유닛 마커를 그릴 월드 공간 파티클입니다.")] private ParticleSystem markers;
        [SerializeField,Tooltip("원소별 배치 렌더링과 확정 전투 효과입니다.")] private ElementalVisuals visuals;
        [SerializeField,Tooltip("개발용 격자를 표시합니다.")] private bool showGrid;
        [SerializeField, Tooltip("이동 거리를 보여줄 격자 루트입니다.")] private Transform grid;
        [SerializeField, Tooltip("탐색 여부와 무관하게 표시할 바닥 재질입니다.")] private Material groundMaterial;
        [SerializeField, Tooltip("월드 바닥의 표현 색입니다.")] private Color groundTint = new Color(0.045f, 0.085f, 0.1f);
        [SerializeField, Min(0.1f), Tooltip("유닛 마커 크기(m)입니다.")] private float unitSize = 1.3f;
        [SerializeField, Min(0.1f), Tooltip("내 군단 중심 마커 크기(m)입니다.")] private float centerSize = 0.6f;
        [SerializeField, Min(0), Tooltip("개발용 유닛 마커를 바닥에서 띄울 높이(m)입니다.")] private float markerHeight = 0.3f;
        [SerializeField, Min(1), Tooltip("시각 격자의 간격(m)입니다. 게임 판정과 무관합니다.")] private float gridSpacing = 10;
        [SerializeField, Tooltip("내 유닛 표시 색입니다.")] private Color ownColor = new Color(0.2f, 0.9f, 1);
        [SerializeField, Tooltip("다른 플레이어의 유닛 표시 색입니다.")] private Color enemyColor = new Color(1, 0.35f, 0.15f);
        [SerializeField, Tooltip("중립 유닛 표시 색입니다.")] private Color neutralColor = new Color(0.8f, 0.8f, 0.9f);
        private readonly ParticleSystem.Particle[] particles = new ParticleSystem.Particle[Protocol.MaxUnits + 1];
        private Mesh groundMesh;
        private float groundSize;

        public void ConfigureGround(float size)
        {
            if (groundMaterial == null || groundSize == size) return;
            var corners = new[] { Vector3.zero, new Vector3(size, 0, 0), new Vector3(size, 0, size), new Vector3(0, 0, size) };
            groundSize = size;
            if (groundMesh != null)
            {
                groundMesh.vertices = corners;
                groundMesh.RecalculateBounds();
                return;
            }
            var surface = new GameObject("World ground", typeof(MeshFilter), typeof(MeshRenderer));
            surface.transform.SetParent(transform, false);
            surface.transform.localPosition = new Vector3(0, -0.025f, 0);
            groundMesh = new Mesh { name = "World ground" };
            groundMesh.vertices = corners;
            groundMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            groundMesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            groundMesh.RecalculateBounds();
            surface.GetComponent<MeshFilter>().sharedMesh = groundMesh;
            var renderer = surface.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = groundMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var properties = new MaterialPropertyBlock();
            properties.SetColor("_BaseColor", groundTint);
            renderer.SetPropertyBlock(properties);
        }

        private void OnDestroy()
        {
            if (groundMesh != null) Destroy(groundMesh);
        }

        public void ValidateReferences()
        {
            if (markers == null || grid == null || visuals == null) throw new InvalidOperationException("LegionPresentation 필수 참조가 없습니다.");
            markers.Clear(false);
            grid.gameObject.SetActive(showGrid);
            visuals.Initialize();
        }

        public void Render(SnapshotTimeline timeline, int owner, System.Numerics.Vector2 predicted, double renderTick)
        {
            visuals.Render(timeline,owner,predicted,renderTick);
            Set(0, predicted, Color.white, centerSize, 0);
            int count = 1;
            if (showGrid)
            {
                System.Numerics.Vector2 offset = predicted - timeline.SampleAnchor(renderTick);
                for (int index = 0; index < timeline.Count; index++)
                {
                    NetworkUnit unit = timeline.GetLatest(index);
                    if (!timeline.TrySample(unit.Id, renderTick, out var position)) continue;
                    if (unit.OwnerId == owner && !unit.Garrison) position += offset;
                    Color color = unit.OwnerId == owner ? ownColor : unit.OwnerId < 0 ? neutralColor : enemyColor;
                    Set(count++, position, color, unitSize, markerHeight);
                }
                grid.position = new Vector3(Mathf.Floor(predicted.X / gridSpacing) * gridSpacing, 0, Mathf.Floor(predicted.Y / gridSpacing) * gridSpacing);
            }
            if (!markers.isPlaying) markers.Play(false);
            markers.SetParticles(particles, count);
        }

        public void Clear()
        {
            if (markers != null) markers.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (visuals != null) visuals.Clear();
        }

        private void Set(int index, System.Numerics.Vector2 position, Color color, float size, float height)
        {
            particles[index].position = new Vector3(position.X, height, position.Y);
            particles[index].startColor = color; particles[index].startSize = size;
            particles[index].startLifetime = 1000000; particles[index].remainingLifetime = 1000000;
        }

        private void OnValidate() { unitSize = Mathf.Max(0.1f, unitSize); centerSize = Mathf.Max(0.1f, centerSize); markerHeight = Mathf.Max(0, markerHeight); gridSpacing = Mathf.Max(1, gridSpacing); }
    }
}
