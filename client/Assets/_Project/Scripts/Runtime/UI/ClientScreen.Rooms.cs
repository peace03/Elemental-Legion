using System;
using System.Collections.Generic;
using System.Globalization;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
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
        private TMP_Text lobbyTitle, lobbyStatus, lobbyCount;
        private Button addAiButton, removeAiButton;
        private TMP_Text[] memberRows;
        private TMP_Dropdown lobbyElement;
        private TMP_Dropdown[] aiDifficultyChoices;
        private Action<int, AiDifficulty> setLobbyAiDifficulty;
        private Button readyButton, startButton;
        private LobbyState visibleLobby;
        private UnityEngine.UI.Image[] memberIcons;
        private Button[] elementChoices;
        private UnityEngine.UI.Image elementPreview;
        private TMP_Text elementPreviewName;
        private LobbyElementTooltip lobbyElementTooltip;
        private string[] lobbyStartingNames;
        private int lobbySelection = -1;
        private Button[] colorChoices;
        private Image[] memberColors;
        private TMP_Text lobbyColorName;

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
                ui.Icon(line, "Room icon", ui.Art.Building(BuildingKind.Base), 8, 5, 33, 33);
                TMP_Text title = ui.Label(line, "Room title", "", 17, 52, 0, 485, 43);
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
            RectTransform page = Center("Room creation", 1060, 560);
            Header(page, "DEPLOYMENT", "방 만들기", back);
            RectTransform preview = ObservatoryUi.Panel(page, "Room preview", 664, 108, 372, 419, ObservatoryUi.Surface);
            ui.Label(preview, "Preview title", "새로운 은하", 21, 22, 18, 328, 34, ObservatoryUi.Gold);
            ui.Icon(preview, "Planetary core", ui.Art.Building(BuildingKind.Base), 51, 65, 270, 270);
            ui.Label(preview, "Preview caption", "별의 씨앗이 모이는 곳", 16, 22, 355, 328, 33, ObservatoryUi.Muted)
                .alignment = TextAlignmentOptions.Center;
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

        internal void BuildLobby(string[] names, GameRules rules, int chosen, int capacity,
            Action<int, bool> setReady, Action<int> setAiCount, Action<int, AiDifficulty> setAiDifficulty, Action start, Action leave,
            Action<int> setColor)
        {
            setLobbyAiDifficulty = setAiDifficulty;
            RectTransform page = Center("Lobby", 1120, 650);
            Header(page, "DEPLOYMENT", "대기실", leave);
            lobbyCount = ui.Label(page, "Lobby player count", $"0 / {capacity}", 23, 138, 40, 360, 42, ObservatoryUi.Accent);
            first.GetComponentInChildren<TMP_Text>().text = "대기실 나가기";
            RectTransform people = ObservatoryUi.Panel(page, "Lobby members", 0, 100, 680, 490, ObservatoryUi.Surface);
            lobbyTitle = ui.Label(people, "Room title", "", 24, 20, 10, 640, 39, ObservatoryUi.Gold);
            lobbyTitle.richText = lobbyTitle.parseCtrlCharacters = false;
            lobbyTitle.overflowMode = TextOverflowModes.Ellipsis;
            memberRows = new TMP_Text[capacity];
            memberIcons = new UnityEngine.UI.Image[capacity];
            memberColors = new Image[capacity];
            aiDifficultyChoices = new TMP_Dropdown[capacity];
            float rowHeight = Mathf.Min(58, 426f / capacity);
            for (int index = 0; index < capacity; index++)
            {
                RectTransform row = ObservatoryUi.Panel(people, "Participant", 18, 55 + rowHeight * index, 644, rowHeight - 3, ObservatoryUi.Raised);
                memberColors[index] = ObservatoryUi.Panel(row, "Owner color", 0, 0, 4, rowHeight - 3, Color.white).GetComponent<Image>();
                float iconSize = Mathf.Min(35, rowHeight - 7);
                memberIcons[index] = ui.Icon(row, "Participant element", ui.Art.Building(BuildingKind.Base), 6, 2, iconSize, iconSize);
                memberRows[index] = ui.Label(row, "Member", "", 15, 46, 0, 586, rowHeight - 3);
                memberRows[index].richText = false;
                row.gameObject.SetActive(false);
            }
            RectTransform options = ObservatoryUi.Panel(page, "Lobby actions", 706, 100, 414, 490, ObservatoryUi.Surface);
            ui.Label(options, "Element", "주력 원소", 21, 23, 12, 368, 33, ObservatoryUi.Gold);
            lobbyElement = ui.Dropdown(options, names, chosen, 23, 64, 368);
            lobbyElement.onValueChanged.AddListener(index => setReady(index, false));
            lobbyElement.gameObject.SetActive(false);
            lobbyStartingNames = names;
            elementPreview = ui.Icon(options, "Selected element", ui.Art.Element(names[chosen]), 23, 43, 64, 55);
            elementPreviewName = ui.Label(options, "Selected element name", names[chosen], 19, 103, 57, 288, 29, ObservatoryUi.Accent);
            elementChoices = new Button[names.Length];
            lobbyElementTooltip = new LobbyElementTooltip(ui, page);
            for (int index = 0; index < names.Length; index++)
            {
                int option = index;
                Button choice = ui.Button(options, names[index], 23 + index % 4 * 94, 106 + index / 4 * 48, 86, 42, () =>
                {
                    lobbyElement.SetValueWithoutNotify(option);
                    RefreshLobbyElement();
                    setReady(option, false);
                });
                ui.Icon(choice.transform, "Element icon", ui.Art.Element(names[index]), 30, 1, 26, 24);
                TMP_Text caption = choice.GetComponentInChildren<TMP_Text>();
                caption.fontSize = 12;
                ObservatoryUi.Place(caption.rectTransform, Vector2.up, new Vector2(2, -24), new Vector2(82, 18));
                choice.gameObject.AddComponent<LobbyElementHover>().Bind(lobbyElementTooltip, names[index],
                    rules.Elements[rules.StartingElements[index]].Role);
                elementChoices[index] = choice;
            }
            lobbyColorName = ui.Label(options, "Owner color label", "소유 색상", 19, 23, 209, 368, 29, ObservatoryUi.Gold);
            colorChoices = new Button[PlayerPalette.Count];
            for (int index = 0; index < colorChoices.Length; index++)
            {
                int option = index;
                Button choice = null;
                choice = ui.Button(options, (index + 1).ToString("D2"), 23 + index % 5 * 74, 242 + index / 5 * 37,
                    70, 32, () => { if (choice.interactable) setColor(option); });
                choice.name = "Owner color " + (index + 1).ToString("D2");
                Image swatch = ObservatoryUi.Panel(choice.transform, "Swatch", 4, 4, 62, 24, PlayerColors.Get(index)).GetComponent<Image>();
                swatch.transform.SetAsFirstSibling();
                TMP_Text caption = choice.GetComponentInChildren<TMP_Text>();
                caption.fontSize = 13;
                ObservatoryUi.Place(caption.rectTransform, Vector2.up, Vector2.zero, new Vector2(70, 32));
                Color linear = PlayerColors.Get(index).linear;
                caption.color = linear.r * .2126f + linear.g * .7152f + linear.b * .0722f > .179f ? Color.black : Color.white;
                colorChoices[index] = choice;
            }
            readyButton = ui.Button(options, "준비", 23, 360, 368, 36, () => setReady(lobbyElement.value, !visibleLobby.Members[visibleLobby.SelfIndex].Ready), true);
            addAiButton = ui.Button(options, "AI 추가", 23, 405, 179, 28, () => setAiCount(visibleLobby.AiCount + 1));
            removeAiButton = ui.Button(options, "AI 제거", 212, 405, 179, 28, () => setAiCount(visibleLobby.AiCount - 1));
            startButton = ui.Button(options, "게임 시작", 23, 445, 368, 36, start, true);
            lobbyStatus = ui.Label(page, "Lobby status", "", 13, 0, 598, 1120, 34, ObservatoryUi.Muted);
            ui.Label(page, "Palette credit", "소유 색상은 건물과 바닥 링에 적용됩니다.  ·  팔레트: HoloViz / Peter Kovesi, Colorcet (CC BY 4.0), 밝기 보정", 10,
                0, 634, 1120, 16, ObservatoryUi.Muted);
            RefreshLobbyElement();
            first = readyButton;
        }

        private void RefreshLobbyElement()
        {
            if (lobbySelection == lobbyElement.value) return;
            lobbySelection = lobbyElement.value;
            elementPreview.sprite = ui.Art.Element(lobbyStartingNames[lobbySelection]);
            elementPreviewName.text = lobbyStartingNames[lobbySelection];
            for (int index = 0; index < elementChoices.Length; index++)
                ui.StyleButton(elementChoices[index], index == lobbySelection);
        }

        internal void SetLobby(LobbyState lobby, string[] allNames, IReadOnlyList<int> starting, LobbyError? error, bool recovering = false)
        {
            visibleLobby = lobby;
            lobbyTitle.text = lobby.Title;
            int occupied = lobby.Members.Length + lobby.AiCount;
            lobbyCount.text = $"{occupied} / {lobby.Capacity}";
            bool allReady = occupied >= lobby.MinimumPlayers;
            for (int index = 0; index < memberRows.Length; index++)
            {
                memberRows[index].transform.parent.gameObject.SetActive(index < occupied);
                if (aiDifficultyChoices[index] != null)
                    aiDifficultyChoices[index].gameObject.SetActive(index < occupied && index >= lobby.Members.Length);
                if (index >= occupied) continue;
                if (index >= lobby.Members.Length)
                {
                    int aiIndex = index - lobby.Members.Length;
                    memberRows[index].text = $"{lobby.AiColorIds[aiIndex] + 1:D2}   AI 군단 {aiIndex + 1}   ·   준비 완료";
                    memberColors[index].color = PlayerColors.Get(lobby.AiColorIds[aiIndex]);
                    memberRows[index].color = ObservatoryUi.Accent;
                    memberRows[index].rectTransform.sizeDelta = new Vector2(450, memberRows[index].rectTransform.sizeDelta.y);
                    memberIcons[index].sprite = ui.Art.Building(BuildingKind.Base);
                    TMP_Dropdown difficulty = aiDifficultyChoices[index];
                    if (difficulty == null)
                    {
                        int rowIndex = index;
                        var row = (RectTransform)memberRows[index].transform.parent;
                        difficulty = ui.Dropdown(row, new[] { "쉬움", "보통", "어려움" }, (int)AiDifficulty.Normal, 510, 1, 126);
                        difficulty.name = "AI difficulty";
                        ((RectTransform)difficulty.transform).sizeDelta = new Vector2(126, row.rect.height - 2);
                        difficulty.captionText.fontSize = 14;
                        difficulty.captionText.rectTransform.offsetMin = new Vector2(8, 0);
                        difficulty.captionText.rectTransform.offsetMax = new Vector2(-25, 0);
                        aiDifficultyChoices[index] = difficulty;
                        difficulty.onValueChanged.AddListener(value =>
                        {
                            if (!difficulty.interactable || visibleLobby.SelfIndex != 0) return;
                            int ai = rowIndex - visibleLobby.Members.Length;
                            if (ai >= 0 && ai < visibleLobby.AiCount) setLobbyAiDifficulty(ai, (AiDifficulty)value);
                        });
                    }
                    difficulty.SetValueWithoutNotify((int)lobby.AiDifficulties[aiIndex]);
                    difficulty.interactable = lobby.SelfIndex == 0 && !recovering;
                    continue;
                }
                memberRows[index].rectTransform.sizeDelta = new Vector2(586, memberRows[index].rectTransform.sizeDelta.y);
                LobbyMember member = lobby.Members[index];
                string role = (index == 0 ? "방장  " : "") + (index == lobby.SelfIndex ? "나  " : "");
                memberRows[index].text = $"{member.ColorId + 1:D2}   {role}{member.Nickname}   ·   {allNames[member.Element]}   ·   {(member.Ready ? "준비 완료" : "대기 중")}";
                memberColors[index].color = PlayerColors.Get(member.ColorId);
                memberRows[index].color = member.Ready ? ObservatoryUi.Accent : ObservatoryUi.TextColor;
                memberIcons[index].sprite = ui.Art.Element(allNames[member.Element]);
                allReady &= member.Ready;
            }
            LobbyMember self = lobby.Members[lobby.SelfIndex];
            lobbyColorName.text = $"소유 색상 · {self.ColorId + 1:D2}  (× 다른 플레이어)";
            for (int color = 0; color < colorChoices.Length; color++)
            {
                bool taken = false;
                for (int member = 0; member < lobby.Members.Length; member++)
                    if (member != lobby.SelfIndex && lobby.Members[member].ColorId == color) { taken = true; break; }
                Button choice = colorChoices[color];
                choice.interactable = !self.Ready && !recovering && !taken;
                bool selected = color == self.ColorId;
                choice.GetComponentInChildren<TMP_Text>().text = (selected ? "✓ " : taken ? "× " : "") + (color + 1).ToString("D2");
                var frame = (LegionUiFrame)choice.targetGraphic;
                frame.Style(ui.Art.CornerCut, selected ? 2 : ui.Art.LineWidth, selected ? Color.white : ObservatoryUi.Gold);
            }
            // 준비가 확정되기 전에는 대기실 갱신으로 고르는 중인 원소를 덮어쓰지 않는다.
            if (self.Ready)
                for (int index = 0; index < starting.Count; index++)
                    if (starting[index] == self.Element) lobbyElement.SetValueWithoutNotify(index);
            lobbyElement.interactable = !self.Ready && !recovering;
            foreach (Button choice in elementChoices) choice.interactable = !self.Ready && !recovering;
            RefreshLobbyElement();
            readyButton.interactable = !recovering;
            readyButton.GetComponentInChildren<TMP_Text>().text = self.Ready ? "준비 취소" : "준비";
            bool host = lobby.SelfIndex == 0;
            addAiButton.gameObject.SetActive(host);
            removeAiButton.gameObject.SetActive(host);
            startButton.gameObject.SetActive(host);
            addAiButton.interactable = !recovering && occupied < lobby.Capacity;
            removeAiButton.interactable = !recovering && lobby.AiCount > 0;
            startButton.interactable = !recovering && allReady;
            string summary = $"플레이어 {lobby.Members.Length}명 · AI {lobby.AiCount}명 · 시작 최소 {lobby.MinimumPlayers}명 · 모두 준비하면 시작할 수 있습니다. AI에는 남은 색이 자동 배정됩니다.";
            lobbyStatus.text = recovering ? "연결 복구 중…" : error switch
            {
                LobbyError.NotHost => "방장만 AI를 조정하거나 게임을 시작할 수 있습니다.",
                LobbyError.NotReady => "최소 인원과 준비 상태를 확인하세요.",
                LobbyError.SpawnFailed => "모두가 시작할 자리를 확보하지 못했습니다.",
                LobbyError.InvalidState => "대기실 상태가 바뀌었습니다.",
                LobbyError.InvalidPacket => "대기실 명령을 처리하지 못했습니다.",
                LobbyError.RoomFull => "방에 빈자리가 없습니다.",
                LobbyError.ColorTaken => "다른 플레이어가 선택한 색상입니다. 다른 색을 골라 주세요.",
                _ => summary
            };
        }
    }
}
