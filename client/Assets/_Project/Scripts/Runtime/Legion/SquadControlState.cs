using System;
using System.Numerics;

namespace ElementalLegion.Client.Legion
{
    /// <summary>분대 선택과 직접 이동·우클릭 목적지의 입력 상태를 관리한다. 서버 판정은 변경하지 않는다.</summary>
    public sealed class SquadControlState
    {
        public byte SelectedMask { get; private set; } = 1;
        public byte Representative => SelectedMask == 7 ? (byte)1 : SelectedMask == 8 ? (byte)4 : SelectedMask == 4 ? (byte)3 : SelectedMask == 2 ? (byte)2 : (byte)1;
        internal bool PointHeld => pointActive;
        private byte directMask;
        private bool pointActive, pointBlocked, pointPending, pointPressed;
        private Vector2 pointDestination;

        /// <summary>프레임마다 최신 목적지만 보관한다. 취소한 홀드는 버튼을 놓을 때까지 다시 시작하지 않는다.</summary>
        public void ObservePoint(bool held, bool pressed, bool allowed, bool direct, Vector2? destination)
        {
            if (!held && !pressed)
            {
                pointActive = pointBlocked = false;
                if (!allowed || direct) DiscardPendingPoint();
                return;
            }
            if (!allowed || direct) { CancelPoint(); return; }
            if (pointBlocked) return;
            if (pressed) pointActive = true;
            if (!pointActive) return;
            if (!destination.HasValue) { CancelPoint(); return; }
            pointDestination = destination.Value;
            pointPending = true;
            pointPressed |= pressed;
            if (!held) pointActive = false;
        }

        /// <summary>명령 전송 틱에서 최신 목적지를 한 번 소비한다. 짧은 클릭도 다음 틱까지 유지한다.</summary>
        public bool TryTakePoint(out byte mask, out Vector2 destination, out bool pressed)
        {
            mask = SelectedMask;
            destination = pointDestination;
            pressed = pointPressed;
            if (!pointPending) return false;
            DiscardPendingPoint();
            ClearDirect();
            return true;
        }

        public void CancelPoint()
        {
            pointActive = false;
            DiscardPendingPoint();
            pointBlocked = true;
        }

        internal void DiscardPendingPoint() => pointPending = pointPressed = false;

        public byte Select(byte mask)
        {
            if (mask != 1 && mask != 2 && mask != 4 && mask != 8 && mask != 7)
                throw new ArgumentOutOfRangeException(nameof(mask));
            CancelPoint();
            if (mask == SelectedMask) return 0;
            byte stop = directMask;
            directMask = 0;
            SelectedMask = mask;
            return stop;
        }

        public bool NextDirect(bool held, out byte mask)
        {
            mask = held ? SelectedMask : directMask;
            directMask = held ? SelectedMask : (byte)0;
            return mask != 0;
        }

        public void ClearDirect() => directMask = 0;
        public static bool ShouldMoveTo(bool allowed, bool pointerOverUi, bool placementCancelled) =>
            allowed && !pointerOverUi && !placementCancelled;
    }
}
