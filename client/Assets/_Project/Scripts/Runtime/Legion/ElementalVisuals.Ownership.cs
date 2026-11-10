using System;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    /// <summary>원소 본체를 유지하면서 소유 색별 링과 건물 배치를 재사용한다.</summary>
    public sealed partial class ElementalVisuals
    {
        [SerializeField, Range(.06f, .4f), Tooltip("유닛 바닥 링의 두께입니다. 외반경을 유지하고 안쪽으로 넓힙니다(외반경 대비 비율).")]
        private float unitRingThickness = .08f;
        [SerializeField, Range(.005f, .05f), Tooltip("건설 범위와 건물 소속 링의 두께입니다(반경 대비 비율).")]
        private float constructionRangeThickness = .01f;
        [SerializeField, Tooltip("건설 가능한 지면 범위 테두리의 색입니다.")]
        private Color constructionRangeTint = new Color(.3f, .92f, .78f);
        [SerializeField, Range(0, .4f), Tooltip("건설 범위 안쪽 지면의 불투명도입니다.")]
        private float constructionFillOpacity = .12f;
        private const int OwnershipStart = Projectiles + 14, OwnershipKinds = 7;
        private const int OwnerRing = 0, OwnerGhostRing = 1, OwnerBuilding = 2, OwnerGhostBuilding = 3, OwnerDome = 4,
            OwnerUnitRing = 5, OwnerGhostUnitRing = 6, OwnerUnfinished = 7;
        private readonly PlayerColorCache ownerColors = new PlayerColorCache();
        private MaterialPropertyBlock[][] ownershipProperties;
        private Mesh unitOwnershipRing;
        private Mesh constructionRangeFill, constructionRangeOutline;
        private MaterialPropertyBlock constructionFillProperties, constructionOutlineProperties;

        private void InitializeOwnership()
        {
            ReleaseOwnership();
            unitOwnershipRing = CreateGroundRing(1 - Mathf.Clamp(unitRingThickness, .06f, .4f), "Unit ownership ring runtime");
            constructionRangeFill = CreateGroundRing(0, "Construction range fill runtime");
            constructionRangeOutline = CreateGroundRing(1 - Mathf.Clamp(constructionRangeThickness, .005f, .05f), "Construction range outline runtime");
            constructionFillProperties = new MaterialPropertyBlock();
            var fillTint = constructionRangeTint;
            fillTint.a = constructionFillOpacity;
            constructionFillProperties.SetColor(BaseColor, fillTint);
            // 범위 효과용 반투명 재질에서 반응 무늬를 빼고 바닥 색이 보이게 한다.
            constructionFillProperties.SetTexture("_BaseMap", Texture2D.whiteTexture);
            constructionOutlineProperties = new MaterialPropertyBlock();
            ownershipProperties = new MaterialPropertyBlock[PlayerPalette.Count + 1][];
            for (int slot = 0; slot < ownershipProperties.Length; slot++)
            {
                var properties = new MaterialPropertyBlock[OwnershipKinds + 1];
                ownershipProperties[slot] = properties;
                Color tint = PlayerColors.Get(slot);
                for (int kind = 0; kind < properties.Length; kind++)
                {
                    Material source = kind == OwnerRing || kind == OwnerUnitRing ? ownership[0] :
                        kind == OwnerGhostRing || kind == OwnerGhostUnitRing || kind == OwnerDome ? profiles[lightIndex].Body : profiles[crystalIndex].Body;
                    float opacity = kind == OwnerGhostRing || kind == OwnerGhostUnitRing || kind == OwnerGhostBuilding ? ghostOpacity :
                        kind == OwnerDome ? ghostOpacity * .5f : kind == OwnerUnfinished ? unfinishedOpacity :
                        source.HasProperty(Opacity) ? source.GetFloat(Opacity) : 1;
                    var block = Spectral(opacity);
                    if (kind == OwnerBuilding && source.HasProperty(ElementStyle)) block.SetFloat(ElementStyle, source.GetFloat(ElementStyle));
                    Color baseTint = tint;
                    if (source.HasProperty(BaseColor)) baseTint.a = source.GetColor(BaseColor).a;
                    block.SetColor(BaseColor, baseTint);
                    block.SetColor(SecondColor, tint);
                    block.SetFloat("_OwnershipTint", 1);
                    properties[kind] = block;
                }
            }
        }

        private static Mesh CreateGroundRing(float innerRadius, string meshName)
        {
            const int segments = 64;
            int indicesPerSegment = innerRadius > 0 ? 6 : 3;
            var vertices = new Vector3[(segments + 1) * 2];
            var normals = new Vector3[vertices.Length];
            var tangents = new Vector4[vertices.Length];
            var colors = new Color32[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[segments * indicesPerSegment];
            for (int index = 0; index <= segments; index++)
            {
                float angle = index * Mathf.PI * 2 / segments;
                int outer = index * 2;
                vertices[outer] = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                vertices[outer + 1] = vertices[outer] * innerRadius;
                for (int side = 0; side < 2; side++)
                {
                    int vertex = outer + side;
                    normals[vertex] = Vector3.up;
                    tangents[vertex] = new Vector4(1, 0, 0, -1);
                    colors[vertex] = new Color32(255, 255, 255, 255);
                    uv[vertex] = new Vector2(vertices[vertex].x * .5f + .5f, vertices[vertex].z * .5f + .5f);
                }
                if (index == segments) continue;
                int at = index * indicesPerSegment;
                triangles[at] = outer; triangles[at + 1] = outer + 1; triangles[at + 2] = outer + 2;
                if (indicesPerSegment == 6)
                {
                    triangles[at + 3] = outer + 1; triangles[at + 4] = outer + 3; triangles[at + 5] = outer + 2;
                }
            }
            var mesh = new Mesh { name = meshName, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices; mesh.normals = normals; mesh.tangents = tangents;
            mesh.colors32 = colors; mesh.uv = uv; mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        internal void RenderConstructionRange(System.Numerics.Vector2 origin, float radius)
        {
            if (constructionRangeFill == null || radius <= 0) return;
            var center = new Vector3(origin.X, .07f, origin.Y);
            DrawOne(constructionRangeFill, reactions[0], Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * radius), constructionFillProperties);
            center.y = .09f;
            constructionOutlineProperties.SetColor(BaseColor, constructionRangeTint);
            DrawOne(constructionRangeOutline, ownership[0], Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * radius), constructionOutlineProperties);
        }

        private void DrawConstructionOutline(Vector3 center, float radius, Color tint)
        {
            constructionOutlineProperties.SetColor(BaseColor, tint);
            DrawOne(ring, ownership[0], Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * radius), constructionOutlineProperties);
        }

        private void AddUnitOwnership(int owner, bool ghost, Matrix4x4 matrix) =>
            AddOwnership(owner, ghost ? OwnerGhostUnitRing : OwnerUnitRing, matrix);

        private void ReleaseOwnership()
        {
            if (unitOwnershipRing != null)
            {
                if (Application.isPlaying) Destroy(unitOwnershipRing);
                else DestroyImmediate(unitOwnershipRing);
            }
            if (constructionRangeFill != null)
            {
                if (Application.isPlaying) Destroy(constructionRangeFill);
                else DestroyImmediate(constructionRangeFill);
            }
            if (constructionRangeOutline != null)
            {
                if (Application.isPlaying) Destroy(constructionRangeOutline);
                else DestroyImmediate(constructionRangeOutline);
            }
            unitOwnershipRing = null;
            constructionRangeFill = null;
            constructionRangeOutline = null;
            constructionFillProperties = constructionOutlineProperties = null;
            ownershipProperties = null;
        }

        private void AddOwnership(int owner, int kind, Matrix4x4 matrix)
        {
            int group = OwnershipStart + ownerColors.Slot(owner) * OwnershipKinds + kind;
            int count = counts[group];
            if (count == matrices[group].Length)
            {
                int limit = kind == OwnerRing ? Protocol.MaxUnits * 2 + GameplayCodec.MaxBuildings :
                    kind == OwnerGhostRing ? Protocol.MaxUnits + GameplayCodec.MaxBuildings :
                    kind == OwnerUnitRing || kind == OwnerGhostUnitRing ? Protocol.MaxUnits : GameplayCodec.MaxBuildings;
                if (count >= limit) throw new InvalidOperationException("소유 색 배치가 관측 개수 상한을 초과했습니다.");
                Array.Resize(ref matrices[group], Math.Min(limit, count * 2));
            }
            matrices[group][count] = matrix;
            counts[group] = count + 1;
        }

        private void DrawOwnership()
        {
            for (int slot = 0; slot < ownershipProperties.Length; slot++)
            {
                int group = OwnershipStart + slot * OwnershipKinds;
                MaterialPropertyBlock[] properties = ownershipProperties[slot];
                DrawBatch(constructionRangeOutline, ownership[0], group + OwnerRing, properties[OwnerRing]);
                DrawBatch(constructionRangeOutline, profiles[lightIndex].Body, group + OwnerGhostRing, properties[OwnerGhostRing]);
                DrawBatch(crystal, profiles[crystalIndex].Body, group + OwnerBuilding, properties[OwnerBuilding]);
                DrawBatch(crystal, profiles[crystalIndex].Body, group + OwnerGhostBuilding, properties[OwnerGhostBuilding]);
                DrawBatch(domeMesh, profiles[lightIndex].Body, group + OwnerDome, properties[OwnerDome]);
                DrawBatch(unitOwnershipRing, ownership[0], group + OwnerUnitRing, properties[OwnerUnitRing]);
                DrawBatch(unitOwnershipRing, profiles[lightIndex].Body, group + OwnerGhostUnitRing, properties[OwnerGhostUnitRing]);
            }
        }
    }
}
