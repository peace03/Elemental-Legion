using System;
using System.Collections.Generic;
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
    /// <summary>서버의 탐색 마스크와 현재 개체, 마지막으로 관측한 적 시설을 그린다.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class ObservatoryMap : MaskableGraphic, IPointerClickHandler, IPointerDownHandler,
        IPointerUpHandler, IInitializePotentialDragHandler, IDragHandler, IEndDragHandler, IPointerExitHandler
    {
        private static readonly Color32 Unknown = new Color32(5, 13, 19, 255);
        private static readonly Color32 Explored = new Color32(31, 53, 57, 255);
        private static readonly Color32 Visible = new Color32(65, 103, 102, 255);
        private static readonly Vector2[] BuildingMarkerCorners =
        {
            new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1)
        };
        private const byte AllSquadsMask = 15;
        private const float BadgeGap = 4;
        private const double PulseRefreshSeconds = 1.0 / 12;
        private RawImage terrain;
        private Texture2D texture;
        private Color32[] pixels;
        private int[] gridSamples;
        private int gridWidth, resolution, owner;
        private float mapSize, cellSize;
        private double tickSeconds;
        private FogStateReplica fog;
        private SnapshotTimeline timeline;
        private Dictionary<int, UnitMarker> units = new Dictionary<int, UnitMarker>();
        private Dictionary<int, UnitMarker> nextUnits = new Dictionary<int, UnitMarker>();
        private long lastUnitTick = -1;
        private double movingUntil, nextPulseRefresh;
        private bool hasMovingUnits, displayingMotion;
        private float movementThreshold = .2f, movementPeriod = 2.4f, movementTint = 1;
        private GameplayFrame observation;
        private BuildingMemory buildingMemory;
        [SerializeField, Range(1, 6), Tooltip("현재 관측 중인 후보 표식의 반경(화면 픽셀)입니다.")]
        private float candidateMarkerRadius = 3;
        [SerializeField, Range(4, 12), Tooltip("적 시설 미니맵 표식의 너비(Canvas 픽셀)입니다.")]
        private float buildingMarkerSize = 7;
        [SerializeField, Range(1, 4), Tooltip("분대 이동 점선의 두께(Canvas 픽셀)입니다.")]
        private float moveRouteWidth = 1.5f;
        [SerializeField, Range(3, 10), Tooltip("분대 이동 목적지 화살표의 길이(Canvas 픽셀)입니다.")]
        private float moveRouteArrowSize = 6;
        private WorldPoint basePosition, legionPosition;
        private bool hasBase, hasLegion, rallying, baseAlert;
        private ObservatoryUi markerSkin;
        private float badgeSize;
        private byte selectedMask = 1, visibleSquads;
        private readonly WorldPoint[] squadPositions = new WorldPoint[4];
        private readonly LegionUiFrame[] squadBadges = new LegionUiFrame[4];
        private readonly int[] badgeGroups = new int[4];
        private readonly Rect[] badgeGroupRects = new Rect[4];
        private readonly Vector2[] separators = new Vector2[3];
        private int separatorCount;
        private readonly PlayerColorCache ownerColors = new PlayerColorCache();
        internal event Action<WorldPoint> FocusRequested;
        internal event Action<WorldPoint?, bool, bool> MovePointerChanged;
        private bool leftHeld, rightHeld, pointerBlocked, leftClickCancelled;
        internal bool RightHeld => rightHeld;

        private readonly struct UnitMarker
        {
            internal readonly NetworkUnit Unit;
            internal readonly bool Moving;

            internal UnitMarker(NetworkUnit unit, bool moving) { Unit = unit; Moving = moving; }
        }

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

        internal void ConfigureMarkers(ObservatoryUi skin, float numberSize, float motionThreshold, float motionPeriod)
        {
            if (markerSkin != null) return;
            numberSize = Mathf.Clamp(numberSize, 10, 24);
            badgeSize = numberSize + 6;
            movementThreshold = Mathf.Max(.05f, motionThreshold);
            movementPeriod = Mathf.Max(.5f, motionPeriod);
            for (int index = 0; index < squadBadges.Length; index++)
                squadBadges[index] = skin.SquadBadge(transform, index + 1, numberSize);
            markerSkin = skin;
            RefreshBadgeStyle();
            LayoutBadges();
        }

        /// <summary>선택 마스크의 하위 네 비트를 분대 번호 테두리에 반영한다.</summary>
        public void SetSelectedSquads(byte mask)
        {
            mask &= AllSquadsMask;
            if (selectedMask == mask) return;
            selectedMask = mask;
            RefreshBadgeStyle();
            SetVerticesDirty();
        }

        public void SetBaseAlert(bool active)
        {
            if (baseAlert == active) return;
            baseAlert = active;
            RefreshBadgeStyle();
            SetVerticesDirty();
        }

        internal void SetPointerBlocked(bool blocked)
        {
            pointerBlocked = blocked;
            if (blocked) ResetPointer();
        }

        void IPointerClickHandler.OnPointerClick(PointerEventData pointer)
        {
            if (!pointerBlocked && !leftClickCancelled && pointer.button == PointerEventData.InputButton.Left &&
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
            if (pointerBlocked || !TryGetWorldPoint(pointer.position, pointer.pressEventCamera, out WorldPoint position)) return;
            if (pointer.button == PointerEventData.InputButton.Left)
            {
                leftClickCancelled = false;
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
            if (pointerBlocked || !TryGetWorldPoint(pointer.position, pointer.pressEventCamera, out WorldPoint position))
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
            if (leftHeld) leftClickCancelled = true;
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
            tickSeconds = rules.DeltaTime;
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
            ReconcileUnits(Time.unscaledTimeAsDouble);
            Upload();
        }

        internal void ObserveUnits(SnapshotTimeline latest, int player)
        {
            if (timeline != latest || owner != player) ClearUnits();
            timeline = latest;
            owner = player;
            ReconcileUnits(Time.unscaledTimeAsDouble);
            SetVerticesDirty();
        }

        private void ReconcileUnits(double now)
        {
            if (timeline == null) return;
            long tick = timeline.LatestTick;
            bool newObservation = tick > lastUnitTick;
            float thresholdSquared = movementThreshold * movementThreshold;
            hasMovingUnits = false;
            nextUnits.Clear();
            for (int index = 0; index < timeline.Count; index++)
            {
                if (!timeline.TryGetVisibleLatest(index, out NetworkUnit unit) || unit.OwnerId != owner || !unit.IsSquadTransfer) continue;
                bool observedBefore = units.TryGetValue(unit.Id, out UnitMarker previous);
                // 안개만 다시 열렸을 때 지워진 과거 위치를 되살리지 않는다.
                if (!newObservation && !observedBefore) continue;
                bool moving = false;
                if (observedBefore && previous.Unit.OwnerId == unit.OwnerId &&
                    previous.Unit.Element == unit.Element && previous.Unit.MovementRevision == unit.MovementRevision)
                {
                    moving = newObservation
                        ? WorldPoint.DistanceSquared(unit.Position, previous.Unit.Position) >= thresholdSquared
                        : previous.Moving;
                }
                nextUnits[unit.Id] = new UnitMarker(unit, moving);
                hasMovingUnits |= moving;
            }
            (units, nextUnits) = (nextUnits, units);
            if (newObservation)
            {
                // 오래된 관측을 마지막 위치의 계속된 이동으로 표시하지 않는다.
                movingUntil = now + Math.Clamp((tick - lastUnitTick) * tickSeconds * 3, .6, 1.5);
                lastUnitTick = tick;
            }
            UpdatePulse(now);
        }

        private void Update() => UpdatePulse(Time.unscaledTimeAsDouble);

        private void UpdatePulse(double now)
        {
            bool moving = hasMovingUnits && now < movingUntil;
            bool changed = displayingMotion != moving;
            displayingMotion = moving;
            if (!moving)
            {
                if (changed) SetVerticesDirty();
                return;
            }

            bool reduceMotion = markerSkin?.ReduceMotion == true;
            if (!changed && !reduceMotion && now < nextPulseRefresh) return;
            float tint = reduceMotion ? 1f : .9f + .22f * Mathf.Cos((float)(now % movementPeriod / movementPeriod) * (2 * Mathf.PI));
            if (changed || movementTint != tint)
            {
                movementTint = tint;
                SetVerticesDirty();
            }
            nextPulseRefresh = now + PulseRefreshSeconds;
        }

        private void ClearUnits()
        {
            units.Clear();
            nextUnits.Clear();
            lastUnitTick = -1;
            hasMovingUnits = displayingMotion = false;
            movingUntil = nextPulseRefresh = 0;
        }

        internal void ObservePose(WorldPoint position)
        {
            legionPosition = position;
            hasLegion = true;
            if (rallying) SetVerticesDirty();
        }

        internal void SetBuildingMemory(BuildingMemory memory)
        {
            buildingMemory = memory;
            SetVerticesDirty();
        }

        internal void ObserveGameplay(GameplayFrame frame)
        {
            observation = frame;
            owner = frame.PlayerId;
            ownerColors.Observe(frame);
            hasBase = true;
            basePosition = frame.BasePosition;
            rallying = frame.IsRallying;
            visibleSquads = 0;
            foreach (SquadView squad in frame.Squads)
            {
                if (squad.Id < 1 || squad.Id > squadPositions.Length) continue;
                int index = squad.Id - 1;
                squadPositions[index] = squad.Position;
                visibleSquads |= (byte)(1 << index);
            }
            LayoutBadges();
            SetVerticesDirty();
        }

        internal void ResetMap()
        {
            ResetPointer();
            fog = null;
            timeline = null;
            observation = null;
            ClearUnits();
            ownerColors.Clear();
            hasBase = hasLegion = rallying = baseAlert = false;
            owner = 0;
            visibleSquads = 0;
            RefreshBadgeStyle();
            LayoutBadges();
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

        private Vector2 ToLocalPoint(WorldPoint position, Rect rect) => new Vector2(
            rect.xMin + Mathf.Clamp01(position.X / mapSize) * rect.width,
            rect.yMin + Mathf.Clamp01(position.Y / mapSize) * rect.height);

        private void RefreshBadgeStyle()
        {
            for (int index = 0; index < squadBadges.Length; index++)
            {
                LegionUiFrame badge = squadBadges[index];
                if (badge == null) continue;
                bool selected = (selectedMask & (1 << index)) != 0;
                bool alert = index == squadBadges.Length - 1 && baseAlert;
                ObservatoryUi.StyleSquadBadge(badge, selected, alert);
            }
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            LayoutBadges();
        }

        private void LayoutBadges()
        {
            if (markerSkin == null || mapSize <= 0) return;
            Rect mapRect = rectTransform.rect;
            for (int index = 0; index < squadBadges.Length; index++)
            {
                int bit = 1 << index;
                bool visible = (visibleSquads & bit) != 0;
                squadBadges[index].gameObject.SetActive(visible);
                badgeGroups[index] = visible ? bit : 0;
                if (visible) badgeGroupRects[index] = BadgeGroupRect(mapRect, bit);
            }

            // 화면 가장자리에서 안쪽으로 밀린 번호 묶음도 서로 겹치면 함께 표시한다.
            while (MergeOverlappingGroups(mapRect)) { }
            separatorCount = 0;
            for (int group = 0; group < badgeGroups.Length; group++)
            {
                if (badgeGroups[group] == 0) continue;
                Rect bounds = badgeGroupRects[group];
                float x = bounds.xMin + badgeSize * .5f;
                for (int index = 0; index < squadBadges.Length; index++)
                {
                    if ((badgeGroups[group] & (1 << index)) == 0) continue;
                    Vector2 point = new Vector2(x, bounds.center.y);
                    squadBadges[index].rectTransform.anchoredPosition = point - mapRect.center;
                    x += badgeSize + BadgeGap;
                    if (x < bounds.xMax) separators[separatorCount++] = new Vector2(x - (badgeSize + BadgeGap) * .5f, point.y);
                }
            }
        }

        private bool MergeOverlappingGroups(Rect mapRect)
        {
            for (int first = 0; first < badgeGroups.Length; first++)
            {
                if (badgeGroups[first] == 0) continue;
                for (int second = first + 1; second < badgeGroups.Length; second++)
                {
                    if (badgeGroups[second] == 0 || !badgeGroupRects[first].Overlaps(badgeGroupRects[second])) continue;
                    badgeGroups[first] |= badgeGroups[second];
                    badgeGroups[second] = 0;
                    badgeGroupRects[first] = BadgeGroupRect(mapRect, badgeGroups[first]);
                    return true;
                }
            }
            return false;
        }

        private Rect BadgeGroupRect(Rect mapRect, int group)
        {
            Vector2 total = Vector2.zero;
            int count = 0;
            for (int index = 0; index < squadPositions.Length; index++)
            {
                if ((group & (1 << index)) == 0) continue;
                total += ToLocalPoint(squadPositions[index], mapRect);
                count++;
            }
            Vector2 center = total / count;
            float width = count * badgeSize + (count - 1) * BadgeGap;
            float x = width >= mapRect.width ? mapRect.center.x - width * .5f :
                Mathf.Clamp(center.x - width * .5f, mapRect.xMin, mapRect.xMax - width);
            // 번호 아래에 실제 분대 위치와 유닛 점을 읽을 공간을 남긴다.
            float above = center.y + BadgeGap;
            float preferredY = above + badgeSize <= mapRect.yMax ? above : center.y - badgeSize - BadgeGap;
            float y = badgeSize >= mapRect.height ? mapRect.center.y - badgeSize * .5f :
                Mathf.Clamp(preferredY, mapRect.yMin, mapRect.yMax - badgeSize);
            return new Rect(x, y, width, badgeSize);
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (mapSize <= 0) return;
            Rect rect = rectTransform.rect;

            if (observation != null) DrawMoveRoutes(mesh, rect);
            if (observation != null && buildingMemory != null) DrawBuildingMarkers(mesh, rect);
            Color32 ownerTint = ownerColors.Get(owner);
            if (hasBase)
            {
                Vector2 point = ToLocalPoint(basePosition, rect);
                if (baseAlert) Polygon(mesh, point, 8, 4, ObservatoryUi.Danger);
                Polygon(mesh, point, 4, 4, ownerTint);
            }
            if (observation != null && observation.SpatialReady)
                foreach (ConversionCandidateView candidate in observation.Candidates)
                    if (candidate.EndTick > observation.Tick && CurrentlyVisible(candidate.Position))
                        Polygon(mesh, ToLocalPoint(candidate.Position, rect), Mathf.Clamp(candidateMarkerRadius, 1, 6), 4, ObservatoryUi.Gold);

            if (rallying && hasLegion && hasBase)
                DrawDashedLine(mesh, ToLocalPoint(basePosition, rect), ToLocalPoint(legionPosition, rect), ownerTint, .7f, 4);
            foreach (UnitMarker marker in units.Values)
            {
                NetworkUnit unit = marker.Unit;
                Vector2 position = ToLocalPoint(unit.Position, rect);
                Color32 tint = ownerTint;
                if (displayingMotion && marker.Moving)
                {
                    Polygon(mesh, position, 3.2f, 6, new Color32(
                        (byte)Mathf.Min(255, tint.r * movementTint), (byte)Mathf.Min(255, tint.g * movementTint),
                        (byte)Mathf.Min(255, tint.b * movementTint), tint.a));
                }
                else Polygon(mesh, position, unit.Garrison ? 2.5f : 1.5f, 8, tint);
            }
            for (int index = 0; index < separatorCount; index++)
                Polygon(mesh, separators[index], 1.25f, 4, ObservatoryUi.TextColor);
        }

        private void DrawBuildingMarkers(VertexHelper mesh, Rect rect)
        {
            float halfSize = Mathf.Clamp(buildingMarkerSize, 4, 12) * .5f;
            foreach (RememberedBuilding record in buildingMemory.Records)
            {
                Vector2 center = ToLocalPoint(record.Building.Position, rect);
                bool visible = buildingMemory.IsVisible(record.Building.Id);
                Color32 tint = visible ? record.Color : (Color32)Color.Lerp(record.Color, ObservatoryUi.Muted, .4f);
                if (!visible) tint.a = (byte)(tint.a * 3 / 4);

                int start = mesh.currentVertCount;
                foreach (Vector2 corner in BuildingMarkerCorners)
                    mesh.AddVert(center + corner * halfSize, tint, Vector2.zero);
                if (visible)
                {
                    mesh.AddTriangle(start, start + 1, start + 2);
                    mesh.AddTriangle(start, start + 2, start + 3);
                    continue;
                }

                foreach (Vector2 corner in BuildingMarkerCorners)
                    mesh.AddVert(center + corner * (halfSize - 1), tint, Vector2.zero);
                for (int side = 0; side < BuildingMarkerCorners.Length; side++)
                {
                    int outer = start + side, next = start + (side + 1) % BuildingMarkerCorners.Length;
                    mesh.AddTriangle(outer, next, next + 4);
                    mesh.AddTriangle(outer, next + 4, outer + 4);
                }
            }
        }

        private void DrawMoveRoutes(VertexHelper mesh, Rect rect)
        {
            Color32 secondaryTint = ObservatoryUi.Muted;
            secondaryTint.a = 150;
            foreach (SquadView squad in observation.Squads)
            {
                if (squad.Order != SquadOrderKind.MoveTo) continue;
                Vector2 origin = ToLocalPoint(squad.Position, rect);
                Vector2 target = ToLocalPoint(squad.Destination, rect);
                Vector2 direction = (target - origin).normalized;
                if (direction == Vector2.zero) continue;
                Color32 tint = (selectedMask & (1 << (squad.Id - 1))) != 0 ? (Color32)ObservatoryUi.Accent : secondaryTint;
                float arrowSize = Mathf.Clamp(moveRouteArrowSize, 3, 10);
                DrawDashedLine(mesh, origin, target, tint, Mathf.Clamp(moveRouteWidth, 1, 4) * .5f, arrowSize);
                Vector2 arrowBase = target - direction * arrowSize;
                Vector2 arrowWing = new Vector2(-direction.y, direction.x) * (arrowSize * .45f);
                int first = mesh.currentVertCount;
                mesh.AddVert(target, tint, Vector2.zero);
                mesh.AddVert(arrowBase + arrowWing, tint, Vector2.zero);
                mesh.AddVert(arrowBase - arrowWing, tint, Vector2.zero);
                mesh.AddTriangle(first, first + 1, first + 2);
            }
        }

        private static void DrawDashedLine(VertexHelper mesh, Vector2 origin, Vector2 target, Color32 tint, float halfWidth, float endPadding)
        {
            float distance = Vector2.Distance(origin, target);
            if (distance < 5) return;
            Vector2 direction = (target - origin) / distance;
            Vector2 normal = new Vector2(-direction.y, direction.x) * halfWidth;
            float end = distance - endPadding;
            for (float offset = 0; offset < end; offset += 7)
            {
                Vector2 start = origin + direction * offset;
                Vector2 finish = origin + direction * Mathf.Min(offset + 4, end);
                int first = mesh.currentVertCount;
                mesh.AddVert(start + normal, tint, Vector2.zero);
                mesh.AddVert(start - normal, tint, Vector2.zero);
                mesh.AddVert(finish - normal, tint, Vector2.zero);
                mesh.AddVert(finish + normal, tint, Vector2.zero);
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
