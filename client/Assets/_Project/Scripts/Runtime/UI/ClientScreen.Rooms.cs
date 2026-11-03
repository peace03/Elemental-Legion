using System;
using System.Collections.Generic;
using System.Globalization;
using ElementalLegion.Shared.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    internal sealed partial class ClientScreen
    {
        private const int MinimumPasswordLength = 4;
        private const int MaximumPasswordLength = 8;
        private readonly List<Selectable> roomControls = new List<Selectable>();
        private TMP_Text lobbyTitle, lobbyStatus;
        private Button addAiButton, removeAiButton;
        private TMP_Text[] memberRows;
        private TMP_Dropdown lobbyElement;
        private Button readyButton, startButton;
        private LobbyState visibleLobby;

        internal readonly struct RoomCard
        {
            internal readonly string Title, Details, ActionLabel;
            internal readonly Action Join;
            internal RoomCard(string title, int count, int capacity, bool running, Action join, bool persistent = false, bool passwordRequired = false)
            {
                Title = title;
                Details = $"{count}/{capacity}명   ·   {(persistent ? "상시 월드" : running ? "게임 중" : "대기 중")}";
                if (passwordRequired) Details += "   ·   비밀번호 필요";
                ActionLabel = running && !persistent ? "복귀" : "입장";
                Join = join;
            }
        }

        private void RoomTabs(RectTransform page, int active, Action<int> tab, Action create)
        {
            string[] labels = { "인터넷", "같은 네트워크", "저장한 주소" };
            for (int index = 0; index < labels.Length; index++)
            {
                int target = index;
                roomControls.Add(ui.Button(page, labels[index], 162 * index, 83, 152, 37, () => tab(target), active == index));
            }
            roomControls.Add(ui.Button(page, "방 만들기", 900, 83, 160, 37, create));
        }

        internal void BuildRoomIndex(int active, IReadOnlyList<RoomCard> rooms, string[] starting, int selected, Action<int> choose,
            Action<int> tab, Action create, Action refresh, Action back)
        {
            RectTransform page = Center("Room browser", 1060, 560);
            Header(page, "DEPLOYMENT", "방 찾기", back);
            serverBack = first.GetComponentInChildren<TMP_Text>();
            RoomTabs(page, active, tab, create);
            RectTransform list = ObservatoryUi.Panel(page, "Public rooms", 0, 129, 1060, 390, ObservatoryUi.Surface);
            var refreshButton = ui.Button(list, "새로고침", 884, 10, 156, 38, refresh);
            roomControls.Add(refreshButton);
            first = refreshButton;
            ui.Label(list, "Room source", active == 0 ? "인터넷 방" : "같은 네트워크에서 발견한 방", 21, 22, 10, 520, 38, ObservatoryUi.Accent);
            ui.Label(list, "Element label", "주력 원소", 15, 540, 10, 100, 38, ObservatoryUi.Muted);
            TMP_Dropdown preferred = ui.Dropdown(list, starting, selected, 650, 10, 222);
            preferred.onValueChanged.AddListener(index => choose(index));
            roomControls.Add(preferred);

            var roomRows = new List<(Button Join, TMP_Text Title, TMP_Text Details, TMP_Text Action)>();
            const int pageSize = 6;
            int pageIndex = 0;
            TMP_Text pager = ui.Label(list, "Page number", "", 15, 135, 349, 105, 34);
            Button previous = ui.Button(list, "이전", 20, 349, 100, 34, null);
            Button next = ui.Button(list, "다음", 240, 349, 100, 34, null);
            previous.onClick.AddListener(() => { pageIndex--; RenderPage(); });
            next.onClick.AddListener(() => { pageIndex++; RenderPage(); });
            roomControls.Add(previous);
            roomControls.Add(next);
            for (int row = 0; row < pageSize; row++)
            {
                int captured = row;
                Button joinRoom = ui.Button(list, "입장", 20, 53 + 48 * row, 1020, 43, () => rooms[pageIndex * pageSize + captured].Join());
                RectTransform line = (RectTransform)joinRoom.transform;
                TMP_Text action = joinRoom.GetComponentInChildren<TMP_Text>();
                ObservatoryUi.Place(action.rectTransform, Vector2.up, new Vector2(860, 0), new Vector2(145, 43));
                action.color = ObservatoryUi.Accent;
                TMP_Text title = ui.Label(line, "Room title", "", 17, 12, 0, 525, 43);
                title.richText = title.parseCtrlCharacters = false;
                title.overflowMode = TextOverflowModes.Ellipsis;
                TMP_Text details = ui.Label(line, "Room status", "", 15, 550, 0, 300, 43, ObservatoryUi.Muted);
                roomRows.Add((joinRoom, title, details, action));
                roomControls.Add(joinRoom);
            }
            RenderPage();
            status = ui.Label(page, "Connection status", rooms.Count == 0 ? "표시할 방이 없습니다." : "입장할 방을 선택하세요.", 16, 12, 525, 1020, 32, ObservatoryUi.Muted);

            void RenderPage()
            {
                int pageCount = Math.Max(1, (rooms.Count + pageSize - 1) / pageSize);
                pageIndex = Math.Max(0, Math.Min(pageIndex, pageCount - 1));
                pager.text = $"{pageIndex + 1}/{pageCount}";
                previous.gameObject.SetActive(pageIndex > 0);
                next.gameObject.SetActive(pageIndex + 1 < pageCount);
                for (int row = 0; row < roomRows.Count; row++)
                {
                    int index = pageIndex * pageSize + row;
                    var line = roomRows[row];
                    line.Join.gameObject.SetActive(index < rooms.Count);
                    if (index >= rooms.Count) continue;
                    RoomCard room = rooms[index];
                    line.Title.text = room.Title;
                    line.Details.text = room.Details;
                    line.Action.text = room.ActionLabel;
                }
            }
        }

        internal void BuildRoomForm(int capacity, bool preferLan, Action<string, int, int, bool, string> create, Action reopen, Action back)
        {
            RectTransform page = Center("Room creation", 650, 475);
            Header(page, "DEPLOYMENT", "방 만들기", back);
            serverBack = first.GetComponentInChildren<TMP_Text>();
            ui.Label(page, "Room title", "방 제목", 17, 24, 92, 280, 28);
            TMP_InputField title = ui.Input(page, "", 24, 126, 602);
            title.characterLimit = M5Protocol.TitleLimit;
            title.richText = false;
            title.textComponent.richText = false;
            ((TMP_Text)title.placeholder).text = "최대 32자";
            ui.Label(page, "Minimum players", "최소 시작 인원", 17, 24, 183, 280, 29);
            ui.Label(page, "Room capacity", "정원", 17, 328, 183, 280, 29);
            TMP_InputField minimum = ui.Input(page, "2", 24, 219, 274);
            TMP_InputField maximum = ui.Input(page, capacity.ToString(CultureInfo.InvariantCulture), 328, 219, 298);
            minimum.contentType = maximum.contentType = TMP_InputField.ContentType.IntegerNumber;
            minimum.characterLimit = maximum.characterLimit = 2;
            ui.Label(page, "Network type", "방 공개 범위", 17, 24, 272, 280, 29);
            TMP_Dropdown scope = ui.Dropdown(page, new[] { "인터넷", "같은 네트워크" }, preferLan ? 1 : 0, 24, 310, 274);
            RectTransform passwordArea = ObservatoryUi.Rect(page, "Room password", 328, 272, 298, 84);
            ui.Label(passwordArea, "Password label", "비밀번호 (선택)", 17, 0, 0, 298, 29);
            TMP_InputField password = RoomPasswordInput(passwordArea, 0, 38, 298);
            bool requiresPasswordReentry = false;
            passwordArea.gameObject.SetActive(!preferLan);
            scope.onValueChanged.AddListener(value =>
            {
                if (value == 1)
                {
                    password.SetTextWithoutNotify("");
                    requiresPasswordReentry = false;
                }
                passwordArea.gameObject.SetActive(value == 0);
            });
            status = ui.Label(page, "Creation status", "", 15, 24, 359, 602, 32, ObservatoryUi.Muted);
            Button submit = ui.Button(page, "방 만들기", 24, 405, reopen == null ? 602 : 294, 46, () =>
            {
                if (!M5Protocol.IsSafeText(title.text.Trim(), M5Protocol.TitleLimit) ||
                    !int.TryParse(minimum.text, out int min) || !int.TryParse(maximum.text, out int max) || min < 2 || min > max || max > capacity)
                { status.text = $"방 제목과 인원을 확인하세요. 시작은 최소 2명, 정원은 최대 {capacity}명입니다."; return; }
                bool lan = scope.value == 1;
                string pin = lan ? "" : password.text;
                if (!lan && requiresPasswordReentry && pin.Length == 0)
                { status.text = "보호 방의 비밀번호를 다시 입력하세요."; return; }
                if (pin.Length > 0 && !IsRoomPassword(pin))
                { status.text = "비밀번호는 숫자 4~8자리여야 합니다."; return; }
                requiresPasswordReentry = pin.Length > 0;
                password.SetTextWithoutNotify("");
                create(title.text.Trim(), min, max, lan, pin);
            }, true);
            roomControls.AddRange(new Selectable[] { title, minimum, maximum, scope, password, submit });
            if (reopen != null) roomControls.Add(ui.Button(page, "저장한 LAN 방 다시 열기", 330, 405, 296, 46, reopen));
            first = title;
        }

        internal void BuildRoomPassword(string title, Action<string> join, Action back)
        {
            RectTransform page = Center("Protected room", 650, 350);
            Header(page, "DEPLOYMENT", "비밀번호 입력", back);
            serverBack = first.GetComponentInChildren<TMP_Text>();
            TMP_Text room = ui.Label(page, "Room title", "", 22, 24, 96, 602, 36, ObservatoryUi.Accent);
            room.richText = room.parseCtrlCharacters = false;
            room.text = title;
            room.overflowMode = TextOverflowModes.Ellipsis;
            ui.Label(page, "Password label", "방 비밀번호", 17, 24, 145, 602, 28);
            TMP_InputField password = RoomPasswordInput(page, 24, 179, 602);
            status = ui.Label(page, "Join status", "", 15, 24, 233, 602, 36, ObservatoryUi.Muted);
            Button submit = ui.Button(page, "입장", 24, 284, 602, 46, () =>
            {
                string pin = password.text;
                if (!IsRoomPassword(pin))
                { status.text = "비밀번호는 숫자 4~8자리여야 합니다."; return; }
                password.SetTextWithoutNotify("");
                join(pin);
            }, true);
            roomControls.AddRange(new Selectable[] { password, submit });
            first = password;
        }

        private TMP_InputField RoomPasswordInput(Transform parent, float x, float y, float width)
        {
            TMP_InputField input = ui.Input(parent, "", x, y, width);
            input.contentType = TMP_InputField.ContentType.Pin;
            input.characterLimit = MaximumPasswordLength;
            ((TMP_Text)input.placeholder).text = "숫자 4~8자리";
            return input;
        }

        private static bool IsRoomPassword(string value)
        {
            if (value.Length is < MinimumPasswordLength or > MaximumPasswordLength) return false;
            foreach (char digit in value)
                if (digit is < '0' or > '9') return false;
            return true;
        }

        internal void BuildLobby(string[] names, int chosen, int capacity,
            Action<int, bool> setReady, Action<int> setAiCount, Action start, Action leave)
        {
            RectTransform page = Center("Lobby", 1060, 560);
            Header(page, "DEPLOYMENT", "대기실", leave);
            first.GetComponentInChildren<TMP_Text>().text = "대기실 나가기";
            RectTransform people = ObservatoryUi.Panel(page, "Lobby members", 0, 100, 620, 453, ObservatoryUi.Surface);
            lobbyTitle = ui.Label(people, "Room title", "", 24, 20, 10, 584, 39, ObservatoryUi.Accent);
            lobbyTitle.richText = lobbyTitle.parseCtrlCharacters = false;
            lobbyTitle.overflowMode = TextOverflowModes.Ellipsis;
            memberRows = new TMP_Text[capacity];
            for (int index = 0; index < capacity; index++)
            {
                memberRows[index] = ui.Label(people, "Member", "", 15, 20, 53 + 25 * index, 584, 23);
                memberRows[index].richText = false;
            }
            RectTransform options = ObservatoryUi.Panel(page, "Lobby actions", 646, 100, 414, 453, ObservatoryUi.Surface);
            ui.Label(options, "Element", "주력 원소", 19, 23, 21, 368, 33);
            lobbyElement = ui.Dropdown(options, names, chosen, 23, 64, 368);
            lobbyElement.onValueChanged.AddListener(index => setReady(index, false));
            readyButton = ui.Button(options, "준비", 23, 131, 368, 44, () => setReady(lobbyElement.value, !visibleLobby.Members[visibleLobby.SelfIndex].Ready));
            addAiButton = ui.Button(options, "AI 추가", 23, 187, 179, 44, () => setAiCount(visibleLobby.AiCount + 1));
            removeAiButton = ui.Button(options, "AI 제거", 212, 187, 179, 44, () => setAiCount(visibleLobby.AiCount - 1));
            startButton = ui.Button(options, "게임 시작", 23, 243, 368, 44, start, true);
            lobbyStatus = ui.Label(options, "Lobby status", "", 16, 23, 303, 368, 128, ObservatoryUi.Muted);
            first = readyButton;
        }

        internal void SetLobby(LobbyState lobby, string[] allNames, IReadOnlyList<int> starting, LobbyError? error)
        {
            visibleLobby = lobby;
            lobbyTitle.text = lobby.Title;
            int occupied = lobby.Members.Length + lobby.AiCount;
            bool allReady = occupied >= lobby.MinimumPlayers;
            for (int index = 0; index < memberRows.Length; index++)
            {
                memberRows[index].gameObject.SetActive(index < occupied);
                if (index >= occupied) continue;
                if (index >= lobby.Members.Length)
                {
                    memberRows[index].text = $"AI 군단 {index - lobby.Members.Length + 1}   ·   준비 완료";
                    memberRows[index].color = ObservatoryUi.Accent;
                    continue;
                }
                LobbyMember member = lobby.Members[index];
                string role = (index == 0 ? "방장  " : "") + (index == lobby.SelfIndex ? "나  " : "");
                memberRows[index].text = $"{role}{member.Nickname}   ·   {allNames[member.Element]}   ·   {(member.Ready ? "준비 완료" : "대기 중")}";
                memberRows[index].color = member.Ready ? ObservatoryUi.Accent : ObservatoryUi.TextColor;
                allReady &= member.Ready;
            }
            LobbyMember self = lobby.Members[lobby.SelfIndex];
            // 준비가 확정되기 전에는 대기실 갱신으로 고르는 중인 원소를 덮어쓰지 않는다.
            if (self.Ready)
                for (int index = 0; index < starting.Count; index++)
                    if (starting[index] == self.Element) lobbyElement.SetValueWithoutNotify(index);
            lobbyElement.interactable = !self.Ready;
            readyButton.GetComponentInChildren<TMP_Text>().text = self.Ready ? "준비 취소" : "준비";
            bool host = lobby.SelfIndex == 0;
            addAiButton.gameObject.SetActive(host);
            removeAiButton.gameObject.SetActive(host);
            startButton.gameObject.SetActive(host);
            addAiButton.interactable = occupied < lobby.Capacity;
            removeAiButton.interactable = lobby.AiCount > 0;
            startButton.interactable = allReady;
            string summary = $"플레이어 {lobby.Members.Length}명 · AI {lobby.AiCount}명\n현재 {occupied}/{lobby.Capacity}자리 · 최소 {lobby.MinimumPlayers}자리\n플레이어가 모두 준비하면 시작할 수 있습니다.";
            lobbyStatus.text = error switch
            {
                LobbyError.NotHost => "방장만 AI를 조정하거나 게임을 시작할 수 있습니다.",
                LobbyError.NotReady => "최소 인원과 준비 상태를 확인하세요.",
                LobbyError.SpawnFailed => "모두가 시작할 자리를 확보하지 못했습니다.",
                LobbyError.InvalidState => "대기실 상태가 바뀌었습니다.",
                LobbyError.InvalidPacket => "대기실 명령을 처리하지 못했습니다.",
                LobbyError.RoomFull => "방에 빈자리가 없습니다.",
                _ => summary
            };
        }
    }
}
