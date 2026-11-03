using System.Collections.Generic;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Sim;
using UnityEngine;

namespace ElementalLegion.Client.UI
{
    /// <summary>서버 월드와 별개로 이 PC의 음량·화면·가독성 설정을 보존한다.</summary>
    internal sealed class ClientPreferences
    {
        private const string Prefix = "ElementalLegion.";
        private const string NicknameKey = Prefix + "Nickname";
        internal static readonly int[] FrameLimits = { 60, 120, 144, -1 };
        internal readonly float[] Volume = { 0.7f, 0.7f, 0.5f };
        internal readonly bool[] Muted = new bool[3];
        internal DisplayOptions Display;
        internal string Nickname { get; private set; } = LoadNickname();

        internal static string LoadNickname()
        {
            string saved = PlayerPrefs.GetString(NicknameKey, PlayerNickname.Default);
            return PlayerNickname.IsValid(saved) ? saved : PlayerNickname.Default;
        }

        internal void SaveNickname(string nickname)
        {
            if (nickname == Nickname) return;
            Nickname = nickname;
            PlayerPrefs.SetString(NicknameKey, nickname);
            PlayerPrefs.Save();
        }

        internal struct DisplayOptions
        {
            internal Vector2Int Resolution;
            internal bool Fullscreen, VSync, ReduceMotion;
            internal int FrameLimit, UiPercent;
        }

        internal ClientPreferences()
        {
            for (int index = 0; index < Volume.Length; index++)
            {
                float value = PlayerPrefs.GetFloat(Prefix + "Volume" + index, Volume[index]);
                if (!float.IsNaN(value) && !float.IsInfinity(value)) Volume[index] = Mathf.Clamp01(value);
                Muted[index] = PlayerPrefs.GetInt(Prefix + "Muted" + index) == 1;
            }
            Vector2Int resolution = new Vector2Int(PlayerPrefs.GetInt(Prefix + "Width"), PlayerPrefs.GetInt(Prefix + "Height"));
            if (!Resolutions().Contains(resolution)) resolution = new Vector2Int(Screen.width, Screen.height);
            Display = new DisplayOptions
            {
                Resolution = resolution,
                Fullscreen = PlayerPrefs.GetInt(Prefix + "Fullscreen", Screen.fullScreen ? 1 : 0) == 1,
                VSync = PlayerPrefs.GetInt(Prefix + "VSync", QualitySettings.vSyncCount > 0 ? 1 : 0) == 1,
                FrameLimit = FrameLimits[Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "FrameLimit"), 0, FrameLimits.Length - 1)],
                UiPercent = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "UiPercent", 100), 90, 110),
                ReduceMotion = PlayerPrefs.GetInt(Prefix + "ReduceMotion") == 1
            };
        }

        internal static List<Vector2Int> Resolutions()
        {
            var result = new List<Vector2Int> { new Vector2Int(Screen.width, Screen.height) };
            foreach (Resolution mode in Screen.resolutions)
            {
                var size = new Vector2Int(mode.width, mode.height);
                if (mode.width >= 1024 && mode.height >= 576 && !result.Contains(size)) result.Add(size);
            }
            result.Sort((first, second) => first.x == second.x ? first.y.CompareTo(second.y) : first.x.CompareTo(second.x));
            return result;
        }

        internal static void ApplyDisplay(DisplayOptions value)
        {
            QualitySettings.vSyncCount = value.VSync ? 1 : 0;
            Application.targetFrameRate = value.VSync ? -1 : value.FrameLimit;
            FullScreenMode mode = value.Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            if (Screen.width != value.Resolution.x || Screen.height != value.Resolution.y || Screen.fullScreenMode != mode)
                Screen.SetResolution(value.Resolution.x, value.Resolution.y, mode);
        }

        internal void ApplyAudio(CombatAudio audio)
        {
            if (audio != null) audio.Volumes(Volume[0], Volume[1], Volume[2], Muted[0], Muted[1], Muted[2]);
        }

        internal void Save()
        {
            for (int index = 0; index < Volume.Length; index++)
            {
                PlayerPrefs.SetFloat(Prefix + "Volume" + index, Volume[index]);
                PlayerPrefs.SetInt(Prefix + "Muted" + index, Muted[index] ? 1 : 0);
            }
            PlayerPrefs.SetInt(Prefix + "Width", Display.Resolution.x);
            PlayerPrefs.SetInt(Prefix + "Height", Display.Resolution.y);
            PlayerPrefs.SetInt(Prefix + "Fullscreen", Display.Fullscreen ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "VSync", Display.VSync ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "FrameLimit", System.Array.IndexOf(FrameLimits, Display.FrameLimit));
            PlayerPrefs.SetInt(Prefix + "UiPercent", Display.UiPercent);
            PlayerPrefs.SetInt(Prefix + "ReduceMotion", Display.ReduceMotion ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
