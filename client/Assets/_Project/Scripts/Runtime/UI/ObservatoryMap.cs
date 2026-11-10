using System;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.UI
{
    /// <summary>서버의 탐색 마스크와 현재 관측된 개체를 한 장의 미니맵에 그린다.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class ObservatoryMap : MaskableGraphic, IPointerClickHandler, IPointerDownHandler,
        IPointerUpHandler, IInitializePotentialDragHandler, IDragHandler, IEndDragHandler, IPointerExitHandler
    {
        private static readonly Color32 Unknown = new Color32(5, 13, 19, 255);
        private static readonly Color32 Explored = new Color32(31, 53, 57, 255);
        private static readonly Color32 Visible = new Color32(65, 103, 102, 255);
        private RawImage terrain;
        private Texture2D texture;
        private Color32[] pixels;
        private int[] gridSamples;
        private int gridWidth, resolution, owner;
        private float mapSize, cellSize;
        private FogStateReplica fog;
        private SnapshotTimeline timeline;
        private GameplayFrame observation;
        [SerializeField, Range(1, 6), Tooltip("현재 관측 중인 후보 표식의 반경(화면 픽셀)입니다.")]
        private float candidateMarkerRadius = 3;
        private WorldPoint basePosition, legionPosition;
        private bool hasBase, hasLegion, rallying;
        private readonly PlayerColorCache ownerColors = new PlayerColorCache();
        internal event Action<WorldPoint> FocusRequested;
        internal event Action<WorldPoint?, bool, bool> MovePointerChanged;
        private bool leftHeld, rightHeld;
        internal bool RightHeld => rightHeld;

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            candidateMarkerRadius = Mathf.Clamp(candidateMarkerRadius, 1, 6);
        }
#endif

        internal void Initialize(RawImage backdrop, GameRules rules)
        {
            terrain = backdrop;
            raycastTarget = true;
            ConfigureWorld(rules);
        }

        void IPointerClickHandler.OnPointerClick(PointerEventData pointer)
        {
            if (pointer.button == PointerEventData.InputButton.Left &&
                TryGetWorldPoint(pointer.position, pointer.pressEventCamera, out WorldPoint position))
                FocusRequested?.Invoke(position);
        }

        internal bool TryGetWorldPoint(Vector2 screen, UnityEngine.Camera eventCamera, out WorldPoint position)
        {
            position = default;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, screen, eventCamera, out Vector2 local)) return false;

            Rect rect = rectTransform.rect;
            if (!rect.Contains(local)) return false;
            Vector2 uv = Rect.PointToNormalized(rect, local);
            position = new WorldPoint(uv.x * mapSize, uv.y * mapSize);
            return true;
        }

        void IPointerDownHandler.OnPointerDown(PointerEventData pointer)
        {
            if (!TryGetWorldPoint(pointer.position, pointer.pressEventCamera, out WorldPoint position)) return;
            if (pointer.button == PointerEventData.InputButton.Left)
            {
                leftHeld = true;
                FocusRequested?.Invoke(position);
            }
            else if (pointer.button == PointerEventData.InputButton.Right)
            {
                rightHeld = true;
                MovePointerChanged?.Invoke(position, true, true);
            }
        }

        void IDragHandler.OnDrag(PointerEventData pointer)
        {
            if (!TryGetWorldPoint(pointer.position, pointer.pressEventCamera, out WorldPoint position))
            { ResetPointer(); return; }
            if (pointer.button == PointerEventData.InputButton.Left && leftHeld) FocusRequested?.Invoke(position);
            else if (pointer.button == PointerEventData.InputButton.Right && rightHeld)
                MovePointerChanged?.Invoke(position, true, false);
        }

        void IInitializePotentialDragHandler.OnInitializePotentialDrag(PointerEventData pointer) => pointer.useDragThreshold = false;

        void IPointerUpHandler.OnPointerUp(PointerEventData pointer) => ReleasePointer(pointer);
        void IEndDragHandler.OnEndDrag(PointerEventData pointer) => ReleasePointer(pointer);
        void IPointerExitHandler.OnPointerExit(PointerEventData pointer) => ResetPointer();

        private void ReleasePointer(PointerEventData pointer)
        {
            if (pointer.button == PointerEventData.InputButton.Left) leftHeld = false;
            else if (pointer.button == PointerEventData.InputButton.Right && rightHeld)
            {
                rightHeld = false;
                if (TryGetWorldPoint(pointer.position, pointer.pressEventCamera, out WorldPoint position))
                    MovePointerChanged?.Invoke(position, false, false);
                else MovePointerChanged?.Invoke(null, false, false);
            }
        }

        private void ResetPointer()
        {
            bool cancelMove = rightHeld;
            leftHeld = rightHeld = false;
            if (cancelMove) MovePointerChanged?.Invoke(null, false, false);
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ResetPointer();
        }

        protected override void OnDisable()
        {
            ResetPointer();
            base.OnDisable();
        }

        internal void ConfigureWorld(GameRules rules)
        {
            if (mapSize == rules.MapSize && cellSize == rules.CellSize) return;
            if (texture != null)
            {
                if (Application.isPlaying) Destroy(texture);
                else DestroyImmediate(texture);
            }
            gridWidth = Mathf.CeilToInt(rules.MapSize / rules.CellSize);
            mapSize = rules.MapSize;
            cellSize = rules.CellSize;
            resolution = Mathf.Min(160, gridWidth);
            texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
            { name = "탐색 지도", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            pixels = new Color32[resolution * resolution];
            gridSamples = new int[resolution];
            for (int index = 0; index < resolution; index++)
                gridSamples[index] = Mathf.Min(gridWidth - 1, (int)((index + 0.5f) / resolution * mapSize / cellSize));
            terrain.texture = texture;
            ResetMap();
        }

        internal void ObserveFog(FogStateReplica state)
        {
            fog = state;
            for (int y = 0; y < resolution; y++)
                for (int x = 0; x < resolution; x++)
                {
                    int cell = gridSamples[x] + gridSamples[y] * gridWidth;
                    pixels[x + y * resolution] = IsSet(state.Current[cell >> 3], cell) ? Visible :
                        IsSet(state.Explored[cell >> 3], cell) ? Explored : Unknown;
                }
            Upload();
        }

        internal void ObserveUnits(SnapshotTimeline latest, int player)
        {
            timeline = latest;
            owner = player;
            SetVerticesDirty();
        }

        internal void ObservePose(WorldPoint position)
        {
            legionPosition = position;
            hasLegion = true;
            if (rallying || timeline != null) SetVerticesDirty();
        }

        internal void ObserveGameplay(GameplayFrame frame)
        {
            observation = frame;
            owner = frame.PlayerId;
            ownerColors.Observe(frame);
            hasBase = true;
            basePosition = frame.BasePosition;
            rallying = frame.IsRallying;
            SetVerticesDirty();
        }

        internal void ResetMap()
        {
            ResetPointer();
            fog = null;
            timeline = null;
            observation = null;
            ownerColors.Clear();
            hasBase = hasLegion = rallying = false;
            if (pixels == null) return;
            for (int index = 0; index < pixels.Length; index++) pixels[index] = Unknown;
            Upload();
        }

        private void Upload()
        {
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            SetVerticesDirty();
        }

        private static bool IsSet(byte value, int cell) => (value & (1 << (cell & 7))) != 0;

        private bool CurrentlyVisible(WorldPoint position)
        {
            if (fog == null || !fog.Ready) return false;
            int x = Mathf.Clamp((int)(position.X / cellSize), 0, gridWidth - 1);
            int y = Mathf.Clamp((int)(position.Y / cellSize), 0, gridWidth - 1);
            int cell = x + y * gridWidth;
            return IsSet(fog.Current[cell >> 3], cell);
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (mapSize <= 0) return;
            Rect rect = rectTransform.rect;
            Vector2 Point(WorldPoint position) => new Vector2(
                rect.xMin + Mathf.Clamp01(position.X / mapSize) * rect.width,
                rect.yMin + Mathf.Clamp01(position.Y / mapSize) * rect.height);

            Color32 ownerTint = ownerColors.Get(owner);
            if (hasBase) Polygon(mesh, Point(basePosition), 4, 4, ownerTint);
            if (hasLegion) Polygon(mesh, Point(legionPosition), 5, 8, ownerTint);
            if (observation != null && observation.SpatialReady)
                foreach (ConversionCandidateView candidate in observation.Candidates)
                    if (candidate.EndTick > observation.Tick && CurrentlyVisible(candidate.Position))
                        Polygon(mesh, Point(candidate.Position), Mathf.Clamp(candidateMarkerRadius, 1, 6), 4, ObservatoryUi.Gold);
            if (timeline == null) return;

            for (int index = 0; index < timeline.Count; index++)
            {
                if (!timeline.TryGetVisibleLatest(index, out NetworkUnit unit)) continue;
                bool own = unit.OwnerId == owner;
                if (!own && !CurrentlyVisible(unit.Position)) continue;
                Vector2 position = Point(unit.Position);
                if (own && unit.Garrison)
                {
                    Polygon(mesh, position, 2.5f, 8, ownerTint);
                }
                else if (!own)
                {
                    bool neutral = unit.OwnerId < 0;
                    Polygon(mesh, position, 3.5f, neutral ? 4 : 3, ownerColors.Get(unit.OwnerId));
                }
            }
            if (!rallying || !hasLegion || !hasBase) return;
            Vector2 origin = Point(basePosition);
            Vector2 target = Point(legionPosition);
            float distance = Vector2.Distance(origin, target);
            if (distance < 5) return;
            Vector2 direction = (target - origin) / distance;
            Vector2 normal = new Vector2(-direction.y, direction.x) * 0.7f;
            for (float offset = 0; offset < distance - 4; offset += 7)
            {
                Vector2 start = origin + direction * offset;
                Vector2 end = origin + direction * Mathf.Min(offset + 4, distance - 4);
                int first = mesh.currentVertCount;
                mesh.AddVert(start + normal, ownerTint, Vector2.zero);
                mesh.AddVert(start - normal, ownerTint, Vector2.zero);
                mesh.AddVert(end - normal, ownerTint, Vector2.zero);
                mesh.AddVert(end + normal, ownerTint, Vector2.zero);
                mesh.AddTriangle(first, first + 1, first + 2);
                mesh.AddTriangle(first, first + 2, first + 3);
            }
        }

        private static void Polygon(VertexHelper mesh, Vector2 center, float radius, int sides, Color32 color)
        {
            int start = mesh.currentVertCount;
            mesh.AddVert(center, color, Vector2.zero);
            for (int index = 0; index < sides; index++)
            {
                float angle = Mathf.PI * 0.5f + index * (2 * Mathf.PI / sides);
                mesh.AddVert(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, color, Vector2.zero);
            }
            for (int index = 0; index < sides; index++)
                mesh.AddTriangle(start, start + 1 + index, start + 1 + (index + 1) % sides);
        }

        protected override void OnDestroy()
        {
            if (texture != null)
            {
                if (Application.isPlaying) Destroy(texture);
                else DestroyImmediate(texture);
            }
            base.OnDestroy();
        }
    }
}
