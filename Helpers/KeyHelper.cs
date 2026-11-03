using System;
using System.Windows.Input;

namespace Rebind.Helpers
{
    public readonly record struct ScanCode(byte Code, bool IsExtended);

    public static class KeyHelper
    {
        public const int VK_MBUTTON  = 0x04;
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;

        public static int GetVirtualKeyCode(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString))
                return -1;

            if (keyString.Equals("Mouse3", StringComparison.OrdinalIgnoreCase) ||
                keyString.Equals("MiddleMouse", StringComparison.OrdinalIgnoreCase) ||
                keyString.Equals("MButton", StringComparison.OrdinalIgnoreCase))
                return VK_MBUTTON;

            if (keyString.Equals("Mouse4", StringComparison.OrdinalIgnoreCase) ||
                keyString.Equals("XButton1", StringComparison.OrdinalIgnoreCase))
                return VK_XBUTTON1;

            if (keyString.Equals("Mouse5", StringComparison.OrdinalIgnoreCase) ||
                keyString.Equals("XButton2", StringComparison.OrdinalIgnoreCase))
                return VK_XBUTTON2;

            string normalizedKey = NormalizeKeyString(keyString);

            if (Enum.TryParse<Key>(normalizedKey, true, out Key key))
            {
                return KeyInterop.VirtualKeyFromKey(key);
            }

            if (normalizedKey.Equals("Esc", StringComparison.OrdinalIgnoreCase))
                return KeyInterop.VirtualKeyFromKey(Key.Escape);

            if (normalizedKey.Equals("Spacebar", StringComparison.OrdinalIgnoreCase))
                return KeyInterop.VirtualKeyFromKey(Key.Space);

            return -1;
        }

        public static string GetConfigKeyName(Key key)
        {
            return key switch
            {
                Key.Space => "Space",
                _ => key.ToString()
            };
        }

        public static string? GetMouseButtonName(int vkCode)
        {
            return vkCode switch
            {
                VK_MBUTTON  => "Mouse3",
                VK_XBUTTON1 => "Mouse4",
                VK_XBUTTON2 => "Mouse5",
                _ => null
            };
        }

        internal static string GetInputName(int vkCode)
        {
            Key key = KeyInterop.KeyFromVirtualKey(vkCode);
            return GetMouseButtonName(vkCode) ?? (key == Key.None ? $"VK 0x{vkCode:X2}" : GetConfigKeyName(key));
        }

        internal static string GetScanCodeName(ScanCode scanCode)
        {
            uint code = scanCode.Code | (scanCode.IsExtended ? 0xE000u : 0u);
            uint vk = MapVirtualKey(code, 3); // MAPVK_VSC_TO_VK_EX
            return vk == 0 ? $"Scan 0x{code:X2}" : GetInputName((int)vk);
        }

        private static string NormalizeKeyString(string keyString)
        {
            string trimmed = keyString.Trim();

            if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
                return $"D{trimmed}";

            string compact = trimmed.Replace(" ", string.Empty);
            if (compact.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = compact.Substring("Numpad".Length);
                if (suffix.Length == 1 && char.IsDigit(suffix[0]))
                    return $"NumPad{suffix}";
            }

            return trimmed;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public static ScanCode GetScanCode(string keyString, ScanCode fallback)
        {
            int vk = GetVirtualKeyCode(keyString);
            if (vk <= 0) return fallback;

            // MAPVK_VK_TO_VSC_EX returns an E0/E1 prefix in the high byte for
            // keys that need the extended-key flag when injected.
            uint mapped = MapVirtualKey((uint)vk, 4);
            if (mapped == 0) return fallback;

            byte prefix = (byte)((mapped >> 8) & 0xFF);
            return new ScanCode((byte)(mapped & 0xFF), prefix == 0xE0);
        }
    }
}
