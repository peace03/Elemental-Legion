using ElementalLegion.Shared.Data;
using UnityEngine;

namespace ElementalLegion.Client.Camera
{
    /// <summary>서버 판정과 독립적으로 내 군단 중심을 탑다운 시점에서 따라간다.</summary>
    public sealed class LegionCamera : MonoBehaviour
    {
        [SerializeField, Tooltip("따라갈 실제 카메라 참조입니다.")] private UnityEngine.Camera view;
        [SerializeField, Min(1), Tooltip("군단 위 카메라 높이(m)입니다.")] private float height = 48;
        [SerializeField, Min(0), Tooltip("군단 뒤쪽 카메라 거리(m)입니다.")] private float backward = 25;
        [SerializeField, Min(1), Tooltip("직교 카메라의 수직 반높이(m)입니다.")] private float viewSize = 30;
        [SerializeField, Min(0.01f), Tooltip("카메라가 중심을 따라가는 시간(초)입니다.")] private float followSeconds = 0.12f;
        private Vector3 target, confirmed;
        private Vector2 visionHalfSize;
        private float visionPadding;
        private bool initialized;
        internal UnityEngine.Camera View => view;

        internal void ConfigureVision(GameRules rules)
        {
            visionHalfSize = new Vector2(rules.CameraVisionHalfSize.X, rules.CameraVisionHalfSize.Y);
            visionPadding = rules.CameraVisionPadding;
        }

        public void Follow(System.Numerics.Vector2 position, System.Numerics.Vector2 confirmedPosition)
        {
            target = new Vector3(position.X, 0, position.Y);
            confirmed = new Vector3(confirmedPosition.X, 0, confirmedPosition.Y);
            if (!initialized) { initialized = true; transform.position = target + new Vector3(0, height, -backward); }
        }

        public void ResetFollow() => initialized = false;

        private void Awake()
        {
            if (view == null) { Debug.LogError("LegionCamera.view 참조가 없습니다.", this); enabled = false; return; }
            view.orthographic = true; view.orthographicSize = viewSize;
        }

        private void LateUpdate()
        {
            if (!initialized) return;
            Vector3 offset = new Vector3(0, height, -backward);
            Vector3 center = Vector3.Lerp(transform.position - offset, target, 1 - Mathf.Exp(-Time.unscaledDeltaTime / followSeconds));
            // 사선으로 내려다보는 화면의 세로 길이는 지면에 더 길게 투영된다.
            float depthLimit = (visionHalfSize.y - visionPadding) * height / offset.magnitude;
            float widthLimit = (visionHalfSize.x - visionPadding) / view.aspect;
            float size = Mathf.Min(viewSize, Mathf.Min(depthLimit, widthLimit));
            if (view.orthographicSize != size) view.orthographicSize = size;
            // 추종 보간과 예측이 서버가 확정한 중심에서 화면 여유 폭을 넘지 않게 한다.
            float followLimit = visionPadding * 0.5f;
            center.x = Mathf.Clamp(center.x, confirmed.x - followLimit, confirmed.x + followLimit);
            center.z = Mathf.Clamp(center.z, confirmed.z - followLimit, confirmed.z + followLimit);
            transform.position = center + offset;
            transform.rotation = Quaternion.LookRotation(-offset, Vector3.up);
        }

        private void OnValidate() { height = Mathf.Max(1, height); backward = Mathf.Max(0, backward); viewSize = Mathf.Max(1, viewSize); followSeconds = Mathf.Max(0.01f, followSeconds); }
    }
}
