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

    /// <summary>
    /// The core engine of the remapper.
    /// Handles listening to physical keystrokes, executing the high-precision macro loop,
    /// and routing simulated inputs to the virtual controller via ViGEm.
    /// </summary>
    public class KeyMapperService : IDisposable
    {
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        // High-resolution waitable timer (Windows 10 1803+)
        // Provides sub-millisecond precision at near-zero CPU cost
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
        private const byte SCAN_N = 0x31; // Inspect key

        // In-game movement keybind scan codes (dynamic, resolved from config)
        private byte _scanForward = 0x17;  // Default I
        private byte _scanBackward = 0x25; // Default K
        private byte _scanLeft = 0x24;     // Default J
        private byte _scanRight = 0x26;    // Default L
        private byte _scanJump = 0x15;     // Default Y

        // Primary WASD scan codes - used to synthesize keyboard events after tap-strafe
        private byte _scanCodeW = 0x11;    // W
        private byte _scanCodeS = 0x1F;    // S
        private byte _scanCodeA = 0x1E;    // A
        private byte _scanCodeD = 0x20;    // D

        private readonly KeyboardHook _keyboardHook;
        private readonly MouseHook _mouseHook;
        private readonly ViGEmService _vigemService;
        private readonly ConfigManager _configManager;

        private MappingConfig? _config;
        private bool _isEnabled = true;

        /// <summary>
        /// Indicates if the remapper engine is currently active.
        /// </summary>
        public bool IsEnabled => _isEnabled;

        /// <summary>
        /// Indicates if ViGEmBus controller is connected.
        /// </summary>
        public bool IsViGEmConnected => _vigemService.IsConnected;

        /// <summary>
        /// Set to true by the UI to pause input blocking, allowing the user to bind new keys.
        /// </summary>
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
        private readonly IntPtr _hrTimer; // high-resolution waitable timer handle (INVALID_HANDLE_VALUE if unsupported)
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
        // VK codes of physical WASD keys that have a synthetic KEYDOWN currently active in Apex.
        // When the physical key is released, we send the matching synthetic KEYUP so the key never sticks.
        private readonly HashSet<int> _syntheticActiveVks = new HashSet<int>();

        private short _lastLoggedJoyX = 0;
        private short _lastLoggedJoyY = 0;

        private Dictionary<byte, bool> _scanCodeStates = new Dictionary<byte, bool>();

        public event Action<bool>? OnToggleChanged;

        /// <summary>
        /// Initializes the KeyMapperService and starts the high-precision background macro loop.
        /// </summary>
        public KeyMapperService(ConfigManager configManager, KeyboardHook keyboardHook, MouseHook mouseHook, ViGEmService vigemService)
        {
            _configManager = configManager;
            _keyboardHook = keyboardHook;
            _mouseHook = mouseHook;
            _vigemService = vigemService;
            _keyPressActions = new Dictionary<int, Action<bool>>();

            timeBeginPeriod(1);

            // Try to create a high-resolution waitable timer (Windows 10 1803+).
            // If this succeeds, the macro loop will sleep via hardware timer instead of spinning,
            // giving identical timing precision at near-zero CPU cost.
            _hrTimer = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

            // Elevate process priority so Windows doesn't throttle the macro loop when
            // the app is in the background (e.g. game window is focused).
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                    System.Diagnostics.ProcessPriorityClass.High;
            }
            catch { /* silently ignore if running without sufficient privileges */ }

            _macroThread = new Thread(MacroLoop) { IsBackground = true, Priority = ThreadPriority.Highest };
            _macroThread.Start();

            ReloadConfig();

            _keyboardHook.KeyEvent += HandleKeyEvent;
            _mouseHook.KeyEvent += HandleKeyEvent;
        }

        /// <summary>
        /// Reloads the mapping configuration from the disk and rebuilds the virtual keycode cache.
        /// Can be called at runtime to apply new settings without restarting the engine.
        /// </summary>
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

            // Resolve dynamic scan codes for tap strafe output keys
            _scanForward  = KeyHelper.GetScanCode(_config.TapStrafeForward  ?? "I", 0x17);
            _scanBackward = KeyHelper.GetScanCode(_config.TapStrafeBackward ?? "K", 0x25);
            _scanLeft     = KeyHelper.GetScanCode(_config.TapStrafeLeft     ?? "J", 0x24);
            _scanRight    = KeyHelper.GetScanCode(_config.TapStrafeRight    ?? "L", 0x26);
            _scanJump     = KeyHelper.GetScanCode(_config.TapStrafeJump     ?? "Y", 0x15);

            // Resolve WASD scan codes for post-tap-strafe synthetic keyboard events
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

        /// <summary>
        /// The primary intercept handler. Decides whether to block a physical key press
        /// and trigger the corresponding controller logic (SnapTap, Jump, etc.).
        /// </summary>
        private bool HandleKeyEvent(int vkCode, bool isDown)
        {
            if (IsBindingMode) return false;

            if (vkCode == _toggleKeyVk && isDown)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isEnabled = !_isEnabled;
                if (!_isEnabled) ClearVirtualInputs();

                OnToggleChanged?.Invoke(_isEnabled);
                return true; // Block Insert key
            }

            if (!_isEnabled || _config == null) return false;

            // Superglide: Crouch immediately, then Jump after exactly 1 game frame.
            // Frame duration is calculated from the user's configured FPS.
            if (vkCode == _superglideKeyVk)
            {
                if (isDown)
                {
                    KeyLogger.Log($"SUPERGLIDE: Start - FPS={_config.SuperglideFps}");
                    _vigemService.SetButton(Xbox360Button.Down, true);  // Crouch

                    int fps = Math.Max(30, _config.SuperglideFps);
                    long frameTicks = Stopwatch.Frequency / fps; // 1 frame in ticks

                    // Fire jump on a background thread after exactly 1 frame
                    Task.Run(() =>
                    {
                        long target = _loopTimer.ElapsedTicks + frameTicks;
                        // Spin-wait for the frame window (same precision as the macro loop)
                        Thread.Sleep((int)(1000.0 / fps * 0.8)); // sleep 80% of frame
                        while (_loopTimer.ElapsedTicks < target) { /* spin */ }

                        // Release crouch and fire jump simultaneously
                        _vigemService.SetButton(Xbox360Button.Down, false);
                        SendScanCode(_scanJump, true);
                        Thread.Sleep(16); // hold jump for ~1 frame
                        SendScanCode(_scanJump, false);
                        KeyLogger.Log("SUPERGLIDE: Jump fired");
                    });
                }
                // Crouch release is handled inside the Task above; block the physical key
                return true;
            }

            // SnapTap (A/D)
            if (vkCode == _moveLeftVk || vkCode == _moveRightVk)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                UpdateSnapTap(vkCode, isDown);
                // On KEYUP, also release any synthetic KEYDOWN we issued for this key after tap-strafe
                if (!isDown && _syntheticActiveVks.Contains(vkCode))
                {
                    _syntheticActiveVks.Remove(vkCode);
                    byte sc = (vkCode == _moveLeftVk) ? _scanCodeA : _scanCodeD;
                    keybd_event(0, sc, KEYEVENTF_SCANCODE | (uint)KEYEVENTF_KEYUP, KeyboardHook.SYNTHETIC_MARKER);
                    KeyLogger.Log($"SYNTHETIC KEYUP: VK={vkCode} Scan=0x{sc:X2}");
                }
                return true; // Always block physical A/D
            }

            // Dedicated Tap Strafe Trigger Key (if rebound to a different key than Jump)
            if (vkCode == _tapStrafeTriggerVk && _tapStrafeTriggerVk != _jumpKeyVk)
            {
                if (isDown && _isTapStrafeTriggerPressed) return true; // Ignore auto-repeat
                KeyLogger.Log($"PHYSICAL TAP STRAFE KEY: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isTapStrafeTriggerPressed = isDown;

                if (isDown && _config.IsStrafeToggleMode)
                {
                    _isStrafeToggledActive = !_isStrafeToggledActive;
                    KeyLogger.Log($"TAP STRAFE TOGGLE MODE: Active={_isStrafeToggledActive}");
                }

                UpdateMovementOutput();
                return true; // Block physical key
            }

            // Jump (Space)
            if (vkCode == _jumpKeyVk)
            {
                // Ignore physical keyboard auto-repeat when Space is already held down
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
                        // Single jump tap when Auto-Jump is disabled
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
                return true; // Block physical Space
            }

            // Forward (W)
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
                return true; // Always block physical W
            }

            // Backward (S)
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
                return true; // Always block physical S
            }

            Action<bool>? action = null;
            lock (_mappingLock)
            {
                _keyPressActions.TryGetValue(vkCode, out action);
            }

            if (action != null)
            {
                action.Invoke(isDown);
                return true; // Block other mapped keys
            }

            return false;
        }

        /// <summary>
        /// Implements SnapTap (SOCD) logic. Resolves overlapping Left/Right inputs
        /// by strictly prioritizing the most recently pressed key, ensuring no momentum loss.
        /// </summary>
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

            // Micro-dither active non-zero axes by 1 unit on alternating macro ticks.
            // In hybrid input games like Apex Legends, static controller axes are put to sleep
            // when KBM events occur (mouse click, crouch, tap-strafe). Micro-dithering keeps
            // the XInput driver report stream active so movement NEVER stops or freezes.
            short dither = (short)((_macroCounter % 2 == 0) ? 0 : 1);
            _currentJoyY = rawY == 0 ? (short)0 : (short)(rawY > 0 ? rawY - dither : rawY + dither);
            _currentJoyX = rawX == 0 ? (short)0 : (short)(rawX > 0 ? rawX - dither : rawX + dither);

            if (forceRefresh || rawX != _lastLoggedJoyX || rawY != _lastLoggedJoyY)
            {
                _lastLoggedJoyX = rawX;
                _lastLoggedJoyY = rawY;
                KeyLogger.Log($"VIRTUAL INPUTS: JoyX={_currentJoyX} (raw={rawX}), JoyY={_currentJoyY} (raw={rawY}) | WASD=[W:{ew}, A:{ea}, S:{es}, D:{ed}] (forceRefresh={forceRefresh})");
            }

            // Update Virtual Xbox Controller Left Thumbstick with micro-dithered axis values
            _vigemService.SetAxis(Xbox360Axis.LeftThumbX, _currentJoyX);
            _vigemService.SetAxis(Xbox360Axis.LeftThumbY, _currentJoyY);
        }

        private void SendScanCode(byte scanCode, bool isDown, bool force = false)
        {
            if (!force)
            {
                if (!_scanCodeStates.ContainsKey(scanCode)) _scanCodeStates[scanCode] = false;
                
                // Skip redundant hardware calls
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

            // Release all virtual in-game keys
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

        /// <summary>
        /// A high-priority background loop running at 3ms intervals.
        /// Executes tap-strafe pulsing, optional auto jump, and fast-loot helpers.
        /// </summary>
        private void MacroLoop()
        {
            while (_isRunning)
            {
                if (_isEnabled && _config != null)
                {
                    _macroCounter++;

                    // CONTINUOUS MOVEMENT RE-ASSERTION (Every 5 ticks = 15ms)
                    // Whenever movement keys are held in stack, continuously refresh the controller stick
                    // with micro-dithering so mouse clicks, crouch, and tap-strafe scan-codes never freeze movement.
                    bool hasMovementKeys;
                    lock (_stackLock)
                    {
                        hasMovementKeys = _horizontalStack.Count > 0 || _verticalStack.Count > 0;
                    }

                    if (_macroCounter % 5 == 0 && hasMovementKeys)
                    {
                        UpdateMovementOutput();
                    }

                    // ── Tap Strafe Engine ──────────────────────────────────────────
                    // Rules:
                    //   Space + W  → spam _scanJump + _scanForward
                    //   Space + S  → spam _scanJump + _scanBackward
                    //   Space + A  → spam _scanJump + _scanLeft
                    //   Space + D  → spam _scanJump + _scanRight
                    if (IsMacroActive())
                    {
                        if (!_wasTapStrafeActive)
                        {
                            // We just started tap-strafing. Reset the local counter so we immediately 
                            // send an ON pulse, eliminating any startup delay.
                            _tapStrafeCounter = 0;
                            _wasTapStrafeActive = true;
                        }

                        _tapStrafeCounter++;

                        // Thread-safe stack snapshot
                        bool holdW, holdS, holdA, holdD;
                        lock (_stackLock)
                        {
                            holdW = _verticalStack.Count > 0 && _verticalStack[_verticalStack.Count - 1] == _strafeKeyVk;
                            holdS = _verticalStack.Count > 0 && _verticalStack[_verticalStack.Count - 1] == _backVk;
                            holdA = _horizontalStack.Count > 0 && _horizontalStack[_horizontalStack.Count - 1] == _moveLeftVk;
                            holdD = _horizontalStack.Count > 0 && _horizontalStack[_horizontalStack.Count - 1] == _moveRightVk;
                        }

                        // Log state every 100 ticks (~300ms)
                        if (_macroCounter % 100 == 0)
                            KeyLogger.Log($"STRAFE STATE: W={holdW} S={holdS} A={holdA} D={holdD} | vStack={_verticalStack.Count} hStack={_horizontalStack.Count}");

                        // Pulse: 9ms ON (3 ticks) / 6ms OFF (2 ticks) = 67Hz jump rate.
                        bool tapOn = (_tapStrafeCounter % 5 < 3);

                        if (tapOn)
                        {
                            SendScanCode(_scanJump, true);

                            // Prioritize forward lurch (_scanForward) when W is held.
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
                        // Clean up when Space released or tap-strafe toggled off
                        if (_wasTapStrafeActive)
                        {
                            _wasTapStrafeActive = false;
                            SendScanCode(_scanJump, false, force: true);
                            SendScanCode(_scanForward, false, force: true);
                            SendScanCode(_scanBackward, false, force: true);
                            SendScanCode(_scanLeft, false, force: true);
                            SendScanCode(_scanRight, false, force: true);

                            // Release any synthetic keys cleanly
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

                            // Reset controller stick and re-assert movement output
                            _vigemService.SetAxis(Xbox360Axis.LeftThumbX, 0);
                            _vigemService.SetAxis(Xbox360Axis.LeftThumbY, 0);
                            Thread.Sleep(10);
                            UpdateMovementOutput(forceRefresh: true);

                            KeyLogger.Log("TAP STRAFE ENDED: All lurch keys released.");
                        }

                        if (_macroCounter % 5 == 0)
                        {
                            // Normal Jump Spam (when not Tap Strafing)
                            if (_config.IsJumpSpamEnabled && _isJumpKeyPressed)
                            {
                                _jumpState = !_jumpState;
                                _vigemService.SetButton(Xbox360Button.LeftShoulder, _jumpState);
                                SendScanCode(SCAN_SPACE, _jumpState);
                            }
                        }


                    }

                    // FAST LOOT SPAM (Keyboard E)
                    // Every 5 steps (15ms toggle = 30ms cycle = 33Hz)
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

                    // INSPECT SPAM (Keyboard N)
                    // Configurable millisecond delay between key toggles
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

                // Precise 3ms sleep using hardware waitable timer (near-zero CPU).
                // Falls back to spin-wait on Windows < 1803 where the timer isn't available.
                if (_hrTimer != IntPtr.Zero && _hrTimer != new IntPtr(-1))
                {
                    // Negative value = relative time in 100-nanosecond units. -30000 = 3ms.
                    long dueTime = -30000L;
                    SetWaitableTimer(_hrTimer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(_hrTimer, 10); // 10ms safety timeout
                }
                else
                {
                    // Fallback: pure spin-wait (immune to scheduler throttling, uses ~1 CPU core)
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
