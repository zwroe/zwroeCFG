using System;
using System.Windows.Input;

namespace Rebind.Helpers
{
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

        public static byte GetScanCode(string keyString, byte fallback)
        {
            int vk = GetVirtualKeyCode(keyString);
            if (vk <= 0) return fallback;
            uint scan = MapVirtualKey((uint)vk, 0); // MAPVK_VK_TO_VSC
            return scan > 0 ? (byte)scan : fallback;
        }
    }
}
