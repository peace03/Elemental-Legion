using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;

namespace ElementalLegion.Bots;

/// <summary>게임과 같은 HTTPS 입장 절차로 전용 테스트 방을 열고 방장 프로세스를 소유한다.</summary>
internal sealed class HostedLoadRoom(LoadOptions options, GameRules rules, string? initialTrace = null) : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = options.Relay,
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 256 * 1024
    };
    private readonly string password = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    private string? room;
    private HostedServerProcess? host;
    internal int HostProcessId => host?.Id ?? -1;

    internal async Task<string> CreateAccount(string name, CancellationToken stop)
    {
        JsonElement body = await Post("/accounts", new { nickname = name }, null, stop);
        string token = body.GetProperty("token").GetString() ?? "";
        if (token.Length != Protocol.AccountKeyBytes * 2 || !token.All(Uri.IsHexDigit))
            throw new InvalidDataException("중계가 잘못된 계정 키를 보냈습니다.");
        return token;
    }

    internal async Task StartHost(string token, CancellationToken stop)
    {
        JsonElement created = await Post("/hosted-rooms", new
        {
            title = $"M6 {Guid.NewGuid():N}"[..19], minimumPlayers = options.Bots, capacity = options.Bots,
            password, version = Protocol.Version, dataHash = rules.DataHash
        }, token, stop);
        string? roomId = created.GetProperty("room").GetProperty("id").GetString();
        if (!Guid.TryParseExact(roomId, "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("중계가 잘못된 방 ID를 보냈습니다.");
        room = id.ToString("N");
        string? hostKey = created.GetProperty("hostKey").GetString();
        if (hostKey == null || hostKey.Length != Protocol.AccountKeyBytes * 2 || !hostKey.All(Uri.IsHexDigit))
            throw new InvalidDataException("중계가 잘못된 방장 키를 보냈습니다.");

        string input = JsonSerializer.Serialize(new { serviceUrl = options.Relay!.GetLeftPart(UriPartial.Authority), roomId = room, hostKey });
        host = await HostedServerProcess.StartAsync(options.HostServer!, input, standby: false, stop,
            tracePath: initialTrace ?? options.ServerTrace);
    }

    internal async Task Connect(LoadPeer peer, string token, CancellationToken stop)
    {
        JsonElement body = await Post($"/rooms/{room}/tickets", new
        {
            version = Protocol.Version, dataHash = rules.DataHash, startElement = peer.Element, password
        }, token, stop);
        byte[] ticket = Convert.FromHexString(body.GetProperty("ticket").GetString() ?? "");
        try
        {
            var endpoint = new UriBuilder(options.Relay!) { Scheme = "wss", Path = $"/rooms/{room}/play" };
            peer.Connection.ConnectOnline(endpoint.Uri, ticket, supportsReports: false);
        }
        finally { CryptographicOperations.ZeroMemory(ticket); }
    }

    internal async Task<int> ListedPlayers(CancellationToken stop)
    {
        using var response = await http.GetAsync("/rooms", stop);
        if (!response.IsSuccessStatusCode) throw new IOException($"방 목록 요청이 실패했습니다. HTTP {(int)response.StatusCode}.");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: stop);
        foreach (JsonElement entry in body.GetProperty("rooms").EnumerateArray())
            if (entry.GetProperty("id").GetString() == room) return entry.GetProperty("count").GetInt32();
        return -1;
    }

    private async Task<JsonElement> Post(string path, object body, string? token, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, stop);
        if (!response.IsSuccessStatusCode)
        {
            string detail = "";
            try
            {
                var rejected = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: stop);
                if (rejected.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 and <= 40 } code && code.All(char.IsAsciiLetter)) detail = " " + code;
            }
            catch (JsonException) { }
            throw new IOException($"{path.Split('/')[^1]} 요청이 실패했습니다. HTTP {(int)response.StatusCode}.{detail}");
        }
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: stop);
    }

    public void Dispose()
    {
        http.Dispose();
        host?.Dispose();
    }
}
