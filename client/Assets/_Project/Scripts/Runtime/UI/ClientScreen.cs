using System;
using System.Collections.Generic;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>타이틀·서버 선택·메뉴·현황을 구성하고 화면 조작만 전달한다.</summary>
    internal sealed partial class ClientScreen : MonoBehaviour
    {
        private const int StandingRowHeight = 38;
        private ObservatoryUi ui;
        private RectTransform canvas, confirm, overview, matchups, standingsContent;
        private TMP_InputField host, nicknameInput;
        private UnityEngine.UI.Image nicknameBorder;
        private TMP_Text nicknameFocusHint;
        private Action nicknameFocusSound;
        private TMP_Dropdown element;
        private TMP_Text status, economy, progress, session, noStandings;
        private TMP_Text serverBack;
        private UnityEngine.UI.Button join, save;
        private RectTransform serverRows;
        private Selectable first, matchupEntry, matchupBack;
        private CanvasGroup group;
        private RawImage backdropArt;
        private RectTransform dashboardRoster;
        private TMP_Text[] dashboardCounts;
        private readonly List<GameObject> rows = new List<GameObject>();
        private readonly List<StandingRow> standingsRows = new List<StandingRow>();
        internal bool IsTyping => (nicknameInput != null && nicknameInput.isFocused) ||
            (host != null && (host.isFocused || element.IsExpanded)) ||
            (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null &&
                EventSystem.current.currentSelectedGameObject.TryGetComponent(out TMP_InputField field) && field.isFocused);
        internal bool Confirming => confirm != null;

        /// <summary>같은 순위 줄을 재사용하고 서버에서 바뀐 값만 갱신한다.</summary>
        private sealed class StandingRow
        {
            internal RectTransform Root;
            internal TMP_Text Player, Identifier, Score;
            internal UnityEngine.UI.Image OwnerColor;
            internal int PlayerId;
            internal long LastScore;
        }

        internal void Initialize(ObservatoryUi skin, int order, bool opaque = true)
        {
            ui = skin;
            canvas = ObservatoryUi.Canvas(transform, "Observatory canvas", order);
            group = canvas.gameObject.AddComponent<CanvasGroup>();
            backdropArt = ui.Backdrop(canvas, opaque);
        }

        internal void Scale(int percent) => ObservatoryUi.SetScale(canvas.GetComponent<CanvasScaler>(), percent);
        internal void SetInteractive(bool enabled)
        {
            group.interactable = group.blocksRaycasts = enabled;
            if (!enabled) lobbyElementTooltip?.Hide();
        }
        internal void Focus()
        {
            if (first != null && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(first.gameObject);
        }

        internal void BuildTitle(string nickname, Action<string> saveNickname, Action onNicknameFocus,
            Action play, Action settings, Action showMatchups, Action quit)
        {
            RectTransform panel = overview = Center("Title", 1120, 620);
            panel.GetComponent<UnityEngine.UI.Image>().color = Color.clear;
            panel.GetComponent<LegionUiFrame>().Style(0, 0, Color.clear);
            if (backdropArt != null) backdropArt.color = Color.white;
            ui.Icon(panel, "Core emblem", ui.Art.Building(BuildingKind.Base), 36, 10, 48, 48);
            ui.Label(panel, "Title subtitle", "원소의 군단", 19, 40, 167, 360, 29, ObservatoryUi.Gold);
            TMP_Text title = ui.Label(panel, "Game title", "ELEMENTAL\nLEGION", 44, 36, 38, 400, 134, ObservatoryUi.Gold);
            title.alignment = TextAlignmentOptions.TopLeft;
            title.lineSpacing = -7;
            ObservatoryUi.Panel(panel, "Title divider", 40, 207, 350, 1, ObservatoryUi.Gold);
            ui.Label(panel, "Nickname label", "닉네임", 15, 40, 224, 360, 25, ObservatoryUi.Muted);
            nicknameFocusHint = ui.Label(panel, "Nickname focus", "입력 중", 15, 290, 224, 110, 25, ObservatoryUi.Accent);
            nicknameFocusHint.alignment = TextAlignmentOptions.MidlineRight;
            nicknameFocusHint.gameObject.SetActive(false);
            nicknameBorder = ObservatoryUi.Panel(panel, "Nickname border", 37, 249, 366, 48, ObservatoryUi.Surface)
                .GetComponent<UnityEngine.UI.Image>();
            nicknameInput = ui.Input(panel, nickname, 40, 252, 360);
            nicknameInput.gameObject.name = "Nickname";
            nicknameInput.characterLimit = PlayerNickname.MaxLength;
            nicknameInput.richText = false;
            nicknameInput.customCaretColor = true;
            nicknameInput.caretColor = ObservatoryUi.Accent;
            nicknameInput.caretWidth = 2;
            nicknameFocusSound = onNicknameFocus;
            ((TMP_Text)nicknameInput.placeholder).text = "닉네임을 입력하세요";
            string hint = $"최대 {PlayerNickname.MaxLength}자 · 글자, 숫자, 공백, - 및 _";
            TMP_Text nicknameHint = ui.Label(panel, "Nickname hint", hint, 12, 40, 299, 400, 24, ObservatoryUi.Muted);

            bool RememberNickname(string value)
            {
                string trimmed = value.Trim(' ');
                bool valid = PlayerNickname.IsValid(trimmed);
                nicknameHint.text = valid ? hint : "글자나 숫자를 포함하고 사용할 수 있는 문자를 확인하세요.";
                nicknameHint.color = valid ? ObservatoryUi.Muted : ObservatoryUi.Danger;
                if (valid)
                {
                    nicknameInput.SetTextWithoutNotify(trimmed);
                    saveNickname(trimmed);
                }
                return valid;
            }

            nicknameInput.onEndEdit.AddListener(value => RememberNickname(value));
            first = ui.Button(panel, "시작하기", 40, 340, 360, 50, () =>
            {
                if (RememberNickname(nicknameInput.text)) play();
                else nicknameInput.ActivateInputField();
            }, true);
            matchupEntry = ui.Button(panel, "원소 상성표", 40, 404, 360, 43, showMatchups);
            ui.Button(panel, "설정", 40, 458, 360, 43, settings);
            ui.Button(panel, "게임 종료", 40, 512, 360, 43, () => Confirmation("게임을 종료할까요?", null, quit));
        }

        private void LateUpdate()
        {
            if (nicknameInput == null) return;
            // TMP는 선택 직후가 아니라 실제 편집이 시작된 프레임에 포커스를 보고한다.
            bool focused = nicknameInput.isFocused && nicknameInput.isActiveAndEnabled && group.interactable && Application.isFocused;
            if (nicknameFocusHint.gameObject.activeSelf == focused) return;
            nicknameFocusHint.gameObject.SetActive(focused);
            nicknameBorder.color = focused ? ObservatoryUi.Accent : ObservatoryUi.Surface;
            if (focused) nicknameFocusSound?.Invoke();
        }

        internal void BuildServers(string[] names, string address, int chosen, Action<int> choose, Action<string, int> connect,
            Action<string> saveAddress, Action back, Action<int> tab, Action create)
        {
            RectTransform page = Center("Server browser", 1060, 560);
            Header(page, "DEPLOYMENT", "방 찾기", back);
            serverBack = first.GetComponentInChildren<TMP_Text>();
            RoomTabs(page, 2, tab, create);
            RectTransform list = ObservatoryUi.Panel(page, "Saved servers", 0, 129, 520, 428, ObservatoryUi.Surface);
            ui.Label(list, "List title", "저장된 서버", 23, 22, 12, 460, 36);
            serverRows = ObservatoryUi.Rect(list, "Server rows", 22, 62, 476, 346);
            RectTransform form = ObservatoryUi.Panel(page, "Deployment", 548, 129, 512, 428, ObservatoryUi.Surface);
            ui.Label(form, "Form title", "군단 배치", 23, 24, 16, 464, 36, ObservatoryUi.Accent);
            ui.Label(form, "Address label", "서버 주소", 15, 24, 71, 462, 24, ObservatoryUi.Muted);
            host = ui.Input(form, address, 24, 99, 464);
            ui.Label(form, "Element label", "주력 원소", 15, 24, 155, 464, 24, ObservatoryUi.Muted);
            element = ui.Dropdown(form, names, chosen, 24, 184, 464);
            element.onValueChanged.AddListener(index => choose(index));
            status = ui.Label(form, "Connection status", "주소 또는 주소:포트와 주력 원소를 선택하세요.", 15, 24, 236, 464, 65, ObservatoryUi.Muted);
            join = ui.Button(form, "서버 들어가기", 24, 322, 464, 46, () => connect(host.text, element.value), true);
            save = ui.Button(form, "주소를 목록에 저장", 24, 379, 464, 40, () => saveAddress(host.text));
            first = join;
        }

        internal void SetServers(IReadOnlyList<string> addresses)
        {
            foreach (GameObject row in rows)
            {
                row.SetActive(false);
                Destroy(row);
            }
            rows.Clear();
            for (int index = 0; index < addresses.Count; index++)
            {
                string address = addresses[index];
                string label = index == 0 ? "로컬 테스트 서버   ·   " + address : index == 1 ? "M5 데모   ·   " + address : address;
                var row = ui.Button(serverRows, label, 0, index * 48, 476, 41, () => host.text = address);
                ui.ButtonIcon(row, ui.Art.Building(BuildingKind.Base), 27);
                row.GetComponentInChildren<TMP_Text>().overflowMode = TextOverflowModes.Ellipsis;
                rows.Add(row.gameObject);
            }
        }

        internal void SetConnecting(bool connecting, string message)
        {
            if (host != null)
            {
                host.interactable = element.interactable = join.interactable = save.interactable = !connecting;
                foreach (GameObject row in rows) row.GetComponent<UnityEngine.UI.Button>().interactable = !connecting;
            }
            foreach (Selectable control in roomControls) control.interactable = !connecting;
            serverBack.text = connecting ? "접속 취소" : "돌아가기";
            status.text = message;
        }

        internal void BuildPause(Action resume, Action settings, Action title, Action quit)
        {
            RectTransform panel = Center("Field menu", 430, 360);
            ui.Label(panel, "Section", "ELEMENTAL LEGION", 15, 28, 20, 374, 25, ObservatoryUi.Gold);
            ui.Label(panel, "Heading", "게임 메뉴", 32, 28, 55, 374, 45);
            ui.Label(panel, "Online status", "메뉴를 열어도 온라인 전투는 계속됩니다.", 15, 28, 107, 374, 36, ObservatoryUi.Muted);
            first = ui.Button(panel, "계속하기", 28, 162, 374, 48, resume, true);
            ui.Button(panel, "설정", 28, 222, 374, 48, settings);
            ui.Button(panel, "종료하기", 28, 282, 374, 48, () => Confirmation("전장을 떠날까요?", title, quit));
        }

        internal void BuildResult(bool victory, Action restart)
        {
            RectTransform panel = Center("Defeat", 482, 243);
            ui.Label(panel, "Section", "FIELD REPORT", 15, 28, 20, 426, 25, ObservatoryUi.Gold);
            ui.Label(panel, "Heading", victory ? "전투 승리" : "원시핵이 붕괴했습니다", 29, 28, 52, 426, 51,
                victory ? ObservatoryUi.Accent : ObservatoryUi.Danger);
            string summary = victory ? "마지막까지 본진을 지켰습니다." : "서버와 시작 원소를 골라 새 군단을 배치하세요.";
            ui.Label(panel, "Summary", summary, 16, 28, 108, 426, 33, ObservatoryUi.Muted);
            first = ui.Button(panel, "다시 시작", 28, 158, 426, 52, restart, true);
        }

        private void Confirmation(string text, Action title, Action quit)
        {
            confirm = ObservatoryUi.Panel(canvas, "Confirmation", 0, 0, 0, 0,
                new Color(0.025f, 0.055f, 0.075f, 0.9f), true);
            ObservatoryUi.Fill(confirm);
            foreach (Selectable control in canvas.GetComponentsInChildren<Selectable>()) control.interactable = false;
            RectTransform dialog = ObservatoryUi.Panel(confirm, "Question", 0, 0, 416, 280, ObservatoryUi.Surface);
            ObservatoryUi.Place(dialog, Vector2.one * 0.5f, Vector2.zero, new Vector2(416, 280));
            ui.Label(dialog, "Question", text, 25, 24, 24, 368, 46);
            if (title != null) ui.Button(dialog, "타이틀로 돌아가기", 24, 83, 368, 42, title, true);
            ui.Button(dialog, "게임 종료", 24, 137, 368, 42, quit);
            var cancel = ui.Button(dialog, "취소", 24, 192, 368, 42, CloseConfirmation);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(cancel.gameObject);
        }

        internal void CloseConfirmation()
        {
            if (confirm == null) return;
            confirm.gameObject.SetActive(false);
            Destroy(confirm.gameObject);
            confirm = null;
            foreach (Selectable control in canvas.GetComponentsInChildren<Selectable>()) control.interactable = true;
            Focus();
        }

        internal void BuildDashboard(Action back, Action showMatchups, string heldKey)
        {
            // 전장 카메라가 배경을 맡고 투명한 차단막만 게임 클릭을 막는다.
            canvas.Find("Backdrop").GetComponent<UnityEngine.UI.Image>().color = Color.clear;
            if (backdropArt != null) backdropArt.gameObject.SetActive(false);
            RectTransform panel = overview = Center("Player dashboard", 1120, 620);
            Header(panel, "LEGION STATUS", "현황", back);
            if (showMatchups != null) matchupEntry = ui.Button(panel, "원소 상성표", 786, 31, 160, 42, showMatchups);
            if (heldKey != null)
                ui.Label(panel, "Hold hint", $"{heldKey} 키를 떼면 닫힙니다.", 16, 730, 31, 390, 42, ObservatoryUi.Muted)
                    .alignment = TextAlignmentOptions.MidlineRight;

            RectTransform own = ObservatoryUi.Panel(panel, "Own player", 0, 100, 624, 512, ObservatoryUi.Surface);
            ui.Label(own, "Own title", "내 군단", 23, 22, 12, 580, 38, ObservatoryUi.Accent);
            economy = ui.Label(own, "Economy and population", "서버 상태 수신 중…", 17, 22, 57, 580, 95);
            economy.alignment = TextAlignmentOptions.TopLeft;
            progress = ui.Label(own, "Training progress", "", 17, 22, 151, 580, 32, ObservatoryUi.Gold);
            ui.Label(own, "Session title", "이번 접속", 20, 22, 184, 580, 31, ObservatoryUi.Accent);
            session = ui.Label(own, "Session statistics", "", 17, 22, 221, 580, 73);
            session.alignment = TextAlignmentOptions.TopLeft;
            ui.Label(own, "Roster title", "원소 구성", 17, 22, 298, 280, 25, ObservatoryUi.Gold);
            ui.Label(own, "Roster groups", "자연 원소 / 에너지 원소", 14, 325, 298, 275, 25, ObservatoryUi.Muted);
            dashboardRoster = ObservatoryUi.Rect(own, "Element roster", 22, 329, 580, 176);

            RectTransform standings = ObservatoryUi.Panel(panel, "Connected standings", 644, 100, 476, 512, ObservatoryUi.Surface);
            ui.Label(standings, "Standings title", "접속 플레이어 순위", 23, 22, 12, 430, 38, ObservatoryUi.Accent);
            ui.Label(standings, "Rank heading", "순위", 15, 22, 56, 60, 24, ObservatoryUi.Muted);
            ui.Label(standings, "Player heading", "닉네임", 15, 93, 56, 170, 24, ObservatoryUi.Muted);
            ui.Label(standings, "Score heading", "총점", 15, 254, 56, 182, 24, ObservatoryUi.Muted)
                .alignment = TextAlignmentOptions.MidlineRight;
            noStandings = ui.Label(standings, "Empty standings", "서버 상태 수신 중…", 16, 22, 86, 410, 33, ObservatoryUi.Muted);
            GameObject scrollRoot = RuntimeUiControls.CreateScrollView();
            scrollRoot.transform.SetParent(standings, false);
            ObservatoryUi.Place((RectTransform)scrollRoot.transform, Vector2.up, new Vector2(22, -86), new Vector2(432, 405));
            scrollRoot.GetComponent<UnityEngine.UI.Image>().color = Color.clear;
            var scroll = scrollRoot.GetComponent<UnityEngine.UI.ScrollRect>();
            scroll.horizontalScrollbar.gameObject.SetActive(false);
            scroll.horizontalScrollbar = null;
            scroll.horizontal = false;
            scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
            ObservatoryUi.StyleScroll(scroll);
            standingsContent = scroll.content;
            standingsContent.sizeDelta = Vector2.zero;
        }

        internal void Observe(GameplayFrame frame, GameRules rules, string[] names)
        {
            if (frame == null || frame.Tick < 0) return;
            int pendingBuildings = 0;
            foreach (var building in frame.Buildings)
                if (building.OwnerId == frame.PlayerId && building.IsUnderConstruction) pendingBuildings++;
            economy.text = $"주력 {names[frame.MainElement]}  ·  보유 핵 {frame.CoreBalance:N0}\n" +
                $"본진 레벨 {frame.BaseLevel}  ·  완공 시설 {frame.OwnBuildingCount}개  ·  건설 중 {pendingBuildings}개\n" +
                $"인구 {frame.Population} / {frame.PopulationCap}  ·  예약 {frame.ReservedPopulation}\n" +
                $"수비대 {frame.GarrisonCount}기  ·  집결 중 {frame.RallyingCount}기  ·  본진 대기 {frame.WaitingGarrisonCount}기";
            if (dashboardCounts == null)
            {
                dashboardCounts = new TMP_Text[rules.Elements.Count];
                int nature = 0, energy = 7;
                for (int index = 0; index < rules.Elements.Count; index++)
                {
                    int slot = rules.Elements[index].Group == ElementGroup.Nature ? nature++ : energy++;
                    float x = slot % 7 * 82;
                    float y = slot / 7 * 86;
                    RectTransform tile = ObservatoryUi.Panel(dashboardRoster, "Element", x, y, 77, 81, ObservatoryUi.Raised);
                    ui.Icon(tile, "Element icon", ui.Art.Element(rules.Elements[index].Id), 19, 3, 39, 39);
                    ui.Label(tile, "Element name", names[index], 11, 2, 41, 73, 19).alignment = TextAlignmentOptions.Center;
                    dashboardCounts[index] = ui.Label(tile, "Element count", "", 16, 2, 59, 73, 20, ObservatoryUi.Accent);
                    dashboardCounts[index].alignment = TextAlignmentOptions.Center;
                }
            }
            for (int index = 0; index < frame.ElementCounts.Length; index++)
                dashboardCounts[index].SetText("{0}", frame.ElementCounts[index]);
            string ratio = frame.ConvertedShare?.ToString("P1") ?? "집계 없음";
            session.text = $"전투 승리 {frame.BattleWins:N0}회 (적 본진 격파)\n" +
                $"양성 {frame.TrainedCount:N0}기  ·  전향 {frame.ConvertedCount:N0}기\n" +
                $"새 아군 중 전향 {ratio} (초기 3기 제외)";
            progress.text = frame.TrainingElement < 0 ? "양성 대기" :
                $"양성 중   {names[frame.TrainingElement]}   {Math.Max(0, (frame.TrainingEndTick - frame.Tick) * rules.DeltaTime):0.0}초";
            ObserveStandings(frame);
        }

        private void ObserveStandings(GameplayFrame frame)
        {
            int count = frame.Standings.Count;
            noStandings.text = "현재 접속한 플레이어가 없습니다.";
            noStandings.gameObject.SetActive(count == 0);
            for (int index = 0; index < count; index++)
            {
                if (index == standingsRows.Count) AddStandingRow(index);
                StandingRow row = standingsRows[index];
                row.Root.gameObject.SetActive(true);
                var standing = frame.Standings[index];
                if (row.PlayerId != standing.PlayerId)
                {
                    row.PlayerId = standing.PlayerId;
                    row.Player.text = standing.Nickname;
                    row.Identifier.text = $"#{standing.PlayerId} · 색상 {standing.ColorId + 1:D2}";
                    row.OwnerColor.color = PlayerColors.Get(standing.ColorId);
                    row.Player.color = row.Score.color = standing.PlayerId == frame.PlayerId ? ObservatoryUi.Accent : ObservatoryUi.TextColor;
                }
                if (row.LastScore != standing.Score)
                {
                    row.LastScore = standing.Score;
                    row.Score.text = standing.Score.ToString("N0");
                }
            }
            for (int index = count; index < standingsRows.Count; index++) standingsRows[index].Root.gameObject.SetActive(false);
            standingsContent.sizeDelta = new Vector2(0, count * StandingRowHeight);
        }

        private void AddStandingRow(int index)
        {
            RectTransform row = ObservatoryUi.Panel(standingsContent, "Standing", 0, index * StandingRowHeight,
                417, StandingRowHeight - 1, index % 2 == 0 ? ObservatoryUi.Raised : ObservatoryUi.Surface);
            var ownerColor = ObservatoryUi.Panel(row, "Owner color", 0, 0, 4, StandingRowHeight - 1, Color.white).GetComponent<UnityEngine.UI.Image>();
            ui.Label(row, "Rank", (index + 1).ToString(), 15, 0, 0, 60, StandingRowHeight, ObservatoryUi.Muted);
            var player = ui.Label(row, "Nickname", "", 16, 71, 2, 205, 21);
            player.richText = false;
            player.textWrappingMode = TextWrappingModes.NoWrap;
            player.overflowMode = TextOverflowModes.Ellipsis;
            player.enableAutoSizing = true;
            player.fontSizeMin = 11;
            player.fontSizeMax = 16;
            var identifier = ui.Label(row, "Player ID", "", 12, 71, 21, 205, 14, ObservatoryUi.Muted);
            var score = ui.Label(row, "Total score", "0", 16, 281, 0, 133, StandingRowHeight);
            score.alignment = TextAlignmentOptions.MidlineRight;
            score.enableAutoSizing = true;
            score.fontSizeMin = 12;
            score.fontSizeMax = 16;
            standingsRows.Add(new StandingRow { Root = row, Player = player, Identifier = identifier, Score = score, OwnerColor = ownerColor });
        }

        internal void ShowMatchups(GameRules rules, GameDataDocument document, string[] names)
        {
            if (matchups == null) BuildMatchups(rules, document, names);
            overview.gameObject.SetActive(false);
            matchups.gameObject.SetActive(true);
            first = matchupBack;
            Focus();
        }

        internal bool CloseMatchups()
        {
            if (matchups == null || !matchups.gameObject.activeSelf) return false;
            matchups.gameObject.SetActive(false);
            overview.gameObject.SetActive(true);
            first = matchupEntry;
            Focus();
            return true;
        }

        private void BuildMatchups(GameRules rules, GameDataDocument document, string[] names)
        {
            const int width = 1120;
            matchups = Center("Element matchups", width, 620);
            Header(matchups, "ELEMENT MATCHUPS", "원소 상성표", () => CloseMatchups());
            matchupBack = first;
            RectTransform graph = ObservatoryUi.Rect(matchups, "Matchup graph", 0, 106, width, 514);
            graph.gameObject.AddComponent<MatchupGraph>().Build(ui, rules, document, names);
            RectTransform table = ObservatoryUi.Rect(matchups, "Damage matrix", 0, 106, width, 514);
            BuildDamageMatrix(table, rules, names, width);
            var graphTab = ui.Button(matchups, "상성 그래프", 658, 31, 144, 42, null);
            var tableTab = ui.Button(matchups, "피해 배율표", 809, 31, 144, 42, null);
            void Show(bool showGraph)
            {
                graph.gameObject.SetActive(showGraph);
                table.gameObject.SetActive(!showGraph);
                graphTab.targetGraphic.color = showGraph ? ObservatoryUi.Surface : ObservatoryUi.Raised;
                tableTab.targetGraphic.color = showGraph ? ObservatoryUi.Raised : ObservatoryUi.Surface;
                graphTab.GetComponentInChildren<TMP_Text>().color = showGraph ? ObservatoryUi.Accent : ObservatoryUi.TextColor;
                tableTab.GetComponentInChildren<TMP_Text>().color = showGraph ? ObservatoryUi.TextColor : ObservatoryUi.Accent;
            }
            graphTab.onClick.AddListener(() => Show(true));
            tableTab.onClick.AddListener(() => Show(false));
            Show(true);
        }

        private void BuildDamageMatrix(RectTransform table, GameRules rules, string[] names, int width)
        {
            const int labelWidth = 138, tableTop = 46;
            ui.Label(table, "Matchup directions", "공격 원소(행) → 방어 원소(열)  ·  표의 값은 공격 피해 배율입니다.",
                17, 0, 0, width, 24, ObservatoryUi.Muted);
            ui.Label(table, "Matchup legend", "1보다 크면 유리, 작으면 불리합니다. ×1은 기본 피해입니다.",
                15, 0, 23, width, 22, ObservatoryUi.Muted);
            float cellWidth = (width - labelWidth) / (float)names.Length;
            float rowHeight = 460f / (names.Length + 1);
            ObservatoryUi.Panel(table, "Column background", 0, tableTop, width, rowHeight - 1, ObservatoryUi.Raised);
            ui.Label(table, "Axes", "공격 ↓  /  방어 →", 14, 9, tableTop, labelWidth - 9, rowHeight, ObservatoryUi.Gold);
            for (int index = 0; index < names.Length; index++)
                ui.Label(table, "Defender", names[index], 14, labelWidth + index * cellWidth, tableTop, cellWidth, rowHeight)
                    .alignment = TextAlignmentOptions.Center;
            for (int attacker = 0; attacker < names.Length; attacker++)
            {
                float y = tableTop + (attacker + 1) * rowHeight;
                ObservatoryUi.Panel(table, "Attacker row", 0, y, width, rowHeight - 1,
                    attacker % 2 == 0 ? ObservatoryUi.Surface : ObservatoryUi.Raised);
                ui.Label(table, "Attacker", names[attacker], 15, 9, y, labelWidth - 9, rowHeight);
                for (int defender = 0; defender < names.Length; defender++)
                {
                    float multiplier = rules.Matchup(attacker, defender);
                    Color color = multiplier > 1 ? ObservatoryUi.Accent : multiplier < 1 ? ObservatoryUi.Danger : ObservatoryUi.Muted;
                    ui.Label(table, "Damage multiplier", $"×{multiplier:0.##}", 15,
                        labelWidth + defender * cellWidth, y, cellWidth, rowHeight, color).alignment = TextAlignmentOptions.Center;
                }
            }
        }

        private RectTransform Center(string name, float width, float height)
        {
            Color surface = ObservatoryUi.Background;
            surface.a = 0.88f;
            RectTransform panel = ObservatoryUi.Panel(canvas, name, 0, 0, width, height, surface);
            ObservatoryUi.Place(panel, Vector2.one * 0.5f, Vector2.zero, new Vector2(width, height));
            return panel;
        }

        private void Header(RectTransform panel, string section, string title, Action back)
        {
            ui.Label(panel, "Section", section, 15, 0, 3, 610, 26, ObservatoryUi.Gold);
            ui.Label(panel, "Heading", title, 36, 0, 32, 610, 55);
            ObservatoryUi.Panel(panel, "Header divider", 0, 92, panel.sizeDelta.x, 1, ObservatoryUi.Gold);
            if (back != null) first = ui.Button(panel, "돌아가기", panel.sizeDelta.x - 160, 31, 160, 42, back);
        }
    }
}
