using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ElementalLegion.Client.Bootstrap
{
    public sealed partial class ClientFlow
    {
        [SerializeField, Tooltip("배포에서 사용하는 고정 HTTPS 서비스 주소입니다. 예: https://play.example.com")]
        private string onlineServiceUrl;

        private enum RoomTab { Internet, Lan, Saved }
        private RoomTab roomTab = RoomTab.Saved;
        private IReadOnlyList<ClientScreen.RoomCard> roomCards = Array.Empty<ClientScreen.RoomCard>();
        private NetworkSettings network;
        private CancellationTokenSource roomOperation;
        private ClientConnection pendingConnection;
        private JoinTarget pendingRoomTarget;
        private bool showingRoomForm;

        private static string ReadRelayTestUrl()
        {
            try
            {
                string directory = Application.dataPath;
#if UNITY_EDITOR
                directory = Path.Combine(directory, "../../.artifacts/relay-test");
                try
                {
                    using FileStream stopped = File.Open(Path.Combine(directory, "relay.lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return null;
                }
                // Windows 공유 위반은 실행기가 잠금을 보유하고 있다는 뜻이다.
                catch (IOException error) when ((error.HResult & 0xffff) == 32) { }
#endif
                var file = new FileInfo(Path.Combine(directory, "relay-test-url.txt"));
                if (!file.Exists || file.Length > 128) return null;
                string address = File.ReadAllText(file.FullName).Trim();
                return Regex.IsMatch(address, @"\Ahttps://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com\z") ? address : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private void NewScreen()
        {
            if (screen != null)
            {
                screen.gameObject.SetActive(false);
                Destroy(screen.gameObject);
            }
            var holder = new GameObject("Observatory screen");
            SceneManager.MoveGameObjectToScene(holder, primary);
            screen = holder.AddComponent<ClientScreen>();
            screen.Initialize(ui, 20);
            screen.Scale(preferences.Display.UiPercent);
        }

        private void ShowBrowser(string detail = "")
        {
            showingRoomForm = false;
            NewScreen();
            if (roomTab == RoomTab.Saved)
            {
                screen.BuildServers(startingNames, address, element, chosen => element = chosen,
                    BeginJoin, SaveAddress, ServerBack, SelectRoomTab, ShowRoomForm);
                screen.SetServers(addresses);
            }
            else screen.BuildRoomIndex((int)roomTab, roomCards, startingNames, element, chosen => element = chosen,
                SelectRoomTab, ShowRoomForm, RefreshRooms, ServerBack);
            if (detail.Length > 0) screen.SetConnecting(false, detail);
            screen.Focus();
        }

        private void SelectRoomTab(int index)
        {
            if (busy || (int)roomTab == index) return;
            roomTab = (RoomTab)index;
            roomCards = Array.Empty<ClientScreen.RoomCard>();
            ShowBrowser();
            if (roomTab != RoomTab.Saved) RefreshRooms();
        }

        private void ShowRoomForm()
        {
            if (busy) return;
            showingRoomForm = true;
            NewScreen();
            Action reopen = LanRoomService.LatestSavedRoom() is Guid room ? () => ReopenRoom(room) : null;
            screen.BuildRoomForm(rules.MaxPlayers, roomTab == RoomTab.Lan || string.IsNullOrWhiteSpace(onlineServiceUrl), CreateRoom,
                reopen, ServerBack);
            screen.Focus();
        }

        private void RefreshRooms()
        {
            if (busy) return;
            if (roomTab == RoomTab.Internet)
                StartCoroutine(RoomRequest(token => new OnlineRoomService(onlineServiceUrl).List(token), rooms =>
                {
                    var cards = new List<ClientScreen.RoomCard>(rooms.Length);
                    foreach (OnlineRoomService.Room room in rooms)
                        cards.Add(new ClientScreen.RoomCard(room.title, room.count, room.capacity, room.running,
                            () => OpenOnlineRoom(room), room.persistent, room.passwordRequired));
                    roomCards = cards;
                    ShowBrowser();
                }, "인터넷 방을 불러오는 중…"));
            else
                StartCoroutine(RoomRequest(token => LanRoomService.Discover(network.DiscoveryPort, token), rooms =>
                {
                    var cards = new List<ClientScreen.RoomCard>(rooms.Count);
                    foreach (LanRoomService.Room room in rooms)
                        cards.Add(new ClientScreen.RoomCard(room.Listing.Title, room.Listing.Count, room.Listing.Capacity,
                            room.Listing.Running, () => BeginLanJoin(room.Address, room.Listing.Port, false, !room.Listing.Running)));
                    roomCards = cards;
                    ShowBrowser();
                }, "같은 네트워크에서 방을 찾는 중…"));
        }

        private void CreateRoom(string title, int minimum, int capacity, bool lan, string password)
        {
            if (busy) return;
            StartCoroutine(RoomRequest(async token =>
            {
                if (lan)
                {
                    var launch = await LanRoomService.Launch(title, minimum, capacity, token);
                    return new JoinTarget { Address = LocalAddress, Port = launch.Port, Secret = launch.Proof };
                }
                var online = new OnlineRoomService(onlineServiceUrl);
                var hosted = await online.CreateHosted(title, minimum, capacity, password, preferences.Nickname, rules.DataHash, token);
                LanRoomService.HostedLaunch host = await LanRoomService.LaunchHosted(hosted.LaunchInput,
                    useTestRelayBuild: onlineServiceUrl == ReadRelayTestUrl(), stop: token);
                try
                {
                    var join = await online.Join(hosted.Room, preferences.Nickname, rules.DataHash, rules.StartingElements[element], token);
                    return new JoinTarget { Online = join.Endpoint, Secret = join.Ticket, PlayerHosted = hosted.Room.playerHosted, Host = host };
                }
                catch { host.Dispose(); throw; }
            }, target =>
            {
                RoomTab source = lan ? RoomTab.Lan : RoomTab.Internet;
                if (roomTab != source) roomCards = Array.Empty<ClientScreen.RoomCard>();
                roomTab = source;
                StartCoroutine(ConnectToRoom(target));
            }, "방을 만드는 중…"));
        }

        private void ReopenRoom(Guid room)
        {
            if (busy) return;
            StartCoroutine(RoomRequest(async token => new JoinTarget
            {
                Address = LocalAddress,
                Port = await LanRoomService.Reopen(room, token),
                RequireResume = true
            }, target =>
            {
                if (roomTab != RoomTab.Lan) roomCards = Array.Empty<ClientScreen.RoomCard>();
                roomTab = RoomTab.Lan;
                StartCoroutine(ConnectToRoom(target));
            }, "저장된 LAN 방을 여는 중…"));
        }

        private void OpenOnlineRoom(OnlineRoomService.Room room)
        {
            if (busy) return;
            if (!room.passwordRequired)
            {
                JoinOnlineRoom(room, null);
                return;
            }
            showingRoomForm = true;
            NewScreen();
            screen.BuildRoomPassword(room.title, password => JoinOnlineRoom(room, password), ServerBack);
            screen.Focus();
        }

        private void JoinOnlineRoom(OnlineRoomService.Room room, string password)
        {
            if (busy) return;
            StartCoroutine(RoomRequest(async token =>
            {
                var join = await new OnlineRoomService(onlineServiceUrl).Join(
                    room, preferences.Nickname, rules.DataHash, rules.StartingElements[element], token, password);
                return new JoinTarget { Online = join.Endpoint, Secret = join.Ticket, PlayerHosted = room.playerHosted };
            }, target => StartCoroutine(ConnectToRoom(target)), "입장권을 받는 중…"));
        }

        private void BeginLanJoin(string host, int port, bool remember, bool recoverWaitingRoom = false)
        {
            StartCoroutine(RoomRequest(async token =>
            {
                if (!IPAddress.TryParse(host, out IPAddress ip))
                {
                    Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
                    _ = lookup.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    if (await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(8), token)) != lookup)
                        throw new InvalidOperationException("서버 주소를 확인하지 못했습니다.");
                    IPAddress[] candidates = await lookup;
                    ip = Array.Find(candidates, candidate => candidate.AddressFamily == AddressFamily.InterNetwork) ??
                         Array.Find(candidates, candidate => candidate.AddressFamily == AddressFamily.InterNetworkV6);
                    if (ip == null) throw new InvalidOperationException("서버 주소를 확인하지 못했습니다.");
                }
                return new JoinTarget
                {
                    Address = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString(), Port = port,
                    Remember = remember, RecoverWaitingRoom = recoverWaitingRoom
                };
            }, target => StartCoroutine(ConnectToRoom(target)), "서버 주소 확인 중…"));
        }

        private IEnumerator RoomRequest<T>(Func<CancellationToken, Task<T>> request, Action<T> apply, string progress) where T : class
        {
            busy = joining = true;
            cancelled = false;
            screen.SetConnecting(true, progress);
            using var lifetime = new CancellationTokenSource();
            roomOperation = lifetime;
            Task<T> task = null;
            Exception failure = null;
            try { task = request(lifetime.Token); }
            catch (Exception exception) { failure = exception; }
            if (task != null)
                while (!task.IsCompleted) yield return null;
            roomOperation = null;
            busy = joining = false;
            if (cancelled)
            {
                if (task?.Status == TaskStatus.RanToCompletion && task.Result is IDisposable disposable) disposable.Dispose();
                _ = task?.Exception;
                screen.SetConnecting(false, "요청을 취소했습니다.");
            }
            else if (task?.Status == TaskStatus.RanToCompletion)
            {
                screen.SetConnecting(false, "");
                apply(task.Result);
            }
            else screen.SetConnecting(false, RoomRequestError(failure ?? task?.Exception?.GetBaseException()));
        }

        private IEnumerator ConnectToRoom(JoinTarget target)
        {
            using var lifetime = target;
            pendingRoomTarget = target;
            busy = joining = true;
            cancelled = false;
            screen.SetConnecting(true, "서버에 접속 중…");
            var transport = new ClientConnection(rules, network) { DeferGameplay = true };
            pendingConnection = transport;
            bool local = target.Online == null && LanRoomService.IsTrusted(IPAddress.Parse(target.Address));
            string accountScope = local ? $"lan.{target.Address}:{target.Port}" : null;
            bool resumeAttempted = false;
            string error = null;
            try
            {
                if (target.Online != null) transport.ConnectOnline(target.Online, target.Secret, supportsReports: !target.PlayerHosted);
                else ConnectLan(target.Secret == null, target.Secret);
            }
            catch (Exception exception) { error = RoomRequestError(exception); }
            finally { target.ClearSecret(); }

            LobbyMember[] members = null;
            LobbyError? lobbyError = null;
            while (!cancelled && error == null && (transport.State == ConnectionState.Connecting || transport.State == ConnectionState.Lobby))
            {
                transport.Poll();
                if (!cancelled && transport.State is ConnectionState.Lobby or ConnectionState.Connected)
                    target.DetachHost();
                // 새 대기방이 같은 포트를 다시 사용했을 때만 신규 입장을 한 번 시도한다.
                if (members == null && resumeAttempted && target.RecoverWaitingRoom && transport.JoinFailure == JoinFailure.InvalidResumeKey)
                {
                    screen.SetConnecting(true, "새 대기실에 접속 중…");
                    try { ConnectLan(false, null); }
                    catch (Exception exception) { error = RoomRequestError(exception); }
                }
                if (transport.Lobby is LobbyState lobby && (lobby.Members != members || lobbyError != transport.LastLobbyError))
                {
                    if (members == null)
                    {
                        NewScreen();
                        screen.BuildLobby(startingNames, element, lobby.Capacity, (chosen, ready) =>
                        {
                            element = chosen;
                            transport.SetLobbyReady(rules.StartingElements[chosen], ready);
                        },
                            transport.SetLobbyAiCount, transport.StartRoom, ServerBack);
                        screen.Focus();
                        if (target.Online != null && rules.StartingElements[element] != lobby.Members[lobby.SelfIndex].Element)
                            transport.SetLobbyReady(rules.StartingElements[element], false);
                    }
                    members = lobby.Members;
                    lobbyError = transport.LastLobbyError;
                    screen.SetLobby(lobby, elementNames, rules.StartingElements, lobbyError);
                }
                yield return null;
            }
            if (!cancelled && error == null && transport.State == ConnectionState.Connected && transport.IssuedAccountKey != null)
            {
                try
                {
                    if (accountScope == null) throw new InvalidOperationException("온라인 서버의 입장 응답이 잘못됐습니다.");
                    LocalAccountKeys.Save(accountScope, transport.IssuedAccountKey);
                    Array.Clear(transport.IssuedAccountKey, 0, transport.IssuedAccountKey.Length);
                }
                catch (Exception) { error = "Windows에 복귀 키를 저장하지 못했습니다."; }
            }
            if (!cancelled && error == null && transport.State == ConnectionState.Connected)
            {
                if (target.Remember) RememberAddress(address);
                if (members != null) screen.SetInteractive(false);
                else if (transport.Resumed) screen.SetConnecting(true, "이전 군단으로 복귀하는 중…");
                yield return LoadField(transport);
            }
            else ShowBrowser(cancelled ? "접속을 취소했습니다." : error ?? ClientBootstrap.FailureText(transport.JoinFailure, transport.Error));
            if (game == null) transport.Dispose();
            pendingConnection = null;
            pendingRoomTarget = null;
            busy = joining = false;
            RefreshInput();

            void ConnectLan(bool allowResume, byte[] proof)
            {
                byte[] key = local && allowResume ? LocalAccountKeys.Load(accountScope) : null;
                if (target.RequireResume && key == null) throw new InvalidOperationException("이 PC에 저장된 방 복귀 키가 없습니다.");
                resumeAttempted = key != null;
                try { transport.Connect(target.Address, target.Port, rules.StartingElements[element], preferences.Nickname, key, proof, local); }
                finally { if (key != null) Array.Clear(key, 0, key.Length); }
            }
        }

        private bool TryEndpoint(string value, out string host, out int port)
        {
            host = null;
            port = network.Port;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 260) return false;
            if (IPAddress.TryParse(value, out IPAddress ip)) { host = ip.ToString(); return true; }
            if (!Uri.TryCreate("udp://" + value, UriKind.Absolute, out Uri uri) || uri.UserInfo.Length > 0 ||
                (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                uri.HostNameType == UriHostNameType.Unknown || uri.Port == 0 || uri.Host.Length > 253) return false;
            host = uri.DnsSafeHost;
            if (uri.Port > 0) port = uri.Port;
            return true;
        }

        private static string RoomRequestError(Exception error) => error switch
        {
            InvalidOperationException invalid => invalid.Message,
            OperationCanceledException => "서버 응답 시간이 초과됐습니다.",
            System.Security.Cryptography.CryptographicException => "Windows에 저장된 복귀 키를 읽을 수 없습니다.",
            _ => "서버 연결을 확인하세요. 요청을 완료하지 못했습니다."
        };

        private sealed class JoinTarget : IDisposable
        {
            internal Uri Online;
            internal string Address;
            internal int Port;
            internal byte[] Secret;
            internal bool Remember, RecoverWaitingRoom, RequireResume, PlayerHosted;
            internal LanRoomService.HostedLaunch Host;

            internal void ClearSecret()
            {
                if (Secret == null) return;
                Array.Clear(Secret, 0, Secret.Length);
                Secret = null;
            }

            internal void DetachHost()
            {
                Host?.Detach();
                Host = null;
            }

            public void Dispose()
            {
                ClearSecret();
                Host?.Dispose();
                Host = null;
            }
        }
    }
}
