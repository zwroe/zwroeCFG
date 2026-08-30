using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rebind.Services
{
    /// <summary>
    /// Handles low-level keyboard interception via the Windows API.
    /// This allows the application to capture and block keystrokes before they reach other applications (like games).
    /// </summary>
    public class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        /// <summary>
        /// Magic marker placed in dwExtraInfo for all keybd_event calls made by this engine.
        /// The hook uses this to skip synthetic events and avoid re-processing its own output.
        /// </summary>
        public const uint SYNTHETIC_MARKER = 0x5A57524F; // "ZWRO" in hex

        private LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        /// <summary>
        /// Event fired when a key is pressed or released.
        /// Return true to block the key from reaching the system or other applications.
        /// </summary>
        public event Func<int, bool, bool>? KeyEvent;

        /// <summary>
        /// Initializes the keyboard hook and starts intercepting keystrokes.
        /// </summary>
        public KeyboardHook()
        {
            _proc = HookCallback;
            _hookID = SetHook(_proc);
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule? curModule = curProcess.MainModule)
            {
                if (curModule?.ModuleName == null)
                    return IntPtr.Zero;

                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// The callback function that processes intercepted keystrokes.
        /// Evaluates if the key should be blocked based on the KeyEvent subscribers.
        /// </summary>
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool isDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    // Skip events injected by our own engine to prevent feedback loops
                    uint extraInfo = (uint)Marshal.ReadInt32(lParam, 16);
                    if (extraInfo == SYNTHETIC_MARKER)
                        return CallNextHookEx(_hookID, nCode, wParam, lParam);

                    bool handled = KeyEvent?.Invoke(vkCode, isDown) ?? false;
                    
                    // If handled is true, we return 1 to block the key from Windows/Apex
                    if (handled) return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        /// <summary>
        /// Removes the hook and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            UnhookWindowsHookEx(_hookID);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
