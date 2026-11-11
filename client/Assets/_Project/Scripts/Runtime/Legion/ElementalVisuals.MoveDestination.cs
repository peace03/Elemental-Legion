using ElementalLegion.Shared.Net;
using UnityEngine;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Legion
{
    public sealed partial class ElementalVisuals
    {
        [Header("이동 목적지 표시")]
        [SerializeField, Min(.1f), Tooltip("우클릭한 목적지 고리의 기본 반경(m)입니다.")]
        private float moveMarkerRadius = 2.2f;
        [SerializeField, Range(.1f, 2f), Tooltip("목적지 표시가 수축하며 사라지는 시간(초)입니다.")]
        private float moveMarkerDuration = .55f;
        [SerializeField, Tooltip("목적지 고리와 방향 표식의 색입니다.")]
        private Color moveMarkerColor = new Color(.22f, .95f, .72f, 1);

        private WorldPoint moveMarkerPosition;
        private float moveMarkerStart, moveMarkerUntil;
        private bool moveMarkerKnown;
        private MaterialPropertyBlock moveMarkerProperties;

        internal void ShowMoveDestination(WorldPoint destination, bool pressed)
        {
            if (!Protocol.Finite(destination) || !pressed && !moveMarkerKnown) return;
            float now = Now;
            bool changed = WorldPoint.DistanceSquared(destination, moveMarkerPosition) > .0001f;
            moveMarkerPosition = destination;
            if (pressed || changed && now >= moveMarkerUntil)
            {
                moveMarkerKnown = true;
                moveMarkerStart = now;
                moveMarkerUntil = now + Mathf.Max(.1f, moveMarkerDuration);
            }
        }

        private void DrawMoveDestination()
        {
            float now = Now;
            if (!moveMarkerKnown || now >= moveMarkerUntil) return;
            if (moveMarkerProperties == null)
            {
                moveMarkerProperties = Spectral(1);
                moveMarkerProperties.SetFloat("_OwnershipTint", 1);
                moveMarkerProperties.SetFloat("_Flow", 0);
                moveMarkerProperties.SetFloat("_DeformationStrength", 0);
            }
            float progress = Mathf.Clamp01((now - moveMarkerStart) / Mathf.Max(.1f, moveMarkerUntil - moveMarkerStart));
            float eased = 1 - (1 - progress) * (1 - progress);
            float radius = Mathf.Max(.1f, moveMarkerRadius) * Mathf.Lerp(1.25f, .8f, eased);
            float opacity = Mathf.Clamp01(moveMarkerColor.a) * (1 - progress);
            moveMarkerProperties.SetColor(BaseColor, moveMarkerColor);
            moveMarkerProperties.SetColor(SecondColor, moveMarkerColor);
            moveMarkerProperties.SetFloat(Opacity, opacity);

            var center = new Vector3(moveMarkerPosition.X, .12f, moveMarkerPosition.Y);
            DrawOne(ring, profiles[lightIndex].Body, Matrix4x4.TRS(center, Quaternion.identity, Vector3.one * radius), moveMarkerProperties);
            for (int index = 0; index < 4; index++)
            {
                Quaternion rotation = Quaternion.Euler(0, index * 90, 0);
                Vector3 point = center + rotation * Vector3.forward * (radius * .7f);
                var scale = new Vector3(radius * .13f, .06f, radius * .24f);
                DrawOne(crystal, profiles[lightIndex].Body, Matrix4x4.TRS(point, rotation, scale), moveMarkerProperties);
            }
        }

        private void ClearMoveDestination()
        {
            moveMarkerKnown = false;
            moveMarkerStart = moveMarkerUntil = 0;
            moveMarkerPosition = default;
        }
    }
}
