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
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const byte VK_E = 0x45;
        private static readonly ScanCode SCAN_E = new(0x12, false);
        private const byte VK_N = 0x4E;
        private static readonly ScanCode SCAN_N = new(0x31, false);

        private ScanCode _scanForward = new(0x17, false);
        private ScanCode _scanBackward = new(0x25, false);
        private ScanCode _scanLeft = new(0x24, false);
        private ScanCode _scanRight = new(0x26, false);
        private ScanCode _scanJump = new(0x15, false);

        private ScanCode _scanCodeW = new(0x11, false);
        private ScanCode _scanCodeS = new(0x1F, false);
        private ScanCode _scanCodeA = new(0x1E, false);
        private ScanCode _scanCodeD = new(0x20, false);

        private readonly KeyboardHook _keyboardHook;
        private readonly MouseHook _mouseHook;
        private readonly ViGEmService _vigemService;
        private readonly ConfigManager _configManager;

        private volatile MappingConfig? _config;
        private volatile bool _isEnabled = true;
        private volatile bool _isReloading;

        public bool IsEnabled => _isEnabled && IsViGEmConnected;

        internal InputTrace InputTrace { get; } = new();

        public bool IsViGEmConnected => _vigemService.IsConnected;

        public string? InputHookErrorMessage
        {
            get
            {
                var errors = new List<string>();
                if (!_keyboardHook.IsInstalled)
                    errors.Add($"Keyboard hook: {_keyboardHook.ErrorMessage ?? "installation failed"}");
                if (!_mouseHook.IsInstalled)
                    errors.Add($"Mouse hook: {_mouseHook.ErrorMessage ?? "installation failed"}");
                return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
            }
        }

        // Binding mode lets the UI capture a key without the hook blocking it.
        public bool IsBindingMode
        {
            get => _isBindingMode;
            set
            {
                if (_isBindingMode == value) return;
                _isBindingMode = value;
                if (value && IsEnabled) ClearVirtualInputs();
            }
        }

        private volatile bool _isBindingMode;

        private int _toggleKeyVk;
        // Both hooks run on the UI thread. A null generation means the original press passed through.
        private readonly Dictionary<int, long?> _physicalKeyDowns = new();
        private int _strafeKeyVk;
        private int _jumpKeyVk;
        private int _moveLeftVk;
        private int _moveRightVk;
        private int _backVk;
        private int _mantleJumpKeyVk;
        private int _superglideKeyVk;
        private int _tapStrafeTriggerVk;

        private Dictionary<int, Action<bool>> _keyPressActions;
        private readonly object _mappingLock = new object();
        private readonly object _scanCodeLock = new object();
        private readonly object _lifecycleLock = new object();
        private readonly object _controllerLock = new object();
        private readonly object _movementOutputLock = new object();
        private long _inputGeneration;

        private readonly Thread _macroThread;
        private volatile bool _isRunning = true;
        private volatile bool _isStrafeKeyPressed = false;
        private volatile bool _isBackKeyPressed = false;
        private volatile bool _isJumpKeyPressed = false;
        private volatile bool _isTapStrafeTriggerPressed;
        private volatile bool _isStrafeToggledActive = false;
        private bool _isSuperglideRunning;
        private volatile bool _isMantleJumpRunning;
        private long _jumpPressId;

        private short _currentJoyX = 0;
        private short _currentJoyY = 0;

        private int _macroCounter = 0;
        private int _tapStrafeCounter = 0;
        private readonly Stopwatch _loopTimer = Stopwatch.StartNew();
        private readonly IntPtr _hrTimer;
        private bool _jumpState;
        private long _nextJumpToggleTicks;
        private volatile bool _lootState = false;
        private volatile bool _isLootKeyPressed = false;
        private volatile bool _inspectState = false;
        private volatile bool _isInspectKeyPressed = false;
        private long _lastInspectToggleMs = 0;

        private readonly object _stackLock = new object();
        private List<int> _horizontalStack = new List<int>();
        private List<int> _verticalStack = new List<int>();

        private bool _regularJumpDown;
        private bool _techniqueJumpDown;
        // Track synthetic holds so every physical release gets a matching KEYUP.
        private readonly HashSet<int> _syntheticActiveVks = new HashSet<int>();

        private short _lastLoggedJoyX = 0;
        private short _lastLoggedJoyY = 0;

        private readonly Dictionary<ScanCode, bool> _scanCodeStates = new Dictionary<ScanCode, bool>();

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
            lock (_lifecycleLock)
            {
                _isReloading = true;
                try
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

                    _scanForward  = KeyHelper.GetScanCode(_config.TapStrafeForward  ?? "I", new ScanCode(0x17, false));
                    _scanBackward = KeyHelper.GetScanCode(_config.TapStrafeBackward ?? "K", new ScanCode(0x25, false));
                    _scanLeft     = KeyHelper.GetScanCode(_config.TapStrafeLeft     ?? "J", new ScanCode(0x24, false));
                    _scanRight    = KeyHelper.GetScanCode(_config.TapStrafeRight    ?? "L", new ScanCode(0x26, false));
                    _scanJump     = KeyHelper.GetScanCode(_config.TapStrafeJump     ?? "Y", new ScanCode(0x15, false));

                    _scanCodeW = KeyHelper.GetScanCode(_config.JoystickYPositive ?? "W", new ScanCode(0x11, false));
                    _scanCodeS = KeyHelper.GetScanCode(_config.JoystickYNegative ?? "S", new ScanCode(0x1F, false));
                    _scanCodeA = KeyHelper.GetScanCode(_config.JoystickXNegative ?? "A", new ScanCode(0x1E, false));
                    _scanCodeD = KeyHelper.GetScanCode(_config.JoystickXPositive ?? "D", new ScanCode(0x20, false));

                    lock (_mappingLock)
                    {
                        BuildMappingCache();
                    }

                    // Invalidate any macro iteration that observed the cleared state
                    // before the newly loaded bindings were installed.
                    Interlocked.Increment(ref _inputGeneration);
                }
                finally
                {
                    _isReloading = false;
                }
            }
        }

        private void BuildMappingCache()
        {
            if (_config == null) return;
            _keyPressActions.Clear();

            _mantleJumpKeyVk = KeyHelper.GetVirtualKeyCode(_config.DPadUp ?? "X");
            _superglideKeyVk = KeyHelper.GetVirtualKeyCode(_config.DPadDown ?? "V");
            AddMapping(_config.DPadLeft, isDown => SetControllerButton(Xbox360Button.Left, isDown));
            AddMapping(_config.DPadRight, isDown => SetControllerButton(Xbox360Button.Right, isDown));
            AddMapping(_config.Guide, isDown => SetControllerButton(Xbox360Button.Guide, isDown));
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
            bool traceInput = InputTrace.IsCapturing && IsConfiguredInput(vkCode);
            if (traceInput)
            {
                string edge = isDown ? (_physicalKeyDowns.ContainsKey(vkCode) ? "repeat" : "down") : "up";
                InputTrace.Record("Input", $"{KeyHelper.GetInputName(vkCode)} {edge}");
            }

            bool handled = HandlePhysicalKey(vkCode, isDown);
            if (traceInput && !handled)
                InputTrace.Record("Routing", "Passed through");
            return handled;
        }

        private bool IsConfiguredInput(int vkCode)
        {
            if (vkCode == _toggleKeyVk || vkCode == _jumpKeyVk || vkCode == _tapStrafeTriggerVk ||
                vkCode == _mantleJumpKeyVk || vkCode == _superglideKeyVk || vkCode == _strafeKeyVk ||
                vkCode == _backVk || vkCode == _moveLeftVk || vkCode == _moveRightVk)
                return true;

            lock (_mappingLock)
                return _keyPressActions.ContainsKey(vkCode);
        }

        private bool HandlePhysicalKey(int vkCode, bool isDown)
        {
            if (isDown)
            {
                if (_physicalKeyDowns.TryGetValue(vkCode, out long? originalGeneration))
                    return originalGeneration.HasValue;

                bool handled = HandleMappedKeyEvent(vkCode, true);
                _physicalKeyDowns[vkCode] = handled ? Interlocked.Read(ref _inputGeneration) : null;
                return handled;
            }

            if (!_physicalKeyDowns.Remove(vkCode, out long? generation) || !generation.HasValue)
                return false;

            if (generation.Value == Interlocked.Read(ref _inputGeneration))
                HandleMappedKeyEvent(vkCode, false);
            return true;
        }

        private bool HandleMappedKeyEvent(int vkCode, bool isDown)
        {
            if (IsBindingMode || !IsViGEmConnected || _config == null) return false;

            if (vkCode == _toggleKeyVk)
            {
                if (isDown)
                {
                    KeyLogger.Log($"PHYSICAL: DOWN - VK: {vkCode}");
                    _isEnabled = !_isEnabled;
                    if (!_isEnabled) ClearVirtualInputs();
                    OnToggleChanged?.Invoke(IsEnabled);
                }
                return true;
            }

            if (!_isEnabled) return false;

            // Superglide needs crouch and jump separated by one configured frame.
            if (vkCode == _superglideKeyVk)
            {
                if (isDown) StartSuperglide(_config.SuperglideFps);
                return true;
            }

            if (vkCode == _moveLeftVk || vkCode == _moveRightVk)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                UpdateSnapTap(vkCode, isDown);
                // Pair tap-strafe's synthetic hold with the physical release so the key cannot stick.
                if (!isDown && RemoveSyntheticActiveKey(vkCode))
                {
                    ScanCode scanCode = (vkCode == _moveLeftVk) ? _scanCodeA : _scanCodeD;
                    SendScanCode(scanCode, false, force: true);
                    KeyLogger.Log($"SYNTHETIC KEYUP: VK={vkCode} Scan=0x{scanCode.Code:X2}");
                }
                return true;
            }

            if (_config.IsStrafeEnabled && vkCode == _tapStrafeTriggerVk && _tapStrafeTriggerVk != _jumpKeyVk)
            {
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
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                if (_tapStrafeTriggerVk == _jumpKeyVk)
                {
                    _isTapStrafeTriggerPressed = isDown;
                }

                if (isDown && _config.IsStrafeToggleMode && _tapStrafeTriggerVk == _jumpKeyVk)
                {
                    _isStrafeToggledActive = !_isStrafeToggledActive;
                    KeyLogger.Log($"TAP STRAFE TOGGLE MODE: Active={_isStrafeToggledActive}");
                }

                HandleJump(isDown, _config.IsJumpSpamEnabled);
                UpdateMovementOutput();
                return true;
            }

            if (vkCode == _strafeKeyVk)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isStrafeKeyPressed = isDown;
                UpdateSnapTap(vkCode, isDown);
                if (!isDown && RemoveSyntheticActiveKey(vkCode))
                {
                    SendScanCode(_scanCodeW, false, force: true);
                    KeyLogger.Log($"SYNTHETIC KEYUP: W VK={vkCode}");
                }
                return true;
            }

            if (vkCode == _backVk)
            {
                KeyLogger.Log($"PHYSICAL: {(isDown ? "DOWN" : "UP")} - VK: {vkCode}");
                _isBackKeyPressed = isDown;
                UpdateSnapTap(vkCode, isDown);
                if (!isDown && RemoveSyntheticActiveKey(vkCode))
                {
                    SendScanCode(_scanCodeS, false, force: true);
                    KeyLogger.Log($"SYNTHETIC KEYUP: S VK={vkCode}");
                }
                return true;
            }

            if (vkCode == _mantleJumpKeyVk)
            {
                if (!_config.UseAppMantleJumpTiming)
                    SetControllerButton(Xbox360Button.Up, isDown);
                else if (isDown)
                    StartMantleJump(_config.MantleJumpFps);
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

        private void StartSuperglide(int configuredFps)
        {
            long generation;
            long target;
            lock (_lifecycleLock)
            {
                if (_isSuperglideRunning || _isMantleJumpRunning) return;
                _isSuperglideRunning = true;
                generation = Interlocked.Read(ref _inputGeneration);
                SetControllerButton(Xbox360Button.Down, true);
                target = _loopTimer.ElapsedTicks + Stopwatch.Frequency / Math.Max(30, configuredFps);
            }

            Task.Run(() =>
            {
                if (!RunTechniqueStep(generation, target, () =>
                {
                    SetControllerButton(Xbox360Button.Down, false);
                    SetJumpOutput(true, generation, technique: true);
                })) return;

                Thread.Sleep(16);
                RunForGeneration(generation, () =>
                {
                    SetJumpOutput(false, generation, technique: true);
                    _isSuperglideRunning = false;
                });
                KeyLogger.Log("SUPERGLIDE: Jump fired");
            });
        }

        private void StartMantleJump(int configuredFps)
        {
            long generation;
            long start;
            long fps = Math.Max(MappingConfig.MinimumMantleJumpFps, configuredFps);
            long frameTicks = Math.Max(1, Stopwatch.Frequency / fps);
            // Preserve the Steam layout's 5 ms and 51 ms offsets at the 144 FPS baseline.
            long cancelOffset = Stopwatch.Frequency * 5 * 144 / (1000 * fps);
            long interactOffset = Stopwatch.Frequency * 51 * 144 / (1000 * fps);
            lock (_lifecycleLock)
            {
                if (_isSuperglideRunning || _isMantleJumpRunning) return;
                generation = Interlocked.Read(ref _inputGeneration);
                if (_lootState)
                {
                    SendVirtualKey(VK_E, SCAN_E, false, generation);
                    _lootState = false;
                }
                lock (_scanCodeLock)
                {
                    SetTapStrafeDirections(false, false, false, false, generation);
                    _isMantleJumpRunning = true;
                    SendScanCode(_scanForward, true, expectedGeneration: generation);
                    start = _loopTimer.ElapsedTicks;
                }
            }

            Task.Run(() =>
            {
                if (!RunTechniqueStep(generation, start + cancelOffset, () =>
                {
                    SendScanCode(_scanForward, false, expectedGeneration: generation);
                    SendScanCode(_scanBackward, true, expectedGeneration: generation);
                })) return;

                if (!RunTechniqueStep(generation, _loopTimer.ElapsedTicks + frameTicks,
                    () => SendScanCode(_scanBackward, false, expectedGeneration: generation))) return;

                if (!RunTechniqueStep(generation, start + interactOffset, () =>
                {
                    SendScanCode(SCAN_E, true, expectedGeneration: generation);
                    SetJumpOutput(true, generation, technique: true);
                })) return;

                if (!RunTechniqueStep(generation, _loopTimer.ElapsedTicks + frameTicks, () =>
                {
                    SendScanCode(SCAN_E, false, expectedGeneration: generation);
                    SetJumpOutput(false, generation, technique: true);
                })) return;

                if (!RunTechniqueStep(generation, _loopTimer.ElapsedTicks + frameTicks,
                    () => SetJumpOutput(true, generation, technique: true))) return;

                RunTechniqueStep(generation, _loopTimer.ElapsedTicks + frameTicks, () =>
                {
                    SetJumpOutput(false, generation, technique: true);
                    _isMantleJumpRunning = false;
                });
            });
        }

        private bool RunTechniqueStep(long generation, long target, Action action)
        {
            // Count from the preceding input so scheduling the worker does not add a full delay.
            long remainingTicks = target - _loopTimer.ElapsedTicks;
            if (remainingTicks > 0)
                Thread.Sleep((int)(800.0 * remainingTicks / Stopwatch.Frequency));
            while (Interlocked.Read(ref _inputGeneration) == generation && _loopTimer.ElapsedTicks < target)
                Thread.SpinWait(10);

            return RunForGeneration(generation, action);
        }

        private void HandleJump(bool isDown, bool spamEnabled)
        {
            lock (_lifecycleLock)
            {
                _isJumpKeyPressed = isDown;
                long pressId = ++_jumpPressId;
                long generation = Interlocked.Read(ref _inputGeneration);

                if (!isDown)
                {
                    _jumpState = false;
                    _nextJumpToggleTicks = 0;
                    SetJumpOutput(false, generation);
                }
                else if (!spamEnabled)
                {
                    SetJumpOutput(true, generation);
                    _ = ReleaseJumpPulseAsync(generation, pressId);
                }
            }
        }

        private async Task ReleaseJumpPulseAsync(long generation, long pressId)
        {
            await Task.Delay(30).ConfigureAwait(false);
            RunForGeneration(generation, () =>
            {
                if (pressId == _jumpPressId)
                    SetJumpOutput(false, generation);
            });
        }

        private void SetJumpOutput(bool isDown, long generation, bool technique = false)
        {
            lock (_scanCodeLock)
            {
                if (_isReloading || generation != Interlocked.Read(ref _inputGeneration)) return;

                // Steam Input turbos Left Bumper. Emit only the configured keyboard Jump.
                if (technique) _techniqueJumpDown = isDown;
                else _regularJumpDown = isDown;
                SendScanCode(_scanJump, _regularJumpDown || _techniqueJumpDown);
            }
        }

        private void SetTapStrafeDirections(bool forward, bool backward, bool left, bool right, long generation)
        {
            lock (_scanCodeLock)
            {
                if (_isMantleJumpRunning) return;
                SendScanCode(_scanForward, forward, expectedGeneration: generation);
                SendScanCode(_scanBackward, backward, expectedGeneration: generation);
                SendScanCode(_scanLeft, left, expectedGeneration: generation);
                SendScanCode(_scanRight, right, expectedGeneration: generation);
            }
        }

        private void UpdateSnapTap(int vkCode, bool isDown)
        {
            string horizontalStack;
            string verticalStack;
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

                horizontalStack = string.Join(",", _horizontalStack);
                verticalStack = string.Join(",", _verticalStack);
            }

            KeyLogger.Log($"SNAP TAP STACK: hStack=[{horizontalStack}] vStack=[{verticalStack}]");
            UpdateMovementOutput();
        }

        private bool RemoveSyntheticActiveKey(int vkCode)
        {
            lock (_stackLock)
                return _syntheticActiveVks.Remove(vkCode);
        }

        private bool IsMacroActive()
        {
            if (_config?.IsStrafeEnabled != true) return false;

            bool isTriggerActive = _config.IsStrafeToggleMode ? _isStrafeToggledActive : _isTapStrafeTriggerPressed;
            bool hasHorizontalInput;
            lock (_stackLock)
                hasHorizontalInput = _horizontalStack.Count > 0;
            return isTriggerActive && (_isStrafeKeyPressed || _isBackKeyPressed || hasHorizontalInput);
        }


        private void UpdateMovementOutput(bool forceRefresh = false, long? expectedGeneration = null)
        {
            bool shouldLog = false;
            short rawX = 0;
            short rawY = 0;
            short joyX = 0;
            short joyY = 0;
            bool ew = false, es = false, ea = false, ed = false;

            lock (_movementOutputLock)
            {
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

                rawY = (short)(ew ? 32767 : (es ? -32768 : 0));
                rawX = (short)(ed ? 32767 : (ea ? -32768 : 0));

                lock (_controllerLock)
                {
                    if (expectedGeneration.HasValue
                        && (_isReloading || expectedGeneration.Value != Interlocked.Read(ref _inputGeneration)))
                        return;

                    // Apex can stop reporting a static controller axis after keyboard or mouse input.
                    // Alternating by one unit keeps XInput movement active.
                    short dither = (short)((_macroCounter % 2 == 0) ? 0 : 1);
                    _currentJoyY = rawY == 0 ? (short)0 : (short)(rawY > 0 ? rawY - dither : rawY + dither);
                    _currentJoyX = rawX == 0 ? (short)0 : (short)(rawX > 0 ? rawX - dither : rawX + dither);
                    joyX = _currentJoyX;
                    joyY = _currentJoyY;

                    shouldLog = forceRefresh || rawX != _lastLoggedJoyX || rawY != _lastLoggedJoyY;
                    if (shouldLog)
                    {
                        _lastLoggedJoyX = rawX;
                        _lastLoggedJoyY = rawY;
                    }

                    SetMovementAxes(_currentJoyX, _currentJoyY);
                }
            }

            if (shouldLog)
                KeyLogger.Log($"VIRTUAL INPUTS: JoyX={joyX} (raw={rawX}), JoyY={joyY} (raw={rawY}) | WASD=[W:{ew}, A:{ea}, S:{es}, D:{ed}] (forceRefresh={forceRefresh})");
        }

        private void SendScanCode(ScanCode scanCode, bool isDown, bool force = false, long? expectedGeneration = null)
        {
            lock (_scanCodeLock)
            {
                if (expectedGeneration.HasValue
                    && (_isReloading || expectedGeneration.Value != Interlocked.Read(ref _inputGeneration)))
                    return;

                if (!force)
                {
                    if (!_scanCodeStates.ContainsKey(scanCode)) _scanCodeStates[scanCode] = false;
                    if (_scanCodeStates[scanCode] == isDown) return;
                }

                _scanCodeStates[scanCode] = isDown;

                uint flags = KEYEVENTF_SCANCODE
                    | (scanCode.IsExtended ? KEYEVENTF_EXTENDEDKEY : 0)
                    | (isDown ? 0 : (uint)KEYEVENTF_KEYUP);
                keybd_event(0, scanCode.Code, flags, KeyboardHook.SYNTHETIC_MARKER);
                if (InputTrace.IsCapturing)
                    InputTrace.Record("Keyboard", $"{KeyHelper.GetScanCodeName(scanCode)} {(isDown ? "down" : "up")}");
            }

            KeyLogger.Log($"VIRTUAL KEYBOARD: {(isDown ? "DOWN" : "UP")} - SCAN: 0x{scanCode.Code:X2}{(scanCode.IsExtended ? " (extended)" : string.Empty)}");
        }

        private void SendVirtualKey(byte virtualKey, ScanCode scanCode, bool isDown, long? expectedGeneration = null)
        {
            lock (_lifecycleLock)
            {
                if (expectedGeneration.HasValue && expectedGeneration.Value != Interlocked.Read(ref _inputGeneration))
                    return;

                uint flags = isDown ? 0 : (uint)KEYEVENTF_KEYUP;
                keybd_event(virtualKey, scanCode.Code, flags, KeyboardHook.SYNTHETIC_MARKER);
                if (InputTrace.IsCapturing)
                    InputTrace.Record("Keyboard", $"{KeyHelper.GetInputName(virtualKey)} {(isDown ? "down" : "up")}");
            }
        }

        private bool RunForGeneration(long generation, Action action)
        {
            lock (_lifecycleLock)
            {
                if (generation != Interlocked.Read(ref _inputGeneration) || _isReloading || !IsEnabled || IsBindingMode)
                    return false;

                action();
                return true;
            }
        }

        private void SetControllerButton(Xbox360Button button, bool isDown)
        {
            _vigemService.SetButton(button, isDown);
            if (InputTrace.IsCapturing)
                InputTrace.Record("Controller", $"{button} {(isDown ? "down" : "up")}");
        }

        private void SetMovementAxes(short x, short y)
        {
            _vigemService.SetAxis(Xbox360Axis.LeftThumbX, x);
            _vigemService.SetAxis(Xbox360Axis.LeftThumbY, y);
            if (InputTrace.IsCapturing)
                InputTrace.RecordStick(x, y);
        }

        private void ClearVirtualInputs()
        {
            lock (_lifecycleLock)
            {
                Interlocked.Increment(ref _inputGeneration);
                ClearVirtualInputsLocked();
            }
        }

        private void ClearVirtualInputsLocked()
        {
            _isStrafeKeyPressed = false;
            _isBackKeyPressed = false;
            _isJumpKeyPressed = false;
            _isSuperglideRunning = false;
            _isMantleJumpRunning = false;
            _isTapStrafeTriggerPressed = false;
            _isStrafeToggledActive = false;
            _isLootKeyPressed = false;
            _jumpState = false;
            _nextJumpToggleTicks = 0;
            lock (_stackLock)
            {
                _horizontalStack.Clear();
                _verticalStack.Clear();
                _syntheticActiveVks.Clear();
            }

            SendScanCode(_scanForward, false);
            SendScanCode(_scanBackward, false);
            SendScanCode(_scanLeft, false);
            SendScanCode(_scanRight, false);
            lock (_scanCodeLock)
            {
                _regularJumpDown = false;
                _techniqueJumpDown = false;
                SendScanCode(_scanJump, false);
            }
            SendScanCode(SCAN_E, false);

            lock (_controllerLock)
            {
                _currentJoyX = 0;
                _currentJoyY = 0;
                SetMovementAxes(0, 0);
                SetControllerButton(Xbox360Button.Up, false);
                SetControllerButton(Xbox360Button.Down, false);
                SetControllerButton(Xbox360Button.Left, false);
                SetControllerButton(Xbox360Button.Right, false);
                SetControllerButton(Xbox360Button.Guide, false);
            }
            if (_lootState) SendVirtualKey(VK_E, SCAN_E, false);
            if (_inspectState) SendVirtualKey(VK_N, SCAN_N, false);
            _lootState = false;
            _inspectState = false;
            _isInspectKeyPressed = false;
        }

        private void UpdateJumpSpam(long generation)
        {
            var config = _config;
            if (config?.IsJumpSpamEnabled != true || !_isJumpKeyPressed) return;

            RunForGeneration(generation, () =>
            {
                if (!_isJumpKeyPressed) return;

                int intervalMs = Math.Max(MappingConfig.MinimumAutoJumpIntervalMs, config.AutoJumpIntervalMs);
                long intervalTicks = Math.Max(1, Stopwatch.Frequency * intervalMs / 2000);
                long now = _loopTimer.ElapsedTicks;
                long nextToggle = _nextJumpToggleTicks == 0 ? now : _nextJumpToggleTicks;
                if (now < nextToggle) return;

                // Preserve the edge cadence even if one worker iteration runs late.
                long transitionsDue = 1 + (now - nextToggle) / intervalTicks;
                if ((transitionsDue & 1) != 0) _jumpState = !_jumpState;
                SetJumpOutput(_jumpState, generation);
                _nextJumpToggleTicks = nextToggle + transitionsDue * intervalTicks;
            });
        }

        private void MacroLoop()
        {
            long tapStrafeGeneration = -1;
            while (_isRunning)
            {
                long iterationGeneration = Interlocked.Read(ref _inputGeneration);
                if (IsEnabled && !IsBindingMode && !_isReloading && _config != null)
                {
                    _macroCounter++;
                    UpdateJumpSpam(iterationGeneration);

                    // Refresh held movement every 15 ms because hybrid-input events can stall a static stick report.
                    bool hasMovementKeys;
                    lock (_stackLock)
                    {
                        hasMovementKeys = _horizontalStack.Count > 0 || _verticalStack.Count > 0;
                    }

                    if (_macroCounter % 5 == 0 && hasMovementKeys)
                    {
                        UpdateMovementOutput(expectedGeneration: iterationGeneration);
                    }

                    if (IsMacroActive())
                    {
                        if (tapStrafeGeneration != iterationGeneration)
                        {
                            // Start with an ON pulse instead of waiting for the previous counter phase.
                            _tapStrafeCounter = 0;
                            tapStrafeGeneration = iterationGeneration;
                        }

                        _tapStrafeCounter++;

                        bool holdW, holdS, holdA, holdD;
                        int verticalCount, horizontalCount;
                        lock (_stackLock)
                        {
                            holdW = _verticalStack.Count > 0 && _verticalStack[_verticalStack.Count - 1] == _strafeKeyVk;
                            holdS = _verticalStack.Count > 0 && _verticalStack[_verticalStack.Count - 1] == _backVk;
                            holdA = _horizontalStack.Count > 0 && _horizontalStack[_horizontalStack.Count - 1] == _moveLeftVk;
                            holdD = _horizontalStack.Count > 0 && _horizontalStack[_horizontalStack.Count - 1] == _moveRightVk;
                            verticalCount = _verticalStack.Count;
                            horizontalCount = _horizontalStack.Count;
                        }

                        if (_macroCounter % 100 == 0)
                            KeyLogger.Log($"STRAFE STATE: W={holdW} S={holdS} A={holdA} D={holdD} | vStack={verticalCount} hStack={horizontalCount}");

                        // Pulse only directions. Auto Jump owns repeats of the jump key.
                        bool tapOn = (_tapStrafeCounter % 5 < 3);
                        SetTapStrafeDirections(tapOn && holdW, tapOn && holdS, tapOn && holdA, tapOn && holdD,
                            iterationGeneration);
                    }
                    else
                    {
                        if (tapStrafeGeneration == iterationGeneration)
                        {
                            tapStrafeGeneration = -1;
                            SetTapStrafeDirections(false, false, false, false, iterationGeneration);

                            // End synthetic holds before restoring controller movement.
                            lock (_stackLock)
                            {
                                foreach (int vk in _syntheticActiveVks)
                                {
                                    ScanCode scanCode;
                                    if (vk == _moveLeftVk) scanCode = _scanCodeA;
                                    else if (vk == _moveRightVk) scanCode = _scanCodeD;
                                    else if (vk == _strafeKeyVk) scanCode = _scanCodeW;
                                    else scanCode = _scanCodeS;
                                    SendScanCode(scanCode, false, force: true, expectedGeneration: iterationGeneration);
                                }
                                _syntheticActiveVks.Clear();
                            }

                            // Briefly clear the stick to avoid restoring a stale mixed-input state.
                            RunForGeneration(iterationGeneration, () =>
                            {
                                lock (_controllerLock)
                                {
                                    SetMovementAxes(0, 0);
                                }
                            });
                            Thread.Sleep(10);
                            UpdateMovementOutput(forceRefresh: true, expectedGeneration: iterationGeneration);

                            KeyLogger.Log("TAP STRAFE ENDED: All lurch keys released.");
                        }

                    }

                    // Toggle fast loot every 15 ms for a 30 ms key cycle.
                    if (_macroCounter % 5 == 0)
                    {
                        if (_isLootKeyPressed)
                        {
                            RunForGeneration(iterationGeneration, () =>
                            {
                                if (_isMantleJumpRunning) return;
                                _lootState = !_lootState;
                                SendVirtualKey(VK_E, SCAN_E, _lootState, iterationGeneration);
                            });
                        }
                        else if (_lootState)
                        {
                            RunForGeneration(iterationGeneration, () =>
                            {
                                _lootState = false;
                                SendVirtualKey(VK_E, SCAN_E, false, iterationGeneration);
                            });
                        }

                    }

                    if (_isInspectKeyPressed)
                    {
                        long currentMs = _loopTimer.ElapsedMilliseconds;
                        int delayMs = Math.Max(5, _config.InspectDelayMs);
                        if (currentMs - _lastInspectToggleMs >= delayMs)
                        {
                            RunForGeneration(iterationGeneration, () =>
                            {
                                _lastInspectToggleMs = currentMs;
                                _inspectState = !_inspectState;
                                SendVirtualKey(VK_N, SCAN_N, _inspectState, iterationGeneration);
                            });
                        }
                    }
                    else if (_inspectState)
                    {
                        RunForGeneration(iterationGeneration, () =>
                        {
                            _inspectState = false;
                            SendVirtualKey(VK_N, SCAN_N, false, iterationGeneration);
                        });
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
            _isRunning = false;
            _macroThread.Join();
            ClearVirtualInputs();
            timeEndPeriod(1);
            if (_hrTimer != IntPtr.Zero && _hrTimer != new IntPtr(-1))
                CloseHandle(_hrTimer);
            _keyboardHook.Dispose();
            _mouseHook.Dispose();
            _vigemService.Dispose();
        }
    }
}
