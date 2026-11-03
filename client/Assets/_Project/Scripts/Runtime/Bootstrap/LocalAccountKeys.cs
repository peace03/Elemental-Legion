using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ElementalLegion.Client.Bootstrap
{
    internal static class LocalAccountKeys
    {
        private const string Prefix = "ElementalLegion.Account.v1.";

        internal static byte[] Load(string scope)
        {
            if (!PlayerPrefs.HasKey(Prefix + scope)) return null;
            string stored = PlayerPrefs.GetString(Prefix + scope);
            if (stored.Length == 0 || stored.Length > 2048) throw new CryptographicException();
            byte[] key = Protect(Convert.FromBase64String(stored), scope, false);
            if (key.Length != 32)
            {
                Array.Clear(key, 0, key.Length);
                throw new CryptographicException();
            }
            return key;
        }

        internal static void Save(string scope, byte[] key)
        {
            if (key.Length != 32) throw new CryptographicException();
            PlayerPrefs.SetString(Prefix + scope, Convert.ToBase64String(Protect(key, scope, true)));
            PlayerPrefs.Save();
        }

        private static byte[] Protect(byte[] data, string scope, bool encrypt)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            byte[] scopeBytes = Encoding.UTF8.GetBytes(Prefix + scope);
            GCHandle input = GCHandle.Alloc(data, GCHandleType.Pinned);
            GCHandle entropy = default;
            Blob result = default;
            try
            {
                entropy = GCHandle.Alloc(scopeBytes, GCHandleType.Pinned);
                var source = new Blob { Size = data.Length, Data = input.AddrOfPinnedObject() };
                var context = new Blob { Size = scopeBytes.Length, Data = entropy.AddrOfPinnedObject() };
                // 사용자 프로필 범위에 저장하며 운영체제의 자격 증명 대화 상자를 열지 않는다.
                const uint uiForbidden = 1;
                bool ok = encrypt
                    ? CryptProtectData(ref source, null, ref context, IntPtr.Zero, IntPtr.Zero, uiForbidden, out result)
                    : CryptUnprotectData(ref source, IntPtr.Zero, ref context, IntPtr.Zero, IntPtr.Zero, uiForbidden, out result);
                if (!ok || result.Size <= 0 || result.Size > 1536) throw new CryptographicException();
                byte[] bytes = new byte[result.Size];
                Marshal.Copy(result.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (result.Data != IntPtr.Zero)
                {
                    if (!encrypt)
                        for (int index = 0; index < result.Size; index++) Marshal.WriteByte(result.Data, index, 0);
                    LocalFree(result.Data);
                }
                if (entropy.IsAllocated) entropy.Free();
                input.Free();
            }
#else
            throw new PlatformNotSupportedException("계정 복귀 키는 Windows에서만 사용할 수 있습니다.");
#endif
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            internal int Size;
            internal IntPtr Data;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

        [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
