using System;

namespace ElementalLegion.Client.Legion
{
    /// <summary>선택과 직전 직접 이동 대상만 기억한다. 서버의 분대 명령은 변경하지 않는다.</summary>
    public sealed class SquadControlState
    {
        public byte SelectedMask { get; private set; } = 1;
        public byte Representative => SelectedMask == 7 ? (byte)1 : SelectedMask == 8 ? (byte)4 : SelectedMask == 4 ? (byte)3 : SelectedMask == 2 ? (byte)2 : (byte)1;
        private byte directMask;

        public byte Select(byte mask)
        {
            if (mask != 1 && mask != 2 && mask != 4 && mask != 8 && mask != 7)
                throw new ArgumentOutOfRangeException(nameof(mask));
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
