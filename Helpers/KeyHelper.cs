using System;
using System.Windows.Input;

namespace Rebind.Helpers
{
    /// <summary>
    /// Utility class for translating string key names into Windows virtual key codes.
    /// Supports keyboard keys as well as Mouse3 (middle), Mouse4 (XButton1), Mouse5 (XButton2).
    /// </summary>
    public static class KeyHelper
    {
        // Mouse button virtual key codes
        public const int VK_MBUTTON  = 0x04;
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;

        /// <summary>
        /// Converts a string key name (e.g., "Space", "W", "Mouse4")
        /// into its corresponding Windows Virtual Key Code integer.
        /// Returns -1 if the conversion fails.
        /// </summary>
        public static int GetVirtualKeyCode(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString))
                return -1;

            // Mouse button aliases
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

            // Handle some common aliases if Enum.TryParse fails
            if (normalizedKey.Equals("Esc", StringComparison.OrdinalIgnoreCase))
                return KeyInterop.VirtualKeyFromKey(Key.Escape);

            if (normalizedKey.Equals("Spacebar", StringComparison.OrdinalIgnoreCase))
                return KeyInterop.VirtualKeyFromKey(Key.Space);

            return -1;
        }

        /// <summary>
        /// Converts a WPF Key enum value to its config file string name.
        /// </summary>
        public static string GetConfigKeyName(Key key)
        {
            return key switch
            {
                Key.Space => "Space",
                _ => key.ToString()
            };
        }

        /// <summary>
        /// Returns a display/config name for a mouse button VK code, or null if not a mouse button.
        /// </summary>
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

        /// <summary>
        /// Gets the hardware scan code for a given virtual key code or key name.
        /// Returns fallback if mapping fails or returns 0.
        /// </summary>
        public static byte GetScanCode(string keyString, byte fallback)
        {
            int vk = GetVirtualKeyCode(keyString);
            if (vk <= 0) return fallback;
            uint scan = MapVirtualKey((uint)vk, 0); // MAPVK_VK_TO_VSC
            return scan > 0 ? (byte)scan : fallback;
        }
    }
}
