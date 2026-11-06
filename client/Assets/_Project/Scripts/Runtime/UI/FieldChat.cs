using System;
using System.Collections.Generic;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Net;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    internal sealed class FieldChat
    {
        private const int HistoryLimit = 40;
        private readonly List<ChatMessage> messages = new List<ChatMessage>(HistoryLimit);
        private readonly HashSet<int> blocked = new HashSet<int>();
        private readonly Row[] rows = new Row[4];
        private readonly RectTransform canvas, log, composer, bubble;
        private readonly TMP_Text bubbleText, hint, modeText;
        private readonly TMP_InputField field;
        private readonly Button older, newer, unblock;
        private readonly NetworkSettings settings;
        private readonly float visibleSeconds;
        private ClientConnection connection;
        private ChatMessage floating;
        private float bubbleUntil, feedUntil, reportNoticeUntil;
        private int offset, enterFrame = -1;
        private ulong report;
        private bool global;
        internal bool Composing => composer.gameObject.activeSelf;

        internal FieldChat(Transform parent, ObservatoryUi ui, NetworkSettings settings, float visibleSeconds, float backgroundAlpha)
        {
            this.settings = settings;
            this.visibleSeconds = visibleSeconds;
            canvas = ObservatoryUi.Canvas(parent, "Field chat", 32);
            var frame = ObservatoryUi.Rect(canvas, "Chat", 0, 0, 450, 240);
            ObservatoryUi.Place(frame, Vector2.up, new Vector2(16, -78), new Vector2(450, 240));
            Color background = ObservatoryUi.Background;
            background.a = backgroundAlpha;
            Color surface = ObservatoryUi.Surface;
            surface.a = backgroundAlpha;
            hint = ui.Label(frame, "Chat hint", "", 12, 8, 218, 434, 22, ObservatoryUi.Muted);
            log = ObservatoryUi.Panel(frame, "Chat log", 0, 42, 450, 129, background);
            for (int index = 0; index < rows.Length; index++)
            {
                var row = new Row();
                int y = 5 + 30 * index;
                row.Text = ui.Label(log, "Chat message", "", 13, 9, y, 291, 27);
                row.Text.richText = row.Text.parseCtrlCharacters = false;
                row.Text.overflowMode = TextOverflowModes.Ellipsis;
                row.Block = ui.Button(log, "차단", 308, y, 61, 27, () => Block(row.Message.OwnerId));
                row.Report = ui.Button(log, "신고", 377, y, 61, 27, () => StartReport(row.Message.Id));
                row.Block.GetComponentInChildren<TMP_Text>().fontSize = row.Report.GetComponentInChildren<TMP_Text>().fontSize = 12;
                rows[index] = row;
            }
            composer = ObservatoryUi.Panel(frame, "Chat input", 0, 177, 450, 40, surface, true);
            modeText = ui.Label(composer, "Chat mode", "근접", 16, 8, 6, 80, 42, ObservatoryUi.Accent);
            field = ui.Input(composer, "", 85, 0, 357);
            field.GetComponent<UnityEngine.UI.Image>().color = background;
            field.richText = false;
            field.textComponent.richText = field.textComponent.parseCtrlCharacters = false;
            field.onSubmit.AddListener(_ => Submit());
            older = ui.Button(frame, "이전 대화", 0, 12, 115, 32, () => Browse(rows.Length));
            newer = ui.Button(frame, "다음 대화", 121, 12, 115, 32, () => Browse(-rows.Length));
            unblock = ui.Button(frame, "차단 해제", 243, 12, 115, 32, () => { blocked.Clear(); Render(); Focus(); });
            bubble = ObservatoryUi.Panel(canvas, "Proximity chat bubble", 0, 0, 260, 60, background);
            ObservatoryUi.Place(bubble, Vector2.one * 0.5f, Vector2.zero, new Vector2(260, 60));
            bubbleText = ui.Label(bubble, "Bubble text", "", 15, 8, 3, 244, 54);
            bubbleText.richText = bubbleText.parseCtrlCharacters = false;
            bubbleText.overflowMode = TextOverflowModes.Ellipsis;
            bubbleText.alignment = TextAlignmentOptions.Center;
            Reset();
        }

        internal void Attach(ClientConnection transport)
        {
            Reset();
            connection = transport;
            connection.ChatReceived += Receive;
            canvas.gameObject.SetActive(true);
        }

        internal void Reset()
        {
            if (connection != null) connection.ChatReceived -= Receive;
            connection = null;
            messages.Clear();
            blocked.Clear();
            bubbleUntil = feedUntil = reportNoticeUntil = 0;
            enterFrame = -1;
            global = false;
            Close();
            canvas.gameObject.SetActive(false);
        }

        internal void Scale(int percent) => ObservatoryUi.SetScale(canvas.GetComponent<CanvasScaler>(), percent);

        internal bool HandleInput(bool allowed)
        {
            RefreshVisibility();
            if (connection?.State != ConnectionState.Connected || !allowed)
            {
                if (Composing) Close();
                return false;
            }
            Keyboard keys = Keyboard.current;
            if (keys == null) return Composing;
            if (Composing && keys.escapeKey.wasPressedThisFrame) { Close(); return true; }
            if (Composing && report == 0 && keys.tabKey.wasPressedThisFrame)
            {
                global = !global;
                Render();
                return true;
            }
            if (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame)
            {
                if (enterFrame == Time.frameCount) return true;
                if (Composing)
                {
                    if (!field.isFocused) Submit();
                }
                else
                {
                    composer.gameObject.SetActive(true);
                    enterFrame = Time.frameCount;
                    hint.text = "Tab 근접/전체 · Enter 전송/닫기 · Esc 취소";
                    Render();
                    Focus();
                }
                return true;
            }
            return Composing;
        }

        internal void DrawBubble(UnityEngine.Camera view, SnapshotTimeline timeline, double renderTick)
        {
            bool show = false;
            if (view != null && floating.AnchorId > 0 && Time.unscaledTime < bubbleUntil &&
                timeline.TrySample(floating.AnchorId, renderTick, out var position))
            {
                Vector3 projected = view.WorldToScreenPoint(new Vector3(position.X, 2, position.Y));
                show = projected.z > 0 && projected.x >= 0 && projected.y >= 0 && projected.x <= Screen.width && projected.y <= Screen.height;
                if (show && RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, projected, null, out Vector2 point))
                    bubble.anchoredPosition = point + Vector2.up * 30;
            }
            if (bubble.gameObject.activeSelf != show) bubble.gameObject.SetActive(show);
        }

        private void Receive(ChatMessage message)
        {
            if (blocked.Contains(message.OwnerId)) return;
            if (messages.Count == HistoryLimit) messages.RemoveAt(0);
            messages.Add(message);
            feedUntil = Time.unscaledTime + visibleSeconds;
            if (offset > 0) offset = Math.Min(offset + 1, Math.Max(0, messages.Count - rows.Length));
            if (message.AnchorId > 0)
            {
                floating = message;
                bubbleText.text = message.Text;
                bubbleUntil = feedUntil;
            }
            Render();
        }

        private void Submit()
        {
            if (!Composing || enterFrame == Time.frameCount || connection?.State != ConnectionState.Connected) return;
            enterFrame = Time.frameCount;
            if (connection.IsMigrating) { hint.text = "연결 복구 후 전송할 수 있습니다."; return; }
            string text = field.text.Trim();
            if (text.Length == 0) { Close(); return; }
            if (!M5Protocol.IsSafeText(text, report == 0 ? settings.ChatMaxLength : M5Protocol.ReportLimit))
            { hint.text = "메시지 길이와 입력 문자를 확인하세요."; Focus(); return; }
            bool reporting = report != 0;
            if (reporting) connection.ReportChat(report, text);
            else connection.SendChat(global, text);
            Close();
            if (reporting)
            {
                hint.text = "신고 요청을 보냈습니다.";
                reportNoticeUntil = Time.unscaledTime + visibleSeconds;
                RefreshVisibility();
            }
        }

        private void StartReport(ulong id)
        {
            report = id;
            field.SetTextWithoutNotify("");
            hint.text = "신고 사유 입력 · Enter 제출 · Esc 취소";
            Render();
            Focus();
        }

        private void Block(int owner)
        {
            blocked.Add(owner);
            messages.RemoveAll(message => message.OwnerId == owner);
            if (floating.OwnerId == owner) bubbleUntil = 0;
            Browse(0);
        }

        private void Browse(int delta)
        {
            offset = Mathf.Clamp(offset + delta, 0, Math.Max(0, messages.Count - rows.Length));
            Render();
            Focus();
        }

        private void Close()
        {
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == field.gameObject)
                EventSystem.current.SetSelectedGameObject(null);
            field.DeactivateInputField();
            field.SetTextWithoutNotify("");
            composer.gameObject.SetActive(false);
            report = 0;
            offset = 0;
            feedUntil = reportNoticeUntil = 0;
            Render();
        }

        private void Focus()
        {
            EventSystem.current?.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
        }

        private void Render()
        {
            RefreshVisibility();
            older.gameObject.SetActive(Composing && messages.Count - offset > rows.Length);
            newer.gameObject.SetActive(Composing && offset > 0);
            unblock.gameObject.SetActive(Composing && blocked.Count > 0);
            modeText.text = report != 0 ? "신고" : global ? "전체" : "근접";
            field.characterLimit = report == 0 ? settings.ChatMaxLength : M5Protocol.ReportLimit;
            ((TMP_Text)field.placeholder).text = report == 0 ? "메시지 입력" : "신고 사유";
            int end = messages.Count - offset;
            int start = Math.Max(0, end - rows.Length);
            for (int index = 0; index < rows.Length; index++)
            {
                Row row = rows[index];
                bool present = start + index < end;
                row.Text.gameObject.SetActive(present);
                bool actions = present && Composing && messages[start + index].OwnerId != connection?.PlayerId;
                row.Block.gameObject.SetActive(actions);
                row.Report.gameObject.SetActive(actions && connection?.CanReportChat == true);
                if (!present) continue;
                row.Message = messages[start + index];
                row.Text.text = $"[{(row.Message.Global ? "전체" : "근접")}] {row.Message.Nickname}  {row.Message.Text}";
            }
        }

        private void RefreshVisibility()
        {
            float now = Time.unscaledTime;
            bool showLog = Composing || messages.Count > 0 && now < feedUntil;
            if (log.gameObject.activeSelf != showLog) log.gameObject.SetActive(showLog);
            bool showHint = Composing || now < reportNoticeUntil;
            if (hint.gameObject.activeSelf != showHint) hint.gameObject.SetActive(showHint);
        }

        private sealed class Row
        {
            internal TMP_Text Text;
            internal Button Block, Report;
            internal ChatMessage Message;
        }
    }
}
