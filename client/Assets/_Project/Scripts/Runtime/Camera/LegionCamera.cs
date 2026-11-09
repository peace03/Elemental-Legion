using ElementalLegion.Shared.Data;
using UnityEngine;

namespace ElementalLegion.Client.Camera
{
    /// <summary>군단을 따라가거나 미니맵에서 고른 전장 위치를 비춘다.</summary>
    public sealed class LegionCamera : MonoBehaviour
    {
        [SerializeField, Tooltip("따라갈 실제 카메라 참조입니다.")] private UnityEngine.Camera view;
        [SerializeField, Min(1), Tooltip("군단 위 카메라 높이(m)입니다.")] private float height = 48;
        [SerializeField, Min(0), Tooltip("군단 뒤쪽 카메라 거리(m)입니다.")] private float backward = 25;
        [SerializeField, Min(1), Tooltip("축소 시 최대 직교 반높이(m)입니다. 서버 시야에 맞춰 더 낮아질 수 있습니다.")] private float viewSize = 30;
        [SerializeField, Min(1), Tooltip("확대 시 최소 직교 반높이(m)입니다.")] private float minimumViewSize = 10;
        [SerializeField, Range(.05f, .5f), Tooltip("마우스 휠 한 단계의 배율 변화입니다.")] private float zoomStep = .2f;
        [SerializeField, Min(0.01f), Tooltip("카메라가 중심을 따라가는 시간(초)입니다.")] private float followSeconds = 0.12f;
        private Vector3 target, confirmed;
        private Vector3? inspectionCenter;
        private Vector2 visionHalfSize;
        private float visionPadding, mapSize;
        private float requestedViewSize;
        private bool initialized;
        internal UnityEngine.Camera View => view;

        internal void ConfigureVision(GameRules rules)
        {
            visionHalfSize = new Vector2(rules.CameraVisionHalfSize.X, rules.CameraVisionHalfSize.Y);
            visionPadding = rules.CameraVisionPadding;
            mapSize = rules.MapSize;
        }

        public void Follow(System.Numerics.Vector2 position, System.Numerics.Vector2 confirmedPosition)
        {
            target = new Vector3(position.X, 0, position.Y);
            confirmed = new Vector3(confirmedPosition.X, 0, confirmedPosition.Y);
            if (!initialized) { initialized = true; Place(target); }
        }

        internal void SnapFollow(System.Numerics.Vector2 position, System.Numerics.Vector2 confirmedPosition)
        {
            inspectionCenter = null;
            Follow(position, confirmedPosition);
            Place(ClampToVision(target));
        }

        public void ResetFollow()
        {
            initialized = false;
            requestedViewSize = 0;
            inspectionCenter = null;
        }

        internal void Inspect(System.Numerics.Vector2 position)
        {
            if (!initialized || !float.IsFinite(position.X) || !float.IsFinite(position.Y)) return;
            var center = new Vector3(Mathf.Clamp(position.X, 0, mapSize), 0, Mathf.Clamp(position.Y, 0, mapSize));
            inspectionCenter = center;
            Place(center);
        }

        internal void CenterOnLegion()
        {
            if (!inspectionCenter.HasValue) return;
            inspectionCenter = null;
            Place(ClampToVision(target));
        }

        internal void ZoomByWheel(float scroll)
        {
            if (!initialized || scroll == 0 || !float.IsFinite(scroll)) return;
            float maximum = MaximumViewSize();
            float minimum = Mathf.Min(minimumViewSize, maximum);
            float current = requestedViewSize > 0 ? Mathf.Clamp(requestedViewSize, minimum, maximum) : maximum;
            float step = 1 + zoomStep;
            float next = Mathf.Clamp(scroll > 0 ? current / step : current * step, minimum, maximum);
            requestedViewSize = next < maximum ? next : 0;
            view.orthographicSize = next;
        }

        private void Awake()
        {
            if (view == null) { Debug.LogError("LegionCamera.view 참조가 없습니다.", this); enabled = false; return; }
            view.orthographic = true; view.orthographicSize = viewSize;
        }

        private void LateUpdate()
        {
            if (!initialized) return;
            float maximum = MaximumViewSize();
            float size = requestedViewSize > 0 ? Mathf.Clamp(requestedViewSize, Mathf.Min(minimumViewSize, maximum), maximum) : maximum;
            if (view.orthographicSize != size) view.orthographicSize = size;
            Vector3 offset = new Vector3(0, height, -backward);
            Vector3 center = inspectionCenter ?? ClampToVision(
                Vector3.Lerp(transform.position - offset, target, 1 - Mathf.Exp(-Time.unscaledDeltaTime / followSeconds)));
            Place(center);
        }

        private Vector3 ClampToVision(Vector3 center)
        {
            // 군단 추종 중의 화면 여유 폭은 서버가 확정한 중심을 기준으로 유지한다.
            float followLimit = visionPadding * 0.5f;
            center.x = Mathf.Clamp(center.x, confirmed.x - followLimit, confirmed.x + followLimit);
            center.z = Mathf.Clamp(center.z, confirmed.z - followLimit, confirmed.z + followLimit);
            return center;
        }

        private void Place(Vector3 center)
        {
            var offset = new Vector3(0, height, -backward);
            transform.SetPositionAndRotation(center + offset, Quaternion.LookRotation(-offset, Vector3.up));
        }

        private float MaximumViewSize()
        {
            // 사선 카메라의 세로 길이는 지면에 더 길게 투영된다.
            float depthLimit = (visionHalfSize.y - visionPadding) * height / Mathf.Sqrt(height * height + backward * backward);
            float widthLimit = (visionHalfSize.x - visionPadding) / view.aspect;
            return Mathf.Max(.1f, Mathf.Min(viewSize, Mathf.Min(depthLimit, widthLimit)));
        }

        private void OnValidate()
        {
            height = Mathf.Max(1, height);
            backward = Mathf.Max(0, backward);
            viewSize = Mathf.Max(1, viewSize);
            minimumViewSize = Mathf.Clamp(minimumViewSize, 1, viewSize);
            zoomStep = Mathf.Clamp(zoomStep, .05f, .5f);
            followSeconds = Mathf.Max(.01f, followSeconds);
        }
    }
}
