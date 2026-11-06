using System;

namespace ElementalLegion.Shared.Net
{
    /// <summary>인터넷 프레임에 호스트 세대를 붙여 이전 서버의 지연 메시지를 구분한다.</summary>
    public static class OnlineFrame
    {
        public const int HeaderBytes = 8;
        public const int MaxMessageBytes = Protocol.MaxDatagramBytes + HeaderBytes;

        public static byte[] Wrap(ulong generation, byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload.Length > Protocol.MaxDatagramBytes)
                throw new ArgumentException("인터넷 게임 본문의 길이가 잘못됐습니다.", nameof(payload));
            var frame = new byte[payload.Length + HeaderBytes];
            for (int index = 0; index < HeaderBytes; index++) frame[index] = (byte)(generation >> (index * 8));
            Buffer.BlockCopy(payload, 0, frame, HeaderBytes, payload.Length);
            return frame;
        }

        public static bool TryRead(byte[] bytes, int count, out ulong generation, out byte[] payload)
        {
            generation = 0;
            payload = null;
            if (bytes == null || count <= HeaderBytes || count > MaxMessageBytes || count > bytes.Length) return false;
            for (int index = 0; index < HeaderBytes; index++) generation |= (ulong)bytes[index] << (index * 8);
            payload = new byte[count - HeaderBytes];
            Buffer.BlockCopy(bytes, HeaderBytes, payload, 0, payload.Length);
            return true;
        }
    }
}
