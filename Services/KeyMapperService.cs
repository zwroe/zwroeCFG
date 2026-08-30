using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Rebind.Core.Models;
using Rebind.Helpers;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System.IO;

namespace Rebind.Services
{
    public static class KeyLogger
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Rebind", "logs");
        private static readonly string LogFilePath = Path.Combine(LogDirectory, "movement_debug.log");
        private static readonly object LogLock = new object();
        
        public static string LogPath => LogFilePath;

        public static void Log(string message)
        {
            try 
            { 
                lock (LogLock) 
                { 
                    if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(LogFilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); 
                } 
            } 
            catch { }
        }
    }

    public class KeyMapperService : IDisposable
    {
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        // High-resolution waitable timers are available on Windows 10 1803 and later.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerEx(IntPtr attrs, IntPtr name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr hTimer, ref long dueTime, int period, IntPtr callback, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMs);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;
        private const uint WAIT_OBJECT_0 = 0x00000000;

        private const int KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const byte VK_E = 0x45;
        private const byte SCAN_E = 0x12;
        private const byte SCAN_SPACE = 0x39;
        private const byte VK_N = 0x4E;
        private const byte SCAN_N = 0x31;

        private byte _scanForward = 0x17;
        private byte _scanBackward = 0x25;
        private byte _scanLeft = 0x24;
        private byte _scanRight = 0x26;
        private byte _scanJump = 0x15;

        private byte _scanCodeW = 0x11;
        private byte _scanCodeS = 0x1F;
        private byte _scanCodeA = 0x1E;
        private byte _scanCodeD = 0x20;

        private readonly KeyboardHook _keyboardHook;
        private readonly MouseHook _mouseHook;
        private readonly ViGEmService _vigemService;
        private readonly ConfigManager _configManager;

        private MappingConfig? _config;
        private bool _isEnabled = true;

        public bool IsEnabled => _isEnabled;

        public bool IsViGEmConnected => _vigemService.IsConnected;

        // Binding mode lets the UI capture a key without the hook blocking it.
        public bool IsBindingMode { get; set; } = false;

        private int _toggleKeyVk;
        private int _strafeKeyVk;
        private int _jumpKeyVk;
        private int _moveLeftVk;
        private int _moveRightVk;
        private int _backVk;
        private int _superglideKeyVk;
        private int _tapStrafeTriggerVk;

        private Dictionary<int, Action<bool>> _keyPressActions;
        private readonly object _mappingLock = new object();

        private readonly Thread _macroThread;
        private bool _isRunning = true;
        private bool _isStrafeKeyPressed = false;
        private bool _isBackKeyPressed = false;
        private bool _isJumpKeyPressed = false;
        private bool _isTapStrafeTriggerPressed = false;
        private bool _isStrafeToggledActive = false;

        private short _currentJoyX = 0;
        private short _currentJoyY = 0;

        private int _macroCounter = 0;
        private int _tapStrafeCounter = 0;
        private readonly Stopwatch _loopTimer = Stopwatch.StartNew();
        private readonly IntPtr _hrTimer;
        private bool _jumpState = false;
        private bool _lootState = false;
        private bool _isLootKeyPressed = false;
        private bool _inspectState = false;
        private bool _isInspectKeyPressed = false;
        private long _lastInspectToggleMs = 0;

        private readonly object _stackLock = new object();
        private List<int> _horizontalStack = new List<int>();
        private List<int> _verticalStack = new List<int>();

        private bool _wasTapStrafeActive = false;
        // Track synthetic holds so every physical release gets a matching KEYUP.
        private readonly HashSet<int> _syntheticActiveVks = new HashSet<int>();

        private short _lastLoggedJoyX = 0;
        private short _lastLoggedJoyY = 0;

        private Dictionary<byte, bool> _scanCodeStates = new Dictionary<byte, bool>();

        public event Action<bool>? OnToggleChanged;

        public KeyMapperService(ConfigManager configManager, KeyboardHook keyboardHook, MouseHook mouseHook, ViGEmService vigemService)
        {
            _configManager = configManager;
            _keyboardHook = keyboardHook;
            _mouseHook = mouseHook;
            _vigemService = vigemService;
            _keyPressActions = new Dictionary<int, Action<bool>>();

            timeBeginPeriod(1);

            // Prefer a Windows high-resolution timer so the 3 ms loop can sleep without losing precision.
            _hrTimer = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

            // Keep the timing loop from being deprioritized while the game has focus.
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                    System.Diagnostics.ProcessPriorityClass.High;
            }
            catch { }

            _macroThread = new Thread(MacroLoop) { IsBackground = true, Priority = ThreadPriority.Highest };
            _macroThread.Start();

            ReloadConfig();

            _keyboardHook.KeyEvent += HandleKeyEvent;
            _mouseHook.KeyEvent += HandleKeyEvent;
        }

        public void ReloadConfig()
        {
            ClearVirtualInputs();
            _config = _configManager.LoadConfig();

            _toggleKeyVk = KeyHelper.GetVirtualKeyCode(_config.ToggleShortcut ?? "Insert");
            _strafeKeyVk = KeyHelper.GetVirtualKeyCode(_config.JoystickYPositive ?? "W");
            _jumpKeyVk = KeyHelper.GetVirtualKeyCode(_config.LeftBumper ?? "Space");
            _moveLeftVk = KeyHelper.GetVirtualKeyCode(_config.JoystickXNegative ?? "A");
            _moveRightVk = KeyHelper.GetVirtualKeyCode(_config.JoystickXPositive ?? "D");
            _backVk = KeyHelper.GetVirtualKeyCode(_config.JoystickYNegative ?? "S");
            _tapStrafeTriggerVk = KeyHelper.GetVirtualKeyCode(_config.TapStrafeKey ?? _config.LeftBumper ?? "Space");

            _scanForward  = KeyHelper.GetScanCode(_config.TapStrafeForward  ?? "I", 0x17);
            _scanBackward = KeyHelper.GetScanCode(_config.TapStrafeBackward ?? "K", 0x25);
            _scanLeft     = KeyHelper.GetScanCode(_config.TapStrafeLeft     ?? "J", 0x24);
            _scanRight    = KeyHelper.GetScanCode(_config.TapStrafeRight    ?? "L", 0x26);
            _scanJump     = KeyHelper.GetScanCode(_config.TapStrafeJump     ?? "Y", 0x15);

            _scanCodeW = KeyHelper.GetScanCode(_config.JoystickYPositive ?? "W", 0x11);
            _scanCodeS = KeyHelper.GetScanCode(_config.JoystickYNegative ?? "S", 0x1F);
            _scanCodeA = KeyHelper.GetScanCode(_config.JoystickXNegative ?? "A", 0x1E);
            _scanCodeD = KeyHelper.GetScanCode(_config.JoystickXPositive ?? "D", 0x20);

            lock (_mappingLock)
            {
                BuildMappingCache();
            }
        }

        private void BuildMappingCache()
        {
            if (_config == null) return;
            _keyPressActions.Clear();

            AddMapping(_config.DPadUp, isDown => _vigemService.SetButton(Xbox360Button.Up, isDown));
            // DPadDown (superglide) is handled directly in HandleKeyEvent for frame-accurate timing.
            _superglideKeyVk = KeyHelper.GetVirtualKeyCode(_config.DPadDown ?? "V");
            AddMapping(_config.DPadLeft, isDown => _vigemService.SetButton(Xbox360Button.Left, isDown));
            AddMapping(_config.DPadRight, isDown => _vigemService.SetButton(Xbox360Button.Right, isDown));
            AddMapping(_config.Guide, isDown => _vigemService.SetButton(Xbox360Button.Guide, isDown));
            AddMapping(_config.FastLootKey, isDown => _isLootKeyPressed = isDown);
            AddMapping(_config.InspectKey, isDown => _isInspectKeyPressed = isDown);
        }

        private void AddMapping(string? keyString, Action<bool> action)
        {
            if (keyString == null) return;
            int vkCode = KeyHelper.GetVirtualKeyCode(keyString);
            if (vkCode != -1)
            {
                if (_keyPressActions.ContainsKey(vkCode)) _keyPressActions[vkCode] += action;
                else _keyPressActions[vkCode] = action;
            }
        }

        private bool HandleKeyEvent(int vkCode, bool isDown)
        {
            if (IsBindingMode) return false;

            if (vkCode == _toggleKeyVk && isDown)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isEnabled = !_isEnabled;
                if (!_isEnabled) ClearVirtualInputs();

                OnToggleChanged?.Invoke(_isEnabled);
                return true;
            }

            if (!_isEnabled || _config == null) return false;

            // Superglide needs crouch and jump separated by one configured frame.
            if (vkCode == _superglideKeyVk)
            {
                if (isDown)
                {
                    KeyLogger.Log($"SUPERGLIDE: Start - FPS={_config.SuperglideFps}");
                    _vigemService.SetButton(Xbox360Button.Down, true);

                    int fps = Math.Max(30, _config.SuperglideFps);
                    long frameTicks = Stopwatch.Frequency / fps;

                    Task.Run(() =>
                    {
                        long target = _loopTimer.ElapsedTicks + frameTicks;
                        // Sleep through most of the frame, then spin for the precise edge.
                        Thread.Sleep((int)(1000.0 / fps * 0.8));
                        while (_loopTimer.ElapsedTicks < target) { /* spin */ }

                        _vigemService.SetButton(Xbox360Button.Down, false);
                        SendScanCode(_scanJump, true);
                        Thread.Sleep(16);
                        SendScanCode(_scanJump, false);
                        KeyLogger.Log("SUPERGLIDE: Jump fired");
                    });
                }
                // The task owns the crouch release, so both physical events stay blocked.
                return true;
            }

            if (vkCode == _moveLeftVk || vkCode == _moveRightVk)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                UpdateSnapTap(vkCode, isDown);
                // Pair tap-strafe's synthetic hold with the physical release so the key cannot stick.
                if (!isDown && _syntheticActiveVks.Contains(vkCode))
                {
                    _syntheticActiveVks.Remove(vkCode);
                    byte sc = (vkCode == _moveLeftVk) ? _scanCodeA : _scanCodeD;
                    keybd_event(0, sc, KEYEVENTF_SCANCODE | (uint)KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                    KeyLogger.Log($"SYNTHETIC KEYUP: VK={vkCode} Scan=0x{sc:X2}");
                }
                return true;
            }

            if (vkCode == _tapStrafeTriggerVk && _tapStrafeTriggerVk != _jumpKeyVk)
            {
                if (isDown && _isTapStrafeTriggerPressed) return true;
                KeyLogger.Log($"PHYSICAL TAP STRAFE KEY: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isTapStrafeTriggerPressed = isDown;

                if (isDown && _config.IsStrafeToggleMode)
                {
                    _isStrafeToggledActive = !_isStrafeToggledActive;
                    KeyLogger.Log($"TAP STRAFE TOGGLE MODE: Active={_isStrafeToggledActive}");
                }

                UpdateMovementOutput();
                return true;
            }

            if (vkCode == _jumpKeyVk)
            {
                // Repeated keydown events would retrigger the tap while the key is held.
                if (isDown && _isJumpKeyPressed)
                {
                    return true;
                }

                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isJumpKeyPressed = isDown;
                if (_tapStrafeTriggerVk == _jumpKeyVk)
                {
                    _isTapStrafeTriggerPressed = isDown;
                }

                if (isDown && _config.IsStrafeToggleMode && _tapStrafeTriggerVk == _jumpKeyVk)
                {
                    _isStrafeToggledActive = !_isStrafeToggledActive;
                    KeyLogger.Log($"TAP STRAFE TOGGLE MODE: Active={_isStrafeToggledActive}");
                }

                if (_config.IsJumpSpamEnabled)
                {
                    if (!isDown) 
                    { 
                        _jumpState = false; 
                        _vigemService.SetButton(Xbox360Button.LeftShoulder, false); 
                        if (!IsMacroActive())
                        {
                            SendScanCode(SCAN_SPACE, false); 
                        }
                    }
                }
                else
                {
                    if (isDown)
                    {
                        _vigemService.SetButton(Xbox360Button.LeftShoulder, true);
                        SendScanCode(SCAN_SPACE, true);

                        Task.Run(async () =>
                        {
                            await Task.Delay(30);
                            _vigemService.SetButton(Xbox360Button.LeftShoulder, false);
                            SendScanCode(SCAN_SPACE, false);
                        });
                    }
                    else
                    {
                        _vigemService.SetButton(Xbox360Button.LeftShoulder, false);
                        SendScanCode(SCAN_SPACE, false);
                    }
                }

                UpdateMovementOutput();
                return true;
            }

            if (vkCode == _strafeKeyVk)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isStrafeKeyPressed = isDown;
                UpdateSnapTap(vkCode, isDown);
                if (!isDown && _syntheticActiveVks.Contains(vkCode))
                {
                    _syntheticActiveVks.Remove(vkCode);
                    keybd_event(0, _scanCodeW, KEYEVENTF_SCANCODE | (uint)KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                    KeyLogger.Log($"SYNTHETIC KEYUP: W VK={vkCode}");
                }
                return true;
            }

            if (vkCode == _backVk)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isBackKeyPressed = isDown;
                UpdateSnapTap(vkCode, isDown);
                if (!isDown && _syntheticActiveVks.Contains(vkCode))
                {
                    _syntheticActiveVks.Remove(vkCode);
                    keybd_event(0, _scanCodeS, KEYEVENTF_SCANCODE | (uint)KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                    KeyLogger.Log($"SYNTHETIC KEYUP: S VK={vkCode}");
                }
                return true;
            }

            Action<bool>? action = null;
            lock (_mappingLock)
            {
                _keyPressActions.TryGetValue(vkCode, out action);
            }

            if (action != null)
            {
                action.Invoke(isDown);
                return true;
            }

            return false;
        }

        private void UpdateSnapTap(int vkCode, bool isDown)
        {
            lock (_stackLock)
            {
                if (vkCode == _moveLeftVk || vkCode == _moveRightVk)
                {
                    if (isDown) { if (!_horizontalStack.Contains(vkCode)) _horizontalStack.Add(vkCode); }
                    else { _horizontalStack.Remove(vkCode); }
                }
                else if (vkCode == _strafeKeyVk || vkCode == _backVk)
                {
                    if (isDown) { if (!_verticalStack.Contains(vkCode)) _verticalStack.Add(vkCode); }
                    else { _verticalStack.Remove(vkCode); }
                }
            }

            KeyLogger.Log($"SNAP TAP STACK: hStack=[{string.Join(",", _horizontalStack)}] vStack=[{string.Join(",", _verticalStack)}]");
            UpdateMovementOutput();
        }

        private bool IsMacroActive()
        {
            if (_config?.IsStrafeEnabled != true) return false;

            bool isTriggerActive = _config.IsStrafeToggleMode ? _isStrafeToggledActive : _isJumpKeyPressed;
            return isTriggerActive && (_isStrafeKeyPressed || _isBackKeyPressed || _horizontalStack.Count > 0);
        }


        private void UpdateMovementOutput(bool forceRefresh = false)
        {
            bool ew = false, es = false, ea = false, ed = false;

            lock (_stackLock)
            {
                if (_verticalStack.Count > 0)
                {
                    int lastY = _verticalStack[_verticalStack.Count - 1];
                    if (lastY == _strafeKeyVk) ew = true;
                    else if (lastY == _backVk) es = true;
                }

                if (_horizontalStack.Count > 0)
                {
                    int lastX = _horizontalStack[_horizontalStack.Count - 1];
                    if (lastX == _moveLeftVk) ea = true;
                    else if (lastX == _moveRightVk) ed = true;
                }
            }

            short rawY = (short)(ew ? 32767 : (es ? -32768 : 0));
            short rawX = (short)(ed ? 32767 : (ea ? -32768 : 0));

            // Apex can stop reporting a static controller axis after keyboard or mouse input.
            // Alternating by one unit keeps XInput movement active.
            short dither = (short)((_macroCounter % 2 == 0) ? 0 : 1);
            _currentJoyY = rawY == 0 ? (short)0 : (short)(rawY > 0 ? rawY - dither : rawY + dither);
            _currentJoyX = rawX == 0 ? (short)0 : (short)(rawX > 0 ? rawX - dither : rawX + dither);

            if (forceRefresh || rawX != _lastLoggedJoyX || rawY != _lastLoggedJoyY)
            {
                _lastLoggedJoyX = rawX;
                _lastLoggedJoyY = rawY;
                KeyLogger.Log($"VIRTUAL INPUTS: JoyX={_currentJoyX} (raw={rawX}), JoyY={_currentJoyY} (raw={rawY}) | WASD=[W:{ew}, A:{ea}, S:{es}, D:{ed}] (forceRefresh={forceRefresh})");
            }

            _vigemService.SetAxis(Xbox360Axis.LeftThumbX, _currentJoyX);
            _vigemService.SetAxis(Xbox360Axis.LeftThumbY, _currentJoyY);
        }

        private void SendScanCode(byte scanCode, bool isDown, bool force = false)
        {
            if (!force)
            {
                if (!_scanCodeStates.ContainsKey(scanCode)) _scanCodeStates[scanCode] = false;
                
                if (_scanCodeStates[scanCode] == isDown) return;
            }
            
            _scanCodeStates[scanCode] = isDown;
            KeyLogger.Log($"VIRTUAL KEYBOARD: {(isDown ? "DOWN" : "UP")} - SCAN: 0x{scanCode:X2}");

            uint flag = KEYEVENTF_SCANCODE | (isDown ? 0 : (uint)KEYEVENTF_KEYUP);
            keybd_event(0, scanCode, flag, KeyboardHook.SYNTHETIC_MARKER);
        }

        private void ClearVirtualInputs()
        {
            _isStrafeKeyPressed = false;
            _isBackKeyPressed = false;
            _isJumpKeyPressed = false;
            _isTapStrafeTriggerPressed = false;
            _isStrafeToggledActive = false;
            _isLootKeyPressed = false;
            _jumpState = false;
            _lootState = false;
            _horizontalStack.Clear();
            _verticalStack.Clear();
            _syntheticActiveVks.Clear();

            _currentJoyX = 0;
            _currentJoyY = 0;

            SendScanCode(_scanForward, false);
            SendScanCode(_scanBackward, false);
            SendScanCode(_scanLeft, false);
            SendScanCode(_scanRight, false);
            SendScanCode(_scanJump, false);
            SendScanCode(SCAN_SPACE, false);

            _vigemService.SetAxis(Xbox360Axis.LeftThumbX, 0);
            _vigemService.SetAxis(Xbox360Axis.LeftThumbY, 0);
            _vigemService.SetButton(Xbox360Button.LeftShoulder, false);
            _vigemService.SetButton(Xbox360Button.Up, false);
            _vigemService.SetButton(Xbox360Button.Down, false);
            _vigemService.SetButton(Xbox360Button.Left, false);
            _vigemService.SetButton(Xbox360Button.Right, false);
            _vigemService.SetButton(Xbox360Button.Guide, false);
            keybd_event(VK_E, SCAN_E, KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
            keybd_event(VK_N, SCAN_N, KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
            _inspectState = false;
            _isInspectKeyPressed = false;
        }

        private void MacroLoop()
        {
            while (_isRunning)
            {
                if (_isEnabled && _config != null)
                {
                    _macroCounter++;

                    // Refresh held movement every 15 ms because hybrid-input events can stall a static stick report.
                    bool hasMovementKeys;
                    lock (_stackLock)
                    {
                        hasMovementKeys = _horizontalStack.Count > 0 || _verticalStack.Count > 0;
                    }

                    if (_macroCounter % 5 == 0 && hasMovementKeys)
                    {
                        UpdateMovementOutput();
                    }

                    if (IsMacroActive())
                    {
                        if (!_wasTapStrafeActive)
                        {
                            // Start with an ON pulse instead of waiting for the previous counter phase.
                            _tapStrafeCounter = 0;
                            _wasTapStrafeActive = true;
                        }

                        _tapStrafeCounter++;

                        bool holdW, holdS, holdA, holdD;
                        lock (_stackLock)
                        {
                            holdW = _verticalStack.Count > 0 && _verticalStack[_verticalStack.Count - 1] == _strafeKeyVk;
                            holdS = _verticalStack.Count > 0 && _verticalStack[_verticalStack.Count - 1] == _backVk;
                            holdA = _horizontalStack.Count > 0 && _horizontalStack[_horizontalStack.Count - 1] == _moveLeftVk;
                            holdD = _horizontalStack.Count > 0 && _horizontalStack[_horizontalStack.Count - 1] == _moveRightVk;
                        }

                        if (_macroCounter % 100 == 0)
                            KeyLogger.Log($"STRAFE STATE: W={holdW} S={holdS} A={holdA} D={holdD} | vStack={_verticalStack.Count} hStack={_horizontalStack.Count}");

                        // The 9 ms on / 6 ms off pulse produces a 67 Hz lurch rate.
                        bool tapOn = (_tapStrafeCounter % 5 < 3);

                        if (tapOn)
                        {
                            SendScanCode(_scanJump, true);

                            // Forward takes priority when opposing lurch directions overlap.
                            bool sendI = holdW;
                            bool sendK = holdS && !holdW;
                            bool sendJ = holdA && !holdW && !holdS;
                            bool sendL = holdD && !holdW && !holdS;

                            SendScanCode(_scanForward, sendI);
                            SendScanCode(_scanBackward, sendK);
                            SendScanCode(_scanLeft, sendJ);
                            SendScanCode(_scanRight, sendL);
                        }
                        else
                        {
                            SendScanCode(_scanJump, false);

                            SendScanCode(_scanForward, false);
                            SendScanCode(_scanBackward, false);
                            SendScanCode(_scanLeft, false);
                            SendScanCode(_scanRight, false);
                        }
                    }
                    else
                    {
                        if (_wasTapStrafeActive)
                        {
                            _wasTapStrafeActive = false;
                            SendScanCode(_scanJump, false, force: true);
                            SendScanCode(_scanForward, false, force: true);
                            SendScanCode(_scanBackward, false, force: true);
                            SendScanCode(_scanLeft, false, force: true);
                            SendScanCode(_scanRight, false, force: true);

                            // End synthetic holds before restoring controller movement.
                            lock (_stackLock)
                            {
                                foreach (int vk in _syntheticActiveVks)
                                {
                                    byte sc;
                                    if (vk == _moveLeftVk) sc = _scanCodeA;
                                    else if (vk == _moveRightVk) sc = _scanCodeD;
                                    else if (vk == _strafeKeyVk) sc = _scanCodeW;
                                    else sc = _scanCodeS;
                                    keybd_event(0, sc, KEYEVENTF_SCANCODE | (uint)KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                                }
                                _syntheticActiveVks.Clear();
                            }

                            // Briefly clear the stick to avoid restoring a stale mixed-input state.
                            _vigemService.SetAxis(Xbox360Axis.LeftThumbX, 0);
                            _vigemService.SetAxis(Xbox360Axis.LeftThumbY, 0);
                            Thread.Sleep(10);
                            UpdateMovementOutput(forceRefresh: true);

                            KeyLogger.Log("TAP STRAFE ENDED: All lurch keys released.");
                        }

                        if (_macroCounter % 5 == 0)
                        {
                            if (_config.IsJumpSpamEnabled && _isJumpKeyPressed)
                            {
                                _jumpState = !_jumpState;
                                _vigemService.SetButton(Xbox360Button.LeftShoulder, _jumpState);
                                SendScanCode(SCAN_SPACE, _jumpState);
                            }
                        }


                    }

                    // Toggle fast loot every 15 ms for a 30 ms key cycle.
                    if (_macroCounter % 5 == 0)
                    {
                        if (_isLootKeyPressed)
                        {
                            _lootState = !_lootState;
                            if (_lootState) keybd_event(VK_E, SCAN_E, 0, KeyboardHook.SYNTHETIC_MARKER);
                            else keybd_event(VK_E, SCAN_E, KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                        }
                        else if (_lootState)
                        {
                            _lootState = false;
                            keybd_event(VK_E, SCAN_E, KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                        }

                    }

                    if (_isInspectKeyPressed)
                    {
                        long currentMs = _loopTimer.ElapsedMilliseconds;
                        int delayMs = Math.Max(5, _config.InspectDelayMs);
                        if (currentMs - _lastInspectToggleMs >= delayMs)
                        {
                            _lastInspectToggleMs = currentMs;
                            _inspectState = !_inspectState;
                            if (_inspectState) keybd_event(VK_N, SCAN_N, 0, KeyboardHook.SYNTHETIC_MARKER);
                            else keybd_event(VK_N, SCAN_N, KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                        }
                    }
                    else if (_inspectState)
                    {
                        _inspectState = false;
                        keybd_event(VK_N, SCAN_N, KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                    }
                }

                // Older Windows versions fall back to spinning when high-resolution timers are unavailable.
                if (_hrTimer != IntPtr.Zero && _hrTimer != new IntPtr(-1))
                {
                    // Negative due times are relative 100 ns units; -30000 is 3 ms.
                    long dueTime = -30000L;
                    SetWaitableTimer(_hrTimer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(_hrTimer, 10);
                }
                else
                {
                    // Spinning avoids scheduler jitter here, at the cost of one CPU core.
                    long targetTicks = _loopTimer.ElapsedTicks + (Stopwatch.Frequency * 3 / 1000);
                    while (_loopTimer.ElapsedTicks < targetTicks) { Thread.SpinWait(10); }
                }
            }
        }

        public void Dispose()
        {
            _keyboardHook.KeyEvent -= HandleKeyEvent;
            _mouseHook.KeyEvent -= HandleKeyEvent;
            ClearVirtualInputs();
            _isRunning = false;
            timeEndPeriod(1);
            _macroThread.Join(500);
            if (_hrTimer != IntPtr.Zero && _hrTimer != new IntPtr(-1))
                CloseHandle(_hrTimer);
            _keyboardHook.Dispose();
            _mouseHook.Dispose();
            _vigemService.Dispose();
        }
    }
}
