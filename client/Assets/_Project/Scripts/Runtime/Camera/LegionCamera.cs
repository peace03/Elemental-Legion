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
        private Vector3 target;
        private bool initialized;

        public void Follow(System.Numerics.Vector2 position)
        {
            target = new Vector3(position.X, 0, position.Y);
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
            transform.position = Vector3.Lerp(transform.position, target + offset, 1 - Mathf.Exp(-Time.unscaledDeltaTime / followSeconds));
            transform.rotation = Quaternion.LookRotation(-offset, Vector3.up);
        }

        private void OnValidate() { height = Mathf.Max(1, height); backward = Mathf.Max(0, backward); viewSize = Mathf.Max(1, viewSize); followSeconds = Mathf.Max(0.01f, followSeconds); }
    }
}
