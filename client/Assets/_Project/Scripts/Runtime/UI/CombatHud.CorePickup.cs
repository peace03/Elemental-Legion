using System;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;

namespace ElementalLegion.Client.UI
{
    public sealed partial class CombatHud
    {
        [Header("핵 획득 표시")]
        [SerializeField, Range(.05f, .5f), Tooltip("이 시간(초) 안에 이어진 획득량은 하나의 +N으로 합칩니다.")]
        private float corePickupGroupSeconds = .35f;
        [SerializeField, Range(.1f, .45f), Tooltip("핵 아이콘 반동과 청록빛이 유지되는 시간(초)입니다.")]
        private float corePickupPulseSeconds = .32f;
        [SerializeField, Range(.5f, 2), Tooltip("마지막 획득 뒤 +N을 표시할 시간(초)입니다.")]
        private float corePickupLabelSeconds = 1;
        [SerializeField, Range(1.1f, 1.35f), Tooltip("핵 아이콘과 청록빛 테두리의 최대 확대 배율입니다.")]
        private float corePickupPeakScale = 1.24f;
        [SerializeField, Range(0, 16), Tooltip("+N이 떠오르는 거리(Canvas 픽셀)입니다.")]
        private float corePickupRise = 10;

        private static readonly Vector2 CorePickupLabelOrigin = new Vector2(44, -49);
        private RectTransform corePickupIcon;
        private LegionUiFrame corePickupGlow;
        private TMP_Text corePickupLabel;
        private ulong lastCorePickupSerial;
        private long corePickupAmount;
        private double corePickupStartedAt, corePickupLastAt;

        private void BuildCorePickupFeedback(ObservatoryUi ui)
        {
            var center = new Vector2(24, -22);
            Color glow = ObservatoryUi.Accent;
            glow.a = .12f;
            corePickupGlow = ObservatoryUi.Panel(hudHeader, "Core pickup glow", 0, 0, 34, 34, glow).GetComponent<LegionUiFrame>();
            corePickupGlow.Style(10, 1.25f, ObservatoryUi.Accent);
            corePickupGlow.rectTransform.pivot = Vector2.one * .5f;
            corePickupGlow.rectTransform.anchoredPosition = center;
            corePickupIcon = ui.Icon(hudHeader, "Core emblem", ui.Art.Building(BuildingKind.Base), 0, 0, 30, 30).rectTransform;
            corePickupIcon.pivot = Vector2.one * .5f;
            corePickupIcon.anchoredPosition = center;
            corePickupLabel = ui.Label(hudHeader, "Core pickup amount", string.Empty, 18, CorePickupLabelOrigin.x,
                -CorePickupLabelOrigin.y, 246, 34, ObservatoryUi.Accent);
            corePickupLabel.fontStyle = FontStyles.Bold;
            HideCorePickupFeedback();
        }

        private void ObserveCorePickups(GameplayFrame frame, double now)
        {
            if (corePickupLabel == null || frame.PlayerId <= 0) return;
            long received = 0;
            foreach (NetworkEvent entry in frame.Events)
            {
                SimEvent data = entry.Data;
                if (entry.Serial <= lastCorePickupSerial || data.OwnerId != frame.PlayerId ||
                    data.Kind != SimEventKind.CorePickedUp && data.Kind != SimEventKind.CoresProduced)
                    continue;
                lastCorePickupSerial = entry.Serial;
                if (data.Amount >= 1 && data.Amount <= int.MaxValue)
                    received += Math.Min((long)data.Amount, int.MaxValue);
            }
            if (received == 0) return;
            if (corePickupAmount == 0 || now - corePickupLastAt > corePickupGroupSeconds)
            {
                corePickupStartedAt = now;
                corePickupAmount = 0;
            }
            corePickupLastAt = now;
            corePickupAmount += Math.Min(long.MaxValue - corePickupAmount, received);
            corePickupLabel.text = $"+{corePickupAmount:N0}";
            corePickupLabel.gameObject.SetActive(true);
            TickCorePickupFeedback(now);
        }

        internal void TickCorePickupFeedback(double now)
        {
            if (corePickupAmount == 0) return;
            float labelSeconds = Mathf.Max(.5f, corePickupLabelSeconds);
            float labelAge = Mathf.Clamp01((float)((now - corePickupLastAt) / labelSeconds));
            if (labelAge >= 1)
            {
                HideCorePickupFeedback();
                return;
            }

            bool reduceMotion = skin.ReduceMotion;
            float sinceStart = (float)(now - corePickupStartedAt);
            float pulse = Mathf.Clamp01(sinceStart / Mathf.Max(.1f, corePickupPulseSeconds));
            if (corePickupGlow.enabled || pulse < 1)
            {
                corePickupGlow.enabled = pulse < 1;
                float bounce = reduceMotion || pulse >= 1 ? 0 : Mathf.Sin(Mathf.PI * Mathf.Sqrt(pulse));
                corePickupIcon.localScale = Vector3.one * Mathf.Lerp(1, corePickupPeakScale, bounce);
                corePickupGlow.rectTransform.localScale = Vector3.one * (reduceMotion ? 1 : Mathf.Lerp(1, corePickupPeakScale, pulse));
                corePickupGlow.canvasRenderer.SetAlpha(reduceMotion ? .8f : 1 - pulse);
            }

            float rise = reduceMotion ? 0 : Mathf.SmoothStep(0, corePickupRise, sinceStart / labelSeconds);
            corePickupLabel.rectTransform.anchoredPosition = CorePickupLabelOrigin + Vector2.up * rise;
            float fade = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.55f, 1, labelAge));
            corePickupLabel.canvasRenderer.SetAlpha(reduceMotion ? 1 : 1 - fade);
        }

        internal void ResetCorePickupFeedback()
        {
            lastCorePickupSerial = 0;
            HideCorePickupFeedback();
        }

        private void HideCorePickupFeedback()
        {
            corePickupAmount = 0;
            if (corePickupLabel == null) return;
            corePickupLabel.gameObject.SetActive(false);
            corePickupGlow.enabled = false;
            corePickupIcon.localScale = Vector3.one;
        }
    }
}
