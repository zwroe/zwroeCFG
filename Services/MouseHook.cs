using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rebind.Services
{
    /// <summary>
    /// Handles low-level mouse button interception via the Windows API.
    /// Supports Mouse3 (Middle), Mouse4 (XButton1), and Mouse5 (XButton2).
    /// Fires the same KeyEvent signature as KeyboardHook so the engine can treat them uniformly.
    /// </summary>
    public class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MBUTTONDOWN  = 0x0207;
        private const int WM_MBUTTONUP    = 0x0208;
        private const int WM_XBUTTONDOWN  = 0x020B;
        private const int WM_XBUTTONUP    = 0x020C;

        public const int VK_MBUTTON  = 0x04; // Mouse3 / Middle
        public const int VK_XBUTTON1 = 0x05; // Mouse4 / Back side button
        public const int VK_XBUTTON2 = 0x06; // Mouse5 / Forward side button

        /// <summary>
        /// Event fired when a supported mouse button is pressed or released.
        /// Return true to block the event from reaching the system.
        /// </summary>
        public event Func<int, bool, bool>? KeyEvent;

        private LowLevelMouseProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        public MouseHook()
        {
            _proc = HookCallback;
            _hookID = SetHook(_proc);
        }

        private IntPtr SetHook(LowLevelMouseProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule? curModule = curProcess.MainModule)
            {
                if (curModule?.ModuleName == null) return IntPtr.Zero;
                return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isDown = false;
                bool isUp   = false;
                int  vkCode = 0;

                if (msg == WM_MBUTTONDOWN)
                {
                    isDown = true;
                    vkCode = VK_MBUTTON;
                }
                else if (msg == WM_MBUTTONUP)
                {
                    isUp   = true;
                    vkCode = VK_MBUTTON;
                }
                else if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                {
                    // mouseData is at offset 8 in MSLLHOOKSTRUCT; high-word identifies XButton 1 or 2
                    int mouseData = Marshal.ReadInt32(lParam, 8);
                    int xButton   = (mouseData >> 16) & 0xFFFF;
                    vkCode = (xButton == 1) ? VK_XBUTTON1 : VK_XBUTTON2;
                    isDown = (msg == WM_XBUTTONDOWN);
                    isUp   = (msg == WM_XBUTTONUP);
                }

                if ((isDown || isUp) && vkCode != 0)
                {
                    bool handled = KeyEvent?.Invoke(vkCode, isDown) ?? false;
                    if (handled) return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            UnhookWindowsHookEx(_hookID);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
