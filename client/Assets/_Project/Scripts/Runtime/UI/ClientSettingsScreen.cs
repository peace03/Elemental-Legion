using System;
using System.Collections.Generic;
using ElementalLegion.Client.Input;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>공용 설정 화면의 키 입력 대기와 해상도 확인을 소유한다.</summary>
    internal sealed class ClientSettingsScreen : MonoBehaviour
    {
        private static readonly string[] KeyNames =
        {
            "위로 이동", "아래로 이동", "왼쪽 이동", "오른쪽 이동",
            "보조 위", "보조 아래", "보조 왼쪽", "보조 오른쪽", "수비대 집결", "게임 메뉴", "현황 (누르고 있기)",
            "분대 1 선택·이동", "분대 2 선택·이동", "분대 3 선택·이동", "분대 4 선택·이동",
            "야전 병력 전체 선택", "본진으로 화면 이동", "선택 병력 귀환"
        };
        private ObservatoryUi ui;
        private ClientPreferences preferences;
        private ControlBindings controls;
        private Action applyPreferences, close;
        private RectTransform canvas, page, display, bindings, confirmation;
        private RectTransform audioPage, videoPage, accessibilityPage;
        private readonly UnityEngine.UI.Button[] tabs = new UnityEngine.UI.Button[4];
        private UnityEngine.UI.Button displayApply;
        private CanvasGroup pageGroup;
        private TMP_Text status, keyStatus, displayTimer;
        private TMP_Dropdown resolution, mode, vsync, limit, scale, motion;
        private List<Vector2Int> sizes;
        private readonly TMP_Text[] keyLabels = new TMP_Text[ControlBindings.Count];
        private int capturing = -1, capturedAt;
        private bool savedNavigation;
        private EventSystem capturedEventSystem;
        private double confirmUntil;
        private ClientPreferences.DisplayOptions candidate;
        internal bool Capturing => capturing >= 0 || capturedAt == Time.frameCount;

        internal void Build(ObservatoryUi skin, ClientPreferences state, ControlBindings bindingsModel,
            Action apply, Action back, Action<int> previewAudio)
        {
            ui = skin;
            preferences = state;
            controls = bindingsModel;
            applyPreferences = apply;
            close = back;
            canvas = ObservatoryUi.Canvas(transform, "Settings canvas", 60);
            ui.Backdrop(canvas, true);
            page = ObservatoryUi.Rect(canvas, "Preferences", 0, 0, 1120, 620);
            ObservatoryUi.Place(page, Vector2.one * 0.5f, Vector2.zero, new Vector2(1120, 620));
            pageGroup = page.gameObject.AddComponent<CanvasGroup>();
            ui.Label(page, "Section", "PREFERENCES", 15, 0, 0, 500, 24, ObservatoryUi.Gold);
            ui.Label(page, "Heading", "설정", 37, 0, 24, 580, 54);
            var backButton = ui.Button(page, "돌아가기", 959, 28, 161, 43, Back);
            ObservatoryUi.Panel(page, "Header divider", 0, 91, 1120, 1, ObservatoryUi.Gold);
            string[] sections = { "음향", "화면", "표현", "조작 키" };
            for (int index = 0; index < sections.Length; index++)
            {
                int selected = index;
                tabs[index] = ui.Button(page, sections[index], 0, 112 + index * 64, 174, 51, () => SwitchTab(selected));
            }
            BuildDisplay(previewAudio);
            BuildBindings();
            SyncDisplay();
            SwitchTab(0);
            Scale(preferences.Display.UiPercent);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(backButton.gameObject);
        }

        internal void Scale(int percent) => ObservatoryUi.SetScale(canvas.GetComponent<CanvasScaler>(), percent);

        private void BuildDisplay(Action<int> previewAudio)
        {
            display = ObservatoryUi.Rect(page, "Audio and display", 202, 112, 918, 508);
            RectTransform audio = audioPage = ObservatoryUi.Panel(display, "Audio", 0, 0, 918, 430, ObservatoryUi.Surface);
            ui.Label(audio, "Audio title", "음향", 25, 26, 18, 840, 37, ObservatoryUi.Gold);
            string[] labels = { "전체", "효과음", "UI" };
            for (int index = 0; index < labels.Length; index++)
            {
                int channel = index;
                int y = 78 + index * 113;
                var level = ui.Label(audio, "Volume label", "", 18, 26, y, 560, 27);
                void ShowLevel(float value) => level.text = $"{labels[channel]} 음량  {value:P0}";
                ShowLevel(preferences.Volume[channel]);
                var slider = ui.Slider(audio, preferences.Volume[channel], 26, y + 40, 600);
                slider.onValueChanged.AddListener(value =>
                {
                    preferences.Volume[channel] = value;
                    ShowLevel(value);
                    applyPreferences();
                    preferences.Save();
                });
                var mute = ui.Button(audio, preferences.Muted[channel] ? "음소거 해제" : "음소거", 660, y + 31, 230, 39, null);
                var muteLabel = mute.GetComponentInChildren<TMP_Text>();
                mute.onClick.AddListener(() =>
                {
                    preferences.Muted[channel] = !preferences.Muted[channel];
                    muteLabel.text = preferences.Muted[channel] ? "음소거 해제" : "음소거";
                    applyPreferences();
                    preferences.Save();
                });
                ui.Button(audio, "미리 듣기", 744, y - 4, 146, 30, () => previewAudio(channel));
            }

            RectTransform video = videoPage = ObservatoryUi.Panel(display, "Display", 0, 0, 918, 430, ObservatoryUi.Surface);
            ui.Label(video, "Video title", "화면", 25, 26, 18, 840, 37, ObservatoryUi.Gold);
            sizes = ClientPreferences.Resolutions();
            var options = sizes.ConvertAll(size => $"{size.x} × {size.y}");
            resolution = Setting(video, "해상도", options.ToArray(), 85);
            mode = Setting(video, "화면 모드", new[] { "창 모드", "테두리 없는 전체 화면" }, 149);
            vsync = Setting(video, "수직 동기화", new[] { "끄기", "켜기" }, 213);
            limit = Setting(video, "프레임 제한", new[] { "60 FPS", "120 FPS", "144 FPS", "제한 없음" }, 277);
            accessibilityPage = ObservatoryUi.Panel(display, "Accessibility", 0, 0, 918, 430, ObservatoryUi.Surface);
            ui.Label(accessibilityPage, "Accessibility title", "표현과 가독성", 25, 26, 18, 840, 37, ObservatoryUi.Gold);
            scale = Setting(accessibilityPage, "UI 크기", new[] { "90%", "95%", "100%", "105%", "110%" }, 85);
            motion = Setting(accessibilityPage, "UI 동작 줄이기", new[] { "끄기", "켜기" }, 149);
            ui.Label(accessibilityPage, "Accessibility hint", "원소와 소속은 색뿐 아니라 아이콘·형태·이름으로도 구분합니다.",
                17, 26, 232, 864, 68, ObservatoryUi.Muted);
            vsync.onValueChanged.AddListener(value =>
            {
                limit.interactable = value == 0;
                status.text = value == 0 ? "화면 설정을 적용할 수 있습니다." : "수직 동기화가 켜져 프레임 제한을 비활성화했습니다.";
            });
            status = ui.Label(display, "Display status", "", 15, 4, 447, 690, 48, ObservatoryUi.Muted);
            displayApply = ui.Button(display, "설정 적용", 718, 453, 200, 43, Apply, true);
        }

        private TMP_Dropdown Setting(RectTransform parent, string label, string[] options, int y)
        {
            ui.Label(parent, label, label, 18, 26, y, 306, 42, ObservatoryUi.Muted);
            return ui.Dropdown(parent, options, 0, 354, y, 536);
        }

        private void BuildBindings()
        {
            bindings = ObservatoryUi.Panel(page, "Keyboard bindings", 202, 112, 918, 508, ObservatoryUi.Surface);
            ui.Label(bindings, "Primary keys", "이동 기본 키", 21, 22, 13, 410, 34, ObservatoryUi.Gold);
            ui.Label(bindings, "Secondary keys", "이동 보조 키", 21, 467, 13, 410, 34, ObservatoryUi.Gold);
            for (int index = 0; index < ControlBindings.Count; index++)
            {
                int slot = index;
                int column = index < 8 ? index / 4 : (index - 8) % 2;
                int row = index < 8 ? index % 4 : 4 + (index - 8) / 2;
                int x = 22 + column * 445;
                int y = 48 + row * 38;
                ui.Label(bindings, KeyNames[index], KeyNames[index], 17, x, y, 193, 35);
                keyLabels[index] = ui.Button(bindings, "", x + 195, y, 218, 35,
                    () => Capture(slot)).GetComponentInChildren<TMP_Text>();
            }
            keyStatus = ui.Label(bindings, "Keyboard status", "", 16, 22, 397, 874, 35, ObservatoryUi.Gold);
            ui.Button(bindings, "입력 취소", 22, 443, 154, 42, EndCapture);
            ui.Button(bindings, "기본값 복원", 185, 443, 173, 42, () =>
            {
                EndCapture();
                controls.Reset();
                SaveControls("기본 조작 키를 복원했습니다.");
            });
        }

        private void SwitchTab(int selected)
        {
            EndCapture();
            display.gameObject.SetActive(selected < 3);
            bindings.gameObject.SetActive(selected == 3);
            audioPage.gameObject.SetActive(selected == 0);
            videoPage.gameObject.SetActive(selected == 1);
            accessibilityPage.gameObject.SetActive(selected == 2);
            displayApply.gameObject.SetActive(selected == 1 || selected == 2);
            status.text = selected == 0 ? "음량과 음소거는 즉시 적용하고 저장합니다." :
                vsync.value == 1 ? "수직 동기화가 켜져 프레임 제한을 비활성화했습니다." : "변경한 설정은 적용 버튼으로 저장합니다.";
            for (int index = 0; index < tabs.Length; index++) ui.StyleButton(tabs[index], index == selected);
            RefreshKeys();
        }

        private void Capture(int slot)
        {
            EndCapture();
            capturing = slot;
            capturedAt = Time.frameCount;
            capturedEventSystem = EventSystem.current;
            if (capturedEventSystem != null)
            {
                savedNavigation = capturedEventSystem.sendNavigationEvents;
                capturedEventSystem.sendNavigationEvents = false;
            }
            RefreshKeys();
            keyStatus.text = $"{KeyNames[slot]}에 사용할 키를 누르세요.";
        }

        private void EndCapture()
        {
            if (capturing < 0) return;
            capturing = -1;
            capturedAt = Time.frameCount;
            if (capturedEventSystem != null) capturedEventSystem.sendNavigationEvents = savedNavigation;
            capturedEventSystem = null;
            RefreshKeys();
        }

        private void RefreshKeys()
        {
            for (int index = 0; index < keyLabels.Length; index++)
                if (keyLabels[index] != null) keyLabels[index].text = index == capturing ? "키 입력 대기…" : controls.Label(index);
        }

        private void SaveControls(string message)
        {
            controls.Save();
            applyPreferences();
            RefreshKeys();
            keyStatus.text = message;
        }

        private void Update()
        {
            if (confirmation != null)
            {
                int seconds = Mathf.CeilToInt((float)(confirmUntil - Time.unscaledTimeAsDouble));
                if (seconds <= 0) Revert();
                else displayTimer.SetText("{0}초 후 이전 화면 설정으로 돌아갑니다.", seconds);
            }
            if (capturing < 0 || capturedAt == Time.frameCount || !Application.isFocused || Keyboard.current == null) return;
            foreach (var key in Keyboard.current.allKeys)
            {
                if (!key.wasPressedThisFrame) continue;
                if (!controls.TrySet(capturing, key.keyCode, out int conflict))
                {
                    if (conflict >= 0) keyStatus.text = $"{KeyNames[conflict]}에서 쓰는 키입니다.";
                    else keyStatus.text = key.keyCode == Key.Enter ? "Enter는 메뉴의 선택에 사용하는 키입니다." : "다른 키를 선택하세요.";
                    return;
                }
                EndCapture();
                SaveControls("조작 키를 저장했습니다.");
                return;
            }
        }

        private void SyncDisplay()
        {
            var value = preferences.Display;
            resolution.SetValueWithoutNotify(Mathf.Max(0, sizes.IndexOf(value.Resolution)));
            mode.SetValueWithoutNotify(value.Fullscreen ? 1 : 0);
            vsync.SetValueWithoutNotify(value.VSync ? 1 : 0);
            limit.SetValueWithoutNotify(Mathf.Max(0, Array.IndexOf(ClientPreferences.FrameLimits, value.FrameLimit)));
            limit.interactable = !value.VSync;
            scale.SetValueWithoutNotify((value.UiPercent - 90) / 5);
            motion.SetValueWithoutNotify(value.ReduceMotion ? 1 : 0);
            status.text = value.VSync ? "수직 동기화가 켜져 프레임 제한을 비활성화했습니다." : "현재 화면 설정";
        }

        private void Apply()
        {
            candidate = new ClientPreferences.DisplayOptions
            {
                Resolution = sizes[resolution.value], Fullscreen = mode.value == 1, VSync = vsync.value == 1,
                FrameLimit = ClientPreferences.FrameLimits[limit.value], UiPercent = 90 + 5 * scale.value,
                ReduceMotion = motion.value == 1
            };
            ClientPreferences.ApplyDisplay(candidate);
            if (candidate.Resolution == preferences.Display.Resolution && candidate.Fullscreen == preferences.Display.Fullscreen)
            {
                Commit();
                return;
            }
            confirmation = ObservatoryUi.Panel(canvas, "Display confirmation", 0, 0, 0, 0,
                new Color(0.02f, 0.035f, 0.045f, 0.9f), true);
            ObservatoryUi.Fill(confirmation);
            pageGroup.interactable = false;
            RectTransform dialog = ObservatoryUi.Panel(confirmation, "Display check", 0, 0, 480, 240, ObservatoryUi.Surface);
            ObservatoryUi.Place(dialog, Vector2.one * 0.5f, Vector2.zero, new Vector2(480, 240));
            ui.Label(dialog, "Title", "이 화면 설정을 유지할까요?", 27, 26, 19, 430, 54);
            displayTimer = ui.Label(dialog, "Timer", "15초 후 이전 화면 설정으로 돌아갑니다.", 17, 26, 85, 428, 36, ObservatoryUi.Muted);
            var keep = ui.Button(dialog, "유지하기", 26, 150, 201, 47, Commit, true);
            ui.Button(dialog, "되돌리기", 246, 150, 208, 47, Revert);
            confirmUntil = Time.unscaledTimeAsDouble + 15;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(keep.gameObject);
        }

        private void CloseConfirmation()
        {
            if (confirmation == null) return;
            confirmation.gameObject.SetActive(false);
            Destroy(confirmation.gameObject);
            confirmation = null;
            pageGroup.interactable = true;
        }

        private void Commit()
        {
            CloseConfirmation();
            preferences.Display = candidate;
            preferences.Save();
            applyPreferences();
            status.text = "화면 설정을 저장했습니다.";
        }

        private void Revert()
        {
            ClientPreferences.ApplyDisplay(preferences.Display);
            CloseConfirmation();
            SyncDisplay();
            status.text = "이전 화면 설정으로 돌아갔습니다.";
        }

        internal void Back()
        {
            if (capturing >= 0) EndCapture();
            else if (confirmation != null) Revert();
            else close();
        }

        private void OnDisable()
        {
            EndCapture();
            if (confirmation != null) ClientPreferences.ApplyDisplay(preferences.Display);
        }
    }
}
