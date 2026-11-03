using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ElementalLegion.Shared.Net;

namespace ElementalLegion.Client.Bootstrap
{
    /// <summary>고정 HTTPS 서비스에서 계정과 방 목록, 입장권을 요청한다.</summary>
    internal sealed class OnlineRoomService
    {
        private const int MaxResponseBytes = 65536;
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        private readonly Uri endpoint;
        private readonly string keyScope;

        internal OnlineRoomService(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
                parsed.UserInfo.Length != 0 || parsed.AbsolutePath != "/" || parsed.Query.Length != 0 || parsed.Fragment.Length != 0)
                throw new InvalidOperationException("인터넷 서비스 주소가 설정되지 않았습니다.");
            endpoint = parsed;
            keyScope = "online." + endpoint.Authority;
        }

        internal async Task<Room[]> List(CancellationToken stop)
        {
            var response = await Request<RoomList>(HttpMethod.Get, "/rooms", null, null, stop);
            if (response.rooms == null || response.rooms.Length > 256) throw new IOException();
            foreach (Room room in response.rooms) Validate(room);
            return response.rooms;
        }

        internal async Task<(Room Room, string LaunchInput)> CreateHosted(
            string title, int minimum, int capacity, string password, string nickname, string hash, CancellationToken stop)
        {
            string token = await Account(nickname, stop);
            var response = await Request<HostedRoomResponse>(HttpMethod.Post, "/hosted-rooms", new HostedRoomRequest
            {
                title = title, minimumPlayers = minimum, capacity = capacity, password = password,
                version = Protocol.Version, dataHash = hash
            }, token, stop);
            Validate(response.room);
            if (!response.room.playerHosted || response.hostKey == null || response.hostKey.Length != 64) throw new IOException();
            foreach (char digit in response.hostKey)
                if (!Uri.IsHexDigit(digit)) throw new IOException();
            string launchInput = JsonUtility.ToJson(new HostLaunchInput
            {
                serviceUrl = endpoint.GetLeftPart(UriPartial.Authority), roomId = response.room.id, hostKey = response.hostKey
            });
            return (response.room, launchInput);
        }

        internal async Task<(Uri Endpoint, byte[] Ticket)> Join(
            Room room, string nickname, string hash, int startElement, CancellationToken stop, string password = null)
        {
            Validate(room);
            string token = await Account(nickname, stop);
            string path = "/rooms/" + room.id;
            var response = await Request<TicketResponse>(HttpMethod.Post, path + "/tickets",
                new TicketRequest { version = Protocol.Version, dataHash = hash, startElement = startElement, password = password }, token, stop);
            Uri address = new UriBuilder(endpoint) { Scheme = "wss", Port = endpoint.Port, Path = path + "/play" }.Uri;
            return (address, DecodeKey(response.ticket));
        }

        private async Task<string> Account(string nickname, CancellationToken stop)
        {
            byte[] key = LocalAccountKeys.Load(keyScope);
            if (key != null)
            {
                try { return BitConverter.ToString(key).Replace("-", "").ToLowerInvariant(); }
                finally { Array.Clear(key, 0, key.Length); }
            }
            var response = await Request<AccountResponse>(HttpMethod.Post, "/accounts", new AccountRequest { nickname = nickname }, null, stop);
            key = DecodeKey(response.token);
            try { LocalAccountKeys.Save(keyScope, key); }
            finally { Array.Clear(key, 0, key.Length); }
            return response.token;
        }

        private async Task<T> Request<T>(HttpMethod method, string path, object body, string token, CancellationToken stop) where T : class
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
            if (body != null) request.Content = new StringContent(JsonUtility.ToJson(body), Encoding.UTF8, "application/json");
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new IOException();
            using Stream stream = await response.Content.ReadAsStreamAsync();
            using var output = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk, 0, chunk.Length, timeout.Token)) != 0)
            {
                if (output.Length + count > MaxResponseBytes) throw new IOException();
                output.Write(chunk, 0, count);
            }
            string json = new UTF8Encoding(false, true).GetString(output.ToArray());
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(ErrorText(JsonUtility.FromJson<FailureResponse>(json)?.error));
            return JsonUtility.FromJson<T>(json) ?? throw new IOException();
        }

        private static byte[] DecodeKey(string hex)
        {
            if (hex == null || hex.Length != 64) throw new IOException();
            var key = new byte[32];
            for (int index = 0; index < key.Length; index++)
                key[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
            return key;
        }

        private static void Validate(Room room)
        {
            if (room == null || !Guid.TryParseExact(room.id, "N", out _) || string.IsNullOrWhiteSpace(room.title) || room.title.Length > 32 ||
                room.minimumPlayers < 1 || room.minimumPlayers > room.capacity || room.capacity > 63 || room.count < 0 || room.count > room.capacity)
                throw new IOException();
        }

        private static string ErrorText(string code) => code switch
        {
            "invalidNickname" => "타이틀에서 닉네임을 확인하세요.",
            "invalidRoom" => "방 제목과 인원 범위를 확인하세요.",
            "invalidPassword" => "비밀번호는 숫자 4~8자리여야 합니다.",
            "passwordRequired" => "방 비밀번호를 입력하세요.",
            "incorrectPassword" => "비밀번호가 틀렸습니다. 다시 입력하세요.",
            "invalidAccount" => "저장한 온라인 계정 키를 서버가 거절했습니다.",
            "rateLimited" => "요청이 너무 많습니다. 잠시 후 다시 시도하세요.",
            "roomCreationDisabled" => "서버에서 새 방 만들기를 중단했습니다.",
            "roomLimit" => "서버의 방 개수가 한도에 도달했습니다.",
            "alreadyInRoom" => "이미 방에 접속한 계정입니다.",
            "roomNotFound" => "방이 닫혔습니다. 목록을 새로 고치세요.",
            "roomFull" => "방의 정원이 찼습니다.",
            "versionMismatch" => "서버와 통신 버전이 다릅니다.",
            "invalidElement" => "허용되지 않은 시작 원소입니다.",
            "dataMismatch" => "서버와 게임 데이터가 다릅니다.",
            "reentryUnavailable" => "이 방에 복귀할 수 없습니다.",
            "hostPending" => "방장이 입장한 뒤 다시 시도하세요.",
            _ => "인터넷 서비스가 요청을 처리하지 못했습니다."
        };

        [Serializable] internal sealed class Room
        {
            public string id, title;
            public int count, capacity, minimumPlayers;
            public bool running, persistent, playerHosted, passwordRequired;
        }

        [Serializable] private sealed class RoomList { public Room[] rooms; }
        [Serializable] private sealed class HostedRoomRequest { public string title, password, dataHash; public int minimumPlayers, capacity, version; }
        [Serializable] private sealed class HostedRoomResponse { public Room room; public string hostKey; }
        [Serializable] private sealed class HostLaunchInput { public string serviceUrl, roomId, hostKey; }
        [Serializable] private sealed class AccountRequest { public string nickname; }
        [Serializable] private sealed class AccountResponse { public string token; }
        [Serializable] private sealed class TicketRequest { public int version, startElement; public string dataHash, password; }
        [Serializable] private sealed class TicketResponse { public string ticket; }
        [Serializable] private sealed class FailureResponse { public string error; }
    }
}
