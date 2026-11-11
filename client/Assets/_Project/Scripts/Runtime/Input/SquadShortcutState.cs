namespace ElementalLegion.Client.Input
{
    /// <summary>분대 선택 마스크 또는 빠른 전송의 출발·목적 분대 ID를 전달한다.</summary>
    public readonly struct SquadShortcutResult
    {
        public byte SelectionMask { get; }
        public byte FromSquad { get; }
        public byte TargetSquad { get; }

        internal SquadShortcutResult(byte selectionMask, byte fromSquad, byte targetSquad)
        {
            SelectionMask = selectionMask;
            FromSquad = fromSquad;
            TargetSquad = targetSquad;
        }
    }

    /// <summary>설정된 분대 키의 누름·해제를 모아 선택 또는 빠른 전송으로 해석한다.</summary>
    public sealed class SquadShortcutState
    {
        private const byte AllFieldKey = 1 << 4, AllFieldSelection = 7, SlotMask = 31;
        private byte sourceKey, previousHeld;
        private bool sent, waitForRelease;

        public byte PendingFromSquad => sourceKey == AllFieldKey ? (byte)0 : SquadId(sourceKey);

        /// <summary>포커스나 입력 문맥이 바뀌면 모든 분대 키가 비는 프레임부터 다시 받는다.</summary>
        public void Reset() => Clear(true);

        public SquadShortcutResult Update(byte heldMask, byte pressedMask, byte releasedMask, bool enabled)
        {
            if (!enabled)
            {
                Reset();
                return default;
            }
            byte held = (byte)(heldMask & SlotMask);
            byte down = (byte)(pressedMask & SlotMask & (~previousHeld | releasedMask));
            byte active = (byte)(held | down);
            if (waitForRelease)
            {
                waitForRelease = held != 0;
                return default;
            }
            previousHeld = held;
            if (sourceKey == 0)
            {
                if (active == 0) return default;
                if (down == 0 || Multiple(active))
                {
                    Clear(held != 0);
                    return default;
                }
                sourceKey = down;
            }

            byte other = (byte)(active & ~sourceKey);
            if ((held & sourceKey) == 0 || (releasedMask & sourceKey) != 0)
            {
                // 출발 키 해제와 다른 키 누름이 겹치면 어느 키가 먼저였는지 알 수 없다.
                bool overlapping = other != 0 || (held & sourceKey) != 0;
                byte selection = !overlapping && !sent && (releasedMask & sourceKey) != 0
                    ? sourceKey == AllFieldKey ? AllFieldSelection : sourceKey
                    : (byte)0;
                Clear(overlapping && held != 0);
                return new SquadShortcutResult(selection, 0, 0);
            }
            if (other == 0) return default;
            if (Multiple(other) || sourceKey == AllFieldKey || (other & AllFieldKey) != 0)
            {
                Reset();
                return default;
            }
            if ((down & other) == 0) return default;
            sent = true;
            return new SquadShortcutResult(0, SquadId(sourceKey), SquadId(other));
        }

        private void Clear(bool block)
        {
            sourceKey = previousHeld = 0;
            sent = false;
            waitForRelease = block;
        }

        private static bool Multiple(byte keys) => (keys & (keys - 1)) != 0;
        private static byte SquadId(byte key) => key switch { 1 => 1, 2 => 2, 4 => 3, 8 => 4, _ => 0 };
    }
}
