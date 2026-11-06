using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>기존 Image 참조를 유지하면서 잘린 모서리와 얇은 테두리를 그린다.</summary>
    [AddComponentMenu("Elemental Legion/UI Frame")]
    public sealed class LegionUiFrame : UnityEngine.UI.Image
    {
        [SerializeField, Min(0), Tooltip("모서리를 잘라 내는 길이(UI 기준 픽셀)입니다.")] private float cornerCut = 10;
        [SerializeField, Min(0), Tooltip("테두리 두께(UI 기준 픽셀)입니다.")] private float lineWidth = 1;
        [SerializeField, Tooltip("표시 전용 테두리 색입니다.")] private Color borderColor = new Color32(177, 155, 113, 220);
        private readonly Vector2[] outer = new Vector2[8];
        private readonly Vector2[] inner = new Vector2[8];

        internal void Style(float corner, float line, Color border)
        {
            cornerCut = corner;
            lineWidth = line;
            borderColor = border;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = GetPixelAdjustedRect();
            if (rect.width <= 0 || rect.height <= 0) return;
            float cut = Mathf.Clamp(cornerCut, 0, Mathf.Min(rect.width, rect.height) * 0.5f);
            float line = Mathf.Clamp(lineWidth, 0, Mathf.Min(rect.width, rect.height) * 0.25f);
            Contour(rect, cut, outer);
            Contour(new Rect(rect.x + line, rect.y + line, rect.width - 2 * line, rect.height - 2 * line),
                Mathf.Max(0, cut - line), inner);
            mesh.AddVert(rect.center, color, Vector2.zero);
            for (int index = 0; index < 8; index++) mesh.AddVert(outer[index], color, Vector2.zero);
            for (int index = 0; index < 8; index++) mesh.AddTriangle(0, index + 1, (index + 1) % 8 + 1);
            for (int index = 0; index < 8; index++)
            {
                int next = (index + 1) % 8;
                int start = mesh.currentVertCount;
                mesh.AddVert(outer[index], borderColor, Vector2.zero);
                mesh.AddVert(outer[next], borderColor, Vector2.zero);
                mesh.AddVert(inner[next], borderColor, Vector2.zero);
                mesh.AddVert(inner[index], borderColor, Vector2.zero);
                mesh.AddTriangle(start, start + 1, start + 2);
                mesh.AddTriangle(start, start + 2, start + 3);
            }
        }

        private static void Contour(Rect rect, float cut, Vector2[] points)
        {
            points[0] = new Vector2(rect.xMin + cut, rect.yMin);
            points[1] = new Vector2(rect.xMax - cut, rect.yMin);
            points[2] = new Vector2(rect.xMax, rect.yMin + cut);
            points[3] = new Vector2(rect.xMax, rect.yMax - cut);
            points[4] = new Vector2(rect.xMax - cut, rect.yMax);
            points[5] = new Vector2(rect.xMin + cut, rect.yMax);
            points[6] = new Vector2(rect.xMin, rect.yMax - cut);
            points[7] = new Vector2(rect.xMin, rect.yMin + cut);
        }
    }
}
