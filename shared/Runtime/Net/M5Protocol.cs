using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    public enum LobbyError : byte { NotHost = 1, NotReady, SpawnFailed, InvalidState, InvalidPacket, RoomFull, ColorTaken }

    /// <summary>대기실에 공개하는 시작 원소, 준비 상태와 소유 색이다.</summary>
    public readonly struct LobbyMember
    {
        public string Nickname { get; }
        public int Element { get; }
        public bool Ready { get; }
        public int ColorId { get; }
        public LobbyMember(string nickname, int element, bool ready, int colorId = -1)
        { Nickname = nickname; Element = element; Ready = ready; ColorId = colorId; }
    }

    public readonly struct LobbyState
    {
        public string Title { get; }
        public int SelfIndex { get; }
        public int MinimumPlayers { get; }
        public int Capacity { get; }
        public int AiCount => AiDifficulties?.Length ?? 0;
        public AiDifficulty[] AiDifficulties { get; }
        public int[] AiColorIds { get; }
        public LobbyMember[] Members { get; }
        internal LobbyState(string title, int self, int minimum, int capacity, LobbyMember[] members, AiDifficulty[] aiDifficulties, int[] aiColorIds)
        { Title = title; SelfIndex = self; MinimumPlayers = minimum; Capacity = capacity; Members = members; AiDifficulties = aiDifficulties; AiColorIds = aiColorIds; }
    }

    public readonly struct LanRoomListing
    {
        public int Port { get; }
        public string Title { get; }
        public int Count { get; }
        public int MinimumPlayers { get; }
        public int Capacity { get; }
        public bool Running { get; }
        public LanRoomListing(int port, string title, int count, int minimumPlayers, int capacity, bool running)
        { Port = port; Title = title; Count = count; MinimumPlayers = minimumPlayers; Capacity = capacity; Running = running; }
    }

    public readonly struct ChatRequest
    {
        public bool Global { get; }
        public string Text { get; }
        internal ChatRequest(bool global, string text) { Global = global; Text = text; }
    }

    /// <summary>시야에 있는 유닛 ID만 말풍선에 사용한다. 좌표는 포함하지 않는다.</summary>
    public readonly struct ChatMessage
    {
        public ulong Id { get; }
        public int OwnerId { get; }
        public string Nickname { get; }
        public bool Global { get; }
        public int AnchorId { get; }
        public string Text { get; }
        public ChatMessage(ulong id, int ownerId, string nickname, bool global, int anchorId, string text)
        { Id = id; OwnerId = ownerId; Nickname = nickname; Global = global; AnchorId = anchorId; Text = text; }
    }

    /// <summary>대기실, LAN 검색과 채팅의 크기와 입력 범위를 검증한다.</summary>
    public static class M5Protocol
    {
        public const int TitleLimit = 32, ReportLimit = 100, MaxChatLength = 200;
        public const byte ChatChannel = 3;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static bool IsSafeText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return false;
            for (int index = 0; index < value.Length; index++)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(value, index);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator ||
                    category == UnicodeCategory.Surrogate) return false;
                if (char.IsHighSurrogate(value[index])) index++;
            }
            return true;
        }

        public static void WriteChat(NetDataWriter writer, bool global, string text)
        {
            if (!IsSafeText(text, MaxChatLength)) throw new ArgumentException("채팅 문장이 잘못됐습니다.", nameof(text));
            writer.Reset(); writer.Put((byte)PacketKind.ChatSend); writer.Put((byte)(global ? 1 : 0)); writer.Put(text.Trim());
        }

        public static bool TryReadChat(NetDataReader reader, NetworkSettings settings, out ChatRequest request)
        {
            request = default;
            if (!HasKind(reader, PacketKind.ChatSend) || reader.AvailableBytes < 1) return false;
            byte global = reader.GetByte();
            if (global > 1 || !ReadText(reader, settings.ChatMaxLength, out string text) || reader.AvailableBytes != 0) return false;
            request = new ChatRequest(global == 1, text.Trim());
            return true;
        }

        public static void WriteChatMessage(NetDataWriter writer, ChatMessage message)
        {
            writer.Reset(); writer.Put((byte)PacketKind.ChatMessage); writer.Put(message.Id); writer.Put(message.OwnerId);
            writer.Put((byte)(message.Global ? 1 : 0)); writer.Put(message.AnchorId); writer.Put(message.Nickname); writer.Put(message.Text);
        }

        public static bool TryReadChatMessage(NetDataReader reader, NetworkSettings settings, out ChatMessage message)
        {
            message = default;
            if (!HasKind(reader, PacketKind.ChatMessage) || reader.AvailableBytes < 17) return false;
            ulong id = reader.GetULong();
            int owner = reader.GetInt();
            byte global = reader.GetByte();
            int anchor = reader.GetInt();
            if (id == 0 || owner <= 0 || global > 1 || anchor < 0 || global == 1 && anchor != 0 ||
                !Protocol.TryReadNickname(reader, out string name) || !ReadText(reader, settings.ChatMaxLength, out string text) ||
                reader.AvailableBytes != 0) return false;
            message = new ChatMessage(id, owner, name, global == 1, anchor, text);
            return true;
        }

        public static void WriteChatReport(NetDataWriter writer, ulong id, string reason)
        {
            if (id == 0 || !IsSafeText(reason, ReportLimit)) throw new ArgumentException("신고가 잘못됐습니다.");
            writer.Reset(); writer.Put((byte)PacketKind.ChatReport); writer.Put(id); writer.Put(reason.Trim());
        }

        public static bool TryReadReport(NetDataReader reader, out ulong id, out string reason)
        {
            id = 0; reason = null;
            if (!HasKind(reader, PacketKind.ChatReport) || reader.AvailableBytes < 8) return false;
            id = reader.GetULong();
            return id != 0 && ReadText(reader, ReportLimit, out reason) && reader.AvailableBytes == 0;
        }

        public static void WriteLobbyState(NetDataWriter writer, string title, int selfIndex, int minimum, int capacity,
            IReadOnlyList<LobbyMember> members, int aiCount = 0, IReadOnlyList<AiDifficulty> aiDifficulties = null, IReadOnlyList<int> aiColorIds = null)
        {
            if (aiCount < 0 || aiCount > byte.MaxValue || aiDifficulties != null && aiDifficulties.Count != aiCount)
                throw new ArgumentOutOfRangeException(nameof(aiCount));
            if (aiDifficulties != null)
                foreach (AiDifficulty difficulty in aiDifficulties)
                    if (difficulty > AiDifficulty.Hard) throw new ArgumentOutOfRangeException(nameof(aiDifficulties));
            if (members.Count + aiCount > PlayerPalette.Count || aiColorIds != null && aiColorIds.Count != aiCount)
                throw new ArgumentOutOfRangeException(nameof(aiColorIds));
            int used = 0;
            var colors = new int[members.Count + aiCount];
            for (int index = 0; index < colors.Length; index++)
            {
                int color = index < members.Count ? members[index].ColorId : aiColorIds?[index - members.Count] ?? -1;
                colors[index] = color;
                if (color == -1) continue;
                if (!PlayerPalette.IsValid(color) || (used & (1 << color)) != 0) throw new ArgumentException("소유 색이 잘못됐습니다.");
                used |= 1 << color;
            }
            for (int index = 0; index < colors.Length; index++)
                if (colors[index] == -1)
                {
                    int color = 0; while ((used & (1 << color)) != 0) color++;
                    colors[index] = color; used |= 1 << color;
                }
            writer.Reset(); writer.Put((byte)PacketKind.LobbyState); writer.Put((byte)selfIndex); writer.Put((byte)members.Count);
            writer.Put((byte)minimum); writer.Put((byte)capacity); writer.Put(checked((byte)aiCount));
            for (int index = 0; index < aiCount; index++) writer.Put((byte)(aiDifficulties?[index] ?? AiDifficulty.Normal));
            for (int index = 0; index < aiCount; index++) writer.Put((byte)colors[members.Count + index]);
            for (int index = 0; index < members.Count; index++)
            {
                LobbyMember member = members[index];
                writer.Put((byte)member.Element); writer.Put((byte)(member.Ready ? 1 : 0)); writer.Put((byte)colors[index]); writer.Put(member.Nickname);
            }
            writer.Put(title);
        }

        public static bool TryReadLobbyState(NetDataReader reader, GameRules rules, out LobbyState state)
        {
            state = default;
            if (!HasKind(reader, PacketKind.LobbyState) || reader.AvailableBytes < 5) return false;
            int self = reader.GetByte(), count = reader.GetByte(), minimum = reader.GetByte(), capacity = reader.GetByte();
            int aiCount = reader.GetByte();
            if (count < 1 || self >= count || count + aiCount > capacity || minimum < 2 || minimum > capacity || capacity > rules.MaxPlayers || capacity > PlayerPalette.Count) return false;
            if (reader.AvailableBytes < aiCount) return false;
            var difficulties = new AiDifficulty[aiCount];
            for (int index = 0; index < aiCount; index++)
            {
                difficulties[index] = (AiDifficulty)reader.GetByte();
                if (difficulties[index] > AiDifficulty.Hard) return false;
            }
            if (reader.AvailableBytes < aiCount) return false;
            int used = 0;
            var aiColors = new int[aiCount];
            for (int index = 0; index < aiCount; index++)
            {
                int color = reader.GetByte();
                if (!PlayerPalette.IsValid(color) || (used & (1 << color)) != 0) return false;
                aiColors[index] = color; used |= 1 << color;
            }
            var members = new LobbyMember[count];
            for (int index = 0; index < count; index++)
            {
                if (reader.AvailableBytes < 3) return false;
                int element = reader.GetByte(), ready = reader.GetByte(), color = reader.GetByte();
                if (!PlayerPalette.IsValid(color) || (used & (1 << color)) != 0) return false;
                used |= 1 << color;
                if (ready > 1 || !rules.StartingElements.Contains(element) || !Protocol.TryReadNickname(reader, out string nickname)) return false;
                members[index] = new LobbyMember(nickname, element, ready == 1, color);
            }
            if (!ReadText(reader, TitleLimit, out string title) || reader.AvailableBytes != 0) return false;
            state = new LobbyState(title, self, minimum, capacity, members, difficulties, aiColors);
            return true;
        }

        public static void WriteLobbyReady(NetDataWriter writer, int element, bool ready)
        { writer.Reset(); writer.Put((byte)PacketKind.LobbyReady); writer.Put((byte)element); writer.Put((byte)(ready ? 1 : 0)); }

        public static bool TryReadLobbyReady(NetDataReader reader, GameRules rules, out int element, out bool ready)
        {
            element = -1; ready = false;
            if (!HasKind(reader, PacketKind.LobbyReady) || reader.AvailableBytes != 2) return false;
            element = reader.GetByte(); byte flag = reader.GetByte(); ready = flag == 1;
            return flag <= 1 && rules.StartingElements.Contains(element);
        }

        public static void WriteLobbyColor(NetDataWriter writer, int colorId)
        {
            if (!PlayerPalette.IsValid(colorId)) throw new ArgumentOutOfRangeException(nameof(colorId));
            writer.Reset(); writer.Put((byte)PacketKind.LobbyColor); writer.Put((byte)colorId);
        }

        public static bool TryReadLobbyColor(NetDataReader reader, out int colorId)
        {
            colorId = -1;
            if (!HasKind(reader, PacketKind.LobbyColor) || reader.AvailableBytes != 1) return false;
            colorId = reader.GetByte(); return PlayerPalette.IsValid(colorId);
        }

        public static void WriteLobbyStart(NetDataWriter writer)
        { writer.Reset(); writer.Put((byte)PacketKind.LobbyStart); }
        public static bool TryReadLobbyStart(NetDataReader reader) => HasKind(reader, PacketKind.LobbyStart) && reader.AvailableBytes == 0;
        public static void WriteLobbyAiCount(NetDataWriter writer, int count)
        { writer.Reset(); writer.Put((byte)PacketKind.LobbyAiCount); writer.Put(checked((byte)count)); }

        public static bool TryReadLobbyAiCount(NetDataReader reader, out int count)
        {
            count = 0;
            if (!HasKind(reader, PacketKind.LobbyAiCount) || reader.AvailableBytes != 1) return false;
            count = reader.GetByte();
            return true;
        }

        public static void WriteLobbyAiDifficulty(NetDataWriter writer, int aiIndex, AiDifficulty difficulty)
        {
            if (aiIndex < 0 || aiIndex > byte.MaxValue || difficulty > AiDifficulty.Hard)
                throw new ArgumentOutOfRangeException(nameof(aiIndex));
            writer.Reset(); writer.Put((byte)PacketKind.LobbyAiDifficulty); writer.Put((byte)aiIndex); writer.Put((byte)difficulty);
        }

        public static bool TryReadLobbyAiDifficulty(NetDataReader reader, out int aiIndex, out AiDifficulty difficulty)
        {
            aiIndex = -1; difficulty = AiDifficulty.Normal;
            if (!HasKind(reader, PacketKind.LobbyAiDifficulty) || reader.AvailableBytes != 2) return false;
            aiIndex = reader.GetByte(); difficulty = (AiDifficulty)reader.GetByte();
            return difficulty <= AiDifficulty.Hard;
        }

        public static void WriteLobbyError(NetDataWriter writer, LobbyError error)
        { writer.Reset(); writer.Put((byte)PacketKind.LobbyError); writer.Put((byte)error); }
        public static bool TryReadLobbyError(NetDataReader reader, out LobbyError error)
        {
            error = default;
            if (!HasKind(reader, PacketKind.LobbyError) || reader.AvailableBytes != 1) return false;
            error = (LobbyError)reader.GetByte();
            return error >= LobbyError.NotHost && error <= LobbyError.ColorTaken;
        }

        public static void WriteDiscovery(NetDataWriter writer, uint nonce)
        { writer.Reset(); writer.Put((byte)PacketKind.RoomDiscovery); writer.Put(Protocol.Version); writer.Put(nonce); }
        public static bool TryReadDiscovery(NetDataReader reader, out uint nonce)
        {
            nonce = 0;
            if (!HasKind(reader, PacketKind.RoomDiscovery) || reader.AvailableBytes != 6 || reader.GetUShort() != Protocol.Version) return false;
            nonce = reader.GetUInt();
            return nonce != 0;
        }

        public static void WriteRoomListing(NetDataWriter writer, uint nonce, LanRoomListing room)
        {
            writer.Reset(); writer.Put((byte)PacketKind.RoomListing); writer.Put(Protocol.Version); writer.Put(nonce);
            writer.Put((ushort)room.Port); writer.Put((byte)room.Count); writer.Put((byte)room.MinimumPlayers);
            writer.Put((byte)room.Capacity); writer.Put((byte)(room.Running ? 1 : 0)); writer.Put(room.Title);
        }

        public static bool TryReadRoomListing(NetDataReader reader, out uint nonce, out LanRoomListing room)
        {
            nonce = 0; room = default;
            if (!HasKind(reader, PacketKind.RoomListing) || reader.AvailableBytes < 12 || reader.GetUShort() != Protocol.Version) return false;
            nonce = reader.GetUInt(); int port = reader.GetUShort(), count = reader.GetByte();
            int minimum = reader.GetByte(), capacity = reader.GetByte(), running = reader.GetByte();
            if (nonce == 0 || port == 0 || count > capacity || minimum < 2 || minimum > capacity || capacity > 63 || running > 1 ||
                !ReadText(reader, TitleLimit, out string title) || reader.AvailableBytes != 0) return false;
            room = new LanRoomListing(port, title, count, minimum, capacity, running == 1);
            return true;
        }

        private static bool HasKind(NetDataReader reader, PacketKind kind) => reader.AvailableBytes > 0 &&
            reader.AvailableBytes <= Protocol.MaxDatagramBytes && reader.GetByte() == (byte)kind;

        private static bool ReadText(NetDataReader reader, int maxLength, out string value)
        {
            value = null;
            if (reader.AvailableBytes < sizeof(ushort)) return false;
            int bytes = reader.GetUShort() - 1;
            if (bytes < 1 || bytes > maxLength * 3 || reader.AvailableBytes < bytes) return false;
            try { value = Utf8.GetString(reader.RawData, reader.Position, bytes); }
            catch (DecoderFallbackException) { return false; }
            reader.SkipBytes(bytes);
            return IsSafeText(value, maxLength);
        }
    }
}
