using System.Collections.Generic;
using ElementalLegion.Shared.Data;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>기획서의 두 상성 고리와 선택한 원소의 피해 배율을 표시한다.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MatchupGraph : Graphic
    {
        private const float NodeRadius = 31;
        private readonly List<(int From, int To, Vector2 Control)> edges = new();
        private Vector2[] positions;
        private Color[] colors;
        private int[] counters;
        private GameRules rules;
        private string[] names;
        private TMP_Text heading, strong, weak, cross;
        private int selected = -1;

        internal void Build(ObservatoryUi ui, GameRules gameRules, GameDataDocument document, string[] elementNames)
        {
            raycastTarget = false;
            rules = gameRules;
            names = elementNames;
            positions = new Vector2[names.Length];
            colors = new Color[names.Length];
            counters = new int[names.Length];
            for (int index = 0; index < names.Length; index++)
            {
                if (!ColorUtility.TryParseHtmlString((string)document.Root["elements"][index]["color"], out colors[index]))
                    colors[index] = ObservatoryUi.Accent;
                string counter = (string)document.Root["crossCounters"][rules.Elements[index].Id];
                counters[index] = counter == null ? -1 : rules.ElementIndex(counter);
            }

            BuildRing(ui, document, "nature", "자연 원소", new Vector2(280, 195));
            BuildRing(ui, document, "energy", "에너지 원소", new Vector2(840, 195));
            RectTransform details = ObservatoryUi.Panel(transform, "Selected matchup", 0, 378, 1120, 128, ObservatoryUi.Surface);
            heading = ui.Label(details, "Selection", "", 20, 20, 10, 1080, 30);
            strong = ui.Label(details, "Strong against", "", 17, 20, 44, 530, 29, ObservatoryUi.Accent);
            weak = ui.Label(details, "Weak against", "", 17, 570, 44, 530, 29, ObservatoryUi.Danger);
            cross = ui.Label(details, "Cross counters", "", 17, 20, 83, 1080, 30, ObservatoryUi.Gold);
            Select(-1);
        }

        private void BuildRing(ObservatoryUi ui, GameDataDocument document, string id, string title, Vector2 center)
        {
            ui.Label(transform, title, title, 21, center.x - 80, 0, 160, 23, ObservatoryUi.Accent)
                .alignment = TextAlignmentOptions.Center;
            var ring = document.Root["rings"][id];
            var indices = new List<int>();
            foreach (var item in ring) indices.Add(rules.ElementIndex((string)item));
            for (int index = 0; index < indices.Count; index++)
            {
                int element = indices[index];
                float angle = index * (2 * Mathf.PI / indices.Count);
                Vector2 point = positions[element] = center + new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)) * 135;
                var button = ui.Button(transform, names[element], point.x - NodeRadius, point.y - NodeRadius,
                    NodeRadius * 2, NodeRadius * 2, () => Select(selected == element ? -1 : element));
                var label = button.GetComponentInChildren<TMP_Text>();
                label.rectTransform.sizeDelta = new Vector2(NodeRadius * 2 - 8, NodeRadius * 2);
                label.rectTransform.anchoredPosition = new Vector2(4, 0);
                label.enableAutoSizing = true;
                label.fontSizeMin = 12;
                label.fontSizeMax = 17;
                button.targetGraphic.color = Color.clear;
                button.targetGraphic = label;
            }
            for (int index = 0; index < indices.Count; index++)
                for (int offset = 1; offset < indices.Count; offset++)
                {
                    int from = indices[index], to = indices[(index + offset) % indices.Count];
                    if (rules.Matchup(from, to) <= rules.Matchup(to, from)) continue;
                    Vector2 a = positions[from], b = positions[to], middle = (a + b) * 0.5f;
                    bool neighbors = offset == 1 || offset == indices.Count - 1;
                    Vector2 control = neighbors ? middle + (middle - center).normalized * 30 :
                        middle + Vector2.Perpendicular(b - a).normalized * 12;
                    edges.Add((from, to, control));
                }
        }

        private void Select(int element)
        {
            selected = element;
            heading.text = element < 0 ? "원소를 선택하면 공격 방향과 교차 상성이 표시됩니다." : names[element];
            strong.text = weak.text = cross.text = string.Empty;
            if (element >= 0)
            {
                var wins = new List<string>();
                var losses = new List<string>();
                var crossMatchups = new List<string>();
                for (int other = 0; other < names.Length; other++)
                {
                    float outgoing = rules.Matchup(element, other), incoming = rules.Matchup(other, element);
                    if (rules.Elements[element].Group == rules.Elements[other].Group)
                    {
                        if (outgoing > incoming) wins.Add($"{names[other]} ×{outgoing:0.##}");
                        if (outgoing < incoming) losses.Add($"{names[other]} ×{outgoing:0.##}");
                    }
                    else if (counters[element] == other)
                        crossMatchups.Add($"{names[element]} → {names[other]} ×{outgoing:0.##}");
                    else if (counters[other] == element)
                        crossMatchups.Add($"{names[other]} → {names[element]} ×{incoming:0.##}");
                }
                strong.text = "공격 유리   " + string.Join("  ·  ", wins);
                weak.text = "공격 불리   " + string.Join("  ·  ", losses);
                if (crossMatchups.Count > 0) cross.text = "교차 상성   " + string.Join("  ·  ", crossMatchups);
            }
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (positions == null) return;
            foreach (var edge in edges)
            {
                bool activeGroup = selected >= 0 && rules.Elements[edge.From].Group == rules.Elements[selected].Group;
                DrawArrow(mesh, edge.From, edge.To, edge.Control,
                    new Color(0.53f, 0.65f, 0.69f, activeGroup ? 0.10f : 0.38f), 1.5f);
            }
            foreach (var edge in edges)
            {
                if (edge.From == selected) DrawArrow(mesh, edge.From, edge.To, edge.Control, ObservatoryUi.Accent, 3);
                else if (edge.To == selected) DrawArrow(mesh, edge.From, edge.To, edge.Control, ObservatoryUi.Danger, 3);
            }
            for (int index = 0; index < positions.Length; index++)
            {
                bool counter = selected >= 0 && (counters[selected] == index || counters[index] == selected);
                if (index == selected || counter)
                    Disc(mesh, positions[index], NodeRadius + 6, counter ? ObservatoryUi.Gold : ObservatoryUi.Accent);
                Disc(mesh, positions[index], NodeRadius + 1, colors[index]);
                Disc(mesh, positions[index], NodeRadius - 1.5f, Color.Lerp(ObservatoryUi.Surface, colors[index], 0.20f));
            }
        }

        private void DrawArrow(VertexHelper mesh, int from, int to, Vector2 control, Color tint, float width)
        {
            Vector2 a = positions[from], b = positions[to];
            Vector2 start = a + (control - a).normalized * (NodeRadius + 7);
            Vector2 end = b + (control - b).normalized * (NodeRadius + 8);
            Vector2 last = start;
            const int segments = 8;
            for (int segment = 1; segment <= segments; segment++)
            {
                float t = segment / (float)segments;
                Vector2 point = Vector2.Lerp(Vector2.Lerp(start, control, t), Vector2.Lerp(control, end, t), t);
                Vector2 normal = Vector2.Perpendicular(point - last).normalized * (width * 0.5f);
                int first = mesh.currentVertCount;
                Vertex(mesh, last + normal, tint);
                Vertex(mesh, last - normal, tint);
                Vertex(mesh, point - normal, tint);
                Vertex(mesh, point + normal, tint);
                mesh.AddTriangle(first, first + 1, first + 2);
                mesh.AddTriangle(first, first + 2, first + 3);
                last = point;
            }
            Vector2 tail = end + (control - end).normalized * 9;
            Vector2 wing = Vector2.Perpendicular(end - tail).normalized * 4;
            int tip = mesh.currentVertCount;
            Vertex(mesh, end, tint);
            Vertex(mesh, tail + wing, tint);
            Vertex(mesh, tail - wing, tint);
            mesh.AddTriangle(tip, tip + 1, tip + 2);
        }

        private static void Disc(VertexHelper mesh, Vector2 center, float radius, Color color)
        {
            const int segments = 32;
            int first = mesh.currentVertCount;
            Vertex(mesh, center, color);
            for (int index = 0; index < segments; index++)
            {
                float angle = index * (2 * Mathf.PI / segments);
                Vertex(mesh, center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, color);
                mesh.AddTriangle(first, first + 1 + index, first + 1 + (index + 1) % segments);
            }
        }

        private static void Vertex(VertexHelper mesh, Vector2 point, Color tint)
            => mesh.AddVert(new Vector3(point.x, -point.y), tint, Vector2.zero);
    }
}
