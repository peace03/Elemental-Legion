using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElementalLegion.Server;

/// <summary>인증된 백업 소켓에서 크기·해시·메시지 경계를 확인한다.</summary>
internal static class MigrationSnapshotTransfer
{
    private sealed record Header(int Length, string Sha256);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 2,
        AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task Write(WebSocket socket, byte[] payload, CancellationToken stop)
    {
        if (payload.Length == 0 || payload.Length > MigrationSnapshotCodec.MaxCompressedBytes)
            throw new InvalidDataException("승계 백업의 전송 크기가 잘못됐습니다.");
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(new Header(payload.Length, Convert.ToHexStringLower(SHA256.HashData(payload))), Json);
        await socket.SendAsync(header.AsMemory(), WebSocketMessageType.Text, true, stop);
        for (int offset = 0; offset < payload.Length; offset += MigrationSnapshotCodec.ChunkBytes)
            await socket.SendAsync(payload.AsMemory(offset, Math.Min(MigrationSnapshotCodec.ChunkBytes, payload.Length - offset)),
                WebSocketMessageType.Binary, true, stop);
        await socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, stop);
    }

    internal static async Task<byte[]> Read(WebSocket socket, CancellationToken stop)
    {
        var headerBytes = new byte[257];
        int headerLength = await ReadMessage(socket, headerBytes, WebSocketMessageType.Text, stop);
        Header? header;
        try { header = JsonSerializer.Deserialize<Header>(headerBytes.AsSpan(0, headerLength), Json); }
        catch (JsonException error) { throw new InvalidDataException("승계 백업 전송 정보가 잘못됐습니다.", error); }
        if (header == null || header.Length < 1 || header.Length > MigrationSnapshotCodec.MaxCompressedBytes ||
            header.Sha256?.Length != 64 || !header.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("승계 백업 전송 정보의 범위가 잘못됐습니다.");
        var payload = new byte[header.Length];
        var chunk = new byte[MigrationSnapshotCodec.ChunkBytes + 1];
        int offset = 0;
        for (int part = 0; part <= MigrationSnapshotCodec.MaxCompressedBytes / MigrationSnapshotCodec.ChunkBytes; part++)
        {
            int count = await ReadMessage(socket, chunk, WebSocketMessageType.Binary, stop);
            if (count == 0)
            {
                if (offset != payload.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), Convert.FromHexString(header.Sha256)))
                    throw new InvalidDataException("승계 백업 길이 또는 해시가 다릅니다.");
                return payload;
            }
            if (count > payload.Length - offset) throw new InvalidDataException("승계 백업 청크가 선언한 길이를 넘었습니다.");
            chunk.AsSpan(0, count).CopyTo(payload.AsSpan(offset));
            offset += count;
        }
        throw new InvalidDataException("승계 백업 청크 수가 상한을 넘었습니다.");
    }

    internal static async Task ReadAcknowledgment(WebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[3];
        int length = await ReadMessage(socket, buffer, WebSocketMessageType.Text, stop);
        if (length != 2 || buffer[0] != 'O' || buffer[1] != 'K')
            throw new InvalidDataException("중계가 승계 백업을 확인하지 않았습니다.");
    }

    private static async Task<int> ReadMessage(WebSocket socket, byte[] buffer, WebSocketMessageType expected, CancellationToken stop)
    {
        int length = 0;
        for (int fragments = 0; fragments < buffer.Length; fragments++)
        {
            ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer.AsMemory(length), stop);
            if (part.MessageType != expected) throw new InvalidDataException("승계 백업 메시지 종류가 잘못됐습니다.");
            length += part.Count;
            if (length >= buffer.Length) throw new InvalidDataException("승계 백업 메시지가 너무 큽니다.");
            if (part.EndOfMessage) return length;
        }
        throw new InvalidDataException("승계 백업의 분할 수가 너무 많습니다.");
    }
}
