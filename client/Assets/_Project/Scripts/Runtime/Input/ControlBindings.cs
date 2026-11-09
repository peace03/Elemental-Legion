using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ElementalLegion.Client.Input
{
    /// <summary>조작에 사용하는 키를 검증하고 로컬에 보존한다.</summary>
    public sealed class ControlBindings
    {
        private const string Preference = "ElementalLegion.Controls";
        private static readonly Key[] Defaults =
        {
            Key.W, Key.S, Key.A, Key.D, Key.UpArrow, Key.DownArrow,
            Key.LeftArrow, Key.RightArrow, Key.G, Key.Escape, Key.Tab,
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.B, Key.R
        };
        private readonly Key[] keys = (Key[])Defaults.Clone();
        public const int Rally = 8, Menu = 9, Dashboard = 10;
        public const int Squad1 = 11, Squad2 = 12, Squad3 = 13, Squad4 = 14,
            AllField = 15, BaseFocus = 16, Recall = 17, Count = 18;
        internal int Revision { get; private set; }

        public Key this[int slot] => keys[slot];
        public bool Pressed(int slot) => Keyboard.current != null && Keyboard.current[keys[slot]].wasPressedThisFrame;
        internal bool Held(int slot)
        {
            if (Keyboard.current == null) return false;
            var key = Keyboard.current[keys[slot]];
            return key.isPressed && !key.wasReleasedThisFrame;
        }

        public static ControlBindings Load()
        {
            var result = new ControlBindings();
            result.Restore(PlayerPrefs.GetString(Preference));
            return result;
        }

        public bool TrySet(int slot, Key key, out int conflict)
        {
            conflict = -1;
            if (slot < 0 || slot >= Count || !Valid(key)) return false;
            conflict = Array.IndexOf(keys, key);
            if (conflict >= 0 && conflict != slot) return false;
            keys[slot] = key;
            Revision++;
            return true;
        }

        public void Reset()
        {
            Array.Copy(Defaults, keys, Count);
            Revision++;
        }

        public bool Restore(string serialized)
        {
            string[] values = serialized?.Split(',');
            if (values == null || (values.Length != Count && values.Length != Dashboard && values.Length != Squad1)) return false;
            var restored = new Key[Count];
            for (int index = 0; index < values.Length; index++)
            {
                if (!Enum.TryParse(values[index], out Key key) || !Valid(key) ||
                    Array.IndexOf(restored, key, 0, index) >= 0) return false;
                restored[index] = key;
            }
            for (int index = values.Length; index < Count; index++)
            {
                // 기존 사용자 키를 보존하고 충돌하는 새 조작에는 비어 있는 기능 키를 배정한다.
                Key key = Defaults[index];
                if (Array.IndexOf(restored, key, 0, index) >= 0)
                {
                    key = Key.F1;
                    while (Array.IndexOf(restored, key, 0, index) >= 0)
                        key = key == Key.F12 ? Key.F13 : key + 1;
                }
                restored[index] = key;
            }
            Array.Copy(restored, keys, Count);
            Revision++;
            return true;
        }

        public void Save()
        {
            PlayerPrefs.SetString(Preference, string.Join(",", keys));
            PlayerPrefs.Save();
        }

        public string Label(int slot) => Keyboard.current?[keys[slot]].displayName ?? keys[slot].ToString();

        internal void Apply(InputAction move)
        {
            move.Disable();
            for (int index = 0; index < move.bindings.Count; index++)
            {
                string path = move.bindings[index].path;
                for (int slot = 0; slot < Rally; slot++)
                {
                    if (!string.Equals(path, "<Keyboard>/" + Defaults[slot], StringComparison.OrdinalIgnoreCase)) continue;
                    string control = Keyboard.current?[keys[slot]].name ?? keys[slot].ToString();
                    move.ApplyBindingOverride(index, "<Keyboard>/" + control);
                    break;
                }
            }
            move.Enable();
        }

        private static bool Valid(Key key) => key != Key.None && key != Key.Enter && Enum.IsDefined(typeof(Key), key) &&
            (key <= Key.OEM5 || key >= Key.F13) && key != Key.LeftMeta && key != Key.RightMeta;
    }
}
