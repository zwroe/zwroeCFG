using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rebind.Core.Models;
using Rebind.Helpers;
using Rebind.Services;

namespace Rebind.Views
{
    public partial class MappingPageV2 : Page
    {
        private ConfigManager _configManager;
        private MappingConfig _config;
        private Button? _activeBindButton;
        private InputTestWindow? _inputTestWindow;
        private string? _previousBindValue;
        private bool _isLoadingConfig = true;
        private string? _configSaveError;
        private readonly DispatcherTimer _saveStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        public MappingPageV2()
        {
            InitializeComponent();
            
            // Reuse the app's manager so the UI and engine share the same config state.
            _configManager = App.ConfigManager ?? new ConfigManager();
            _config = _configManager.LoadConfig();
            LoadConfigToUI();
            _isLoadingConfig = false;

            _saveStatusTimer.Tick += (_, _) =>
            {
                _saveStatusTimer.Stop();
                UpdateStatusText(App.KeyMapper?.IsEnabled ?? true);
            };

            this.PreviewKeyDown += MappingPageV2_PreviewKeyDown;
            this.PreviewMouseDown += MappingPageV2_PreviewMouseDown;

            if (App.KeyMapper != null)
            {
                App.KeyMapper.OnToggleChanged += UpdateStatusText;
                UpdateStatusText(App.KeyMapper.IsEnabled);
            }
        }

        private void UpdateStatusText(bool isEnabled)
        {
            this.Dispatcher.Invoke(() => {
                if (_configSaveError != null)
                {
                    StatusText.Text = "CONFIG SAVE FAILED";
                    StatusText.ToolTip = _configSaveError;
                    StatusText.Foreground = Brushes.OrangeRed;
                    return;
                }

                StatusText.Text = isEnabled ? "ENABLED" : "DISABLED";
                StatusText.ToolTip = null;
                StatusText.Foreground = new SolidColorBrush(isEnabled ? 
                    (Color)ColorConverter.ConvertFromString("#00CC66") : 
                    (Color)ColorConverter.ConvertFromString("#666666"));
            });
        }

        private void LoadConfigToUI()
        {
            btnForward.Content = _config.JoystickYPositive;
            btnBackward.Content = _config.JoystickYNegative;
            btnLeft.Content = _config.JoystickXNegative;
            btnRight.Content = _config.JoystickXPositive;
            btnJump.Content = _config.LeftBumper;
            btnToggle.Content = _config.ToggleShortcut;
            
            btnDPadUp.Content = _config.DPadUp;
            btnDPadDown.Content = _config.DPadDown;
            btnDPadRight.Content = _config.FastLootKey;
            btnInspect.Content = _config.InspectKey;
            btnTapStrafeKey.Content = _config.TapStrafeKey ?? "Space";
            
            togStrafe.IsChecked = _config.IsStrafeEnabled;
            togJump.IsChecked = _config.IsJumpSpamEnabled;
            togMantleTiming.IsChecked = _config.UseAppMantleJumpTiming;
            if (_config.IsStrafeToggleMode)
                radStrafeToggle.IsChecked = true;
            else
                radStrafeHold.IsChecked = true;

            txtAutoJumpIntervalMs.Text = _config.AutoJumpIntervalMs.ToString();
            txtMantleJumpFps.Text = _config.MantleJumpFps.ToString();
            txtSuperglideFps.Text = _config.SuperglideFps.ToString();
            txtInspectDelayMs.Text = _config.InspectDelayMs.ToString();
        }

        private bool SaveConfigFromUI()
        {
            _config.JoystickYPositive = btnForward.Content?.ToString();
            _config.JoystickYNegative = btnBackward.Content?.ToString();
            _config.JoystickXNegative = btnLeft.Content?.ToString();
            _config.JoystickXPositive = btnRight.Content?.ToString();
            _config.LeftBumper = btnJump.Content?.ToString();
            _config.ToggleShortcut = btnToggle.Content?.ToString();
            
            _config.DPadUp = btnDPadUp.Content?.ToString();
            _config.DPadDown = btnDPadDown.Content?.ToString();
            _config.FastLootKey = btnDPadRight.Content?.ToString();
            _config.InspectKey = btnInspect.Content?.ToString();
            _config.TapStrafeKey = btnTapStrafeKey.Content?.ToString();
            
            _config.IsStrafeEnabled = togStrafe.IsChecked ?? false;
            _config.IsStrafeToggleMode = radStrafeToggle.IsChecked ?? false;
            _config.IsJumpSpamEnabled = togJump.IsChecked ?? false;
            _config.UseAppMantleJumpTiming = togMantleTiming.IsChecked ?? false;
            if (int.TryParse(txtAutoJumpIntervalMs.Text, out int jumpInterval) && jumpInterval >= MappingConfig.MinimumAutoJumpIntervalMs)
                _config.AutoJumpIntervalMs = jumpInterval;
            if (int.TryParse(txtMantleJumpFps.Text, out int mantleFps) && mantleFps >= MappingConfig.MinimumMantleJumpFps)
                _config.MantleJumpFps = mantleFps;
            if (int.TryParse(txtSuperglideFps.Text, out int fps) && fps >= 30)
                _config.SuperglideFps = fps;
            if (int.TryParse(txtInspectDelayMs.Text, out int delayMs) && delayMs >= 5)
                _config.InspectDelayMs = delayMs;

            return PersistConfig();
        }

        private bool PersistConfig()
        {
            if (!_configManager.TrySaveConfig(_config, out var error))
            {
                _saveStatusTimer.Stop();
                _configSaveError = error ?? "Unable to write the configuration file.";
                UpdateStatusText(App.KeyMapper?.IsEnabled ?? true);
                return false;
            }

            if (_configSaveError != null)
            {
                _configSaveError = null;
                UpdateStatusText(App.KeyMapper?.IsEnabled ?? true);
            }

            App.KeyMapper?.ReloadConfig();
            return true;
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isLoadingConfig)
                SaveConfigFromUI();
        }

        private void AutoJumpIntervalMs_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_isLoadingConfig && int.TryParse(txtAutoJumpIntervalMs.Text, out int interval) && interval >= MappingConfig.MinimumAutoJumpIntervalMs)
            {
                _config.AutoJumpIntervalMs = interval;
                PersistConfig();
            }
        }

        private void MantleJumpFps_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_isLoadingConfig && int.TryParse(txtMantleJumpFps.Text, out int fps) && fps >= MappingConfig.MinimumMantleJumpFps)
            {
                _config.MantleJumpFps = fps;
                PersistConfig();
            }
        }

        private void SuperglideFps_Changed(object sender, TextChangedEventArgs e)
        {
            // TextChanged also fires for partial edits, so wait for a valid value.
            if (!_isLoadingConfig && int.TryParse(txtSuperglideFps.Text, out int fps) && fps >= 30)
            {
                _config.SuperglideFps = fps;
                PersistConfig();
            }
        }

        private void InspectDelayMs_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_isLoadingConfig && int.TryParse(txtInspectDelayMs.Text, out int delayMs) && delayMs >= 5)
            {
                _config.InspectDelayMs = delayMs;
                PersistConfig();
            }
        }

        private void BindButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeBindButton != null) CancelBind();

            _activeBindButton = sender as Button;
            if (_activeBindButton != null)
            {
                _previousBindValue = _activeBindButton.Content?.ToString();
                _activeBindButton.Tag = "Active";
                _activeBindButton.Content = "...";

                if (App.KeyMapper != null) App.KeyMapper.IsBindingMode = true;

                Keyboard.Focus(this);
            }
        }

        private void CancelBind()
        {
            if (_activeBindButton != null)
            {
                _activeBindButton.Content = _previousBindValue;
                _activeBindButton.Tag = null;
                _activeBindButton = null;
                _previousBindValue = null;
            }
            if (App.KeyMapper != null) App.KeyMapper.IsBindingMode = false;
        }

        private void MappingPageV2_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_activeBindButton != null)
            {
                e.Handled = true;

                Key targetKey = e.Key == Key.System ? e.SystemKey : e.Key;

                if (targetKey == Key.Escape)
                {
                    CancelBind();
                    return;
                }

                string keyStr = KeyHelper.GetConfigKeyName(targetKey);
                CommitBind(keyStr);
            }
        }

        private void MappingPageV2_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_activeBindButton != null)
            {
                string? mouseStr = e.ChangedButton switch
                {
                    MouseButton.Middle   => "Mouse3",
                    MouseButton.XButton1 => "Mouse4",
                    MouseButton.XButton2 => "Mouse5",
                    _ => null
                };

                if (mouseStr != null)
                {
                    e.Handled = true;
                    CommitBind(mouseStr);
                }
                else if (e.OriginalSource is not Button b || b != _activeBindButton)
                {
                    CancelBind();
                }
            }
        }

        private void CommitBind(string keyStr)
        {
            _activeBindButton!.Content = keyStr;
            _activeBindButton.Tag = null;
            _activeBindButton = null;
            _previousBindValue = null;

            if (App.KeyMapper != null) App.KeyMapper.IsBindingMode = false;

            SaveConfigFromUI();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveConfigFromUI())
                return;

            StatusText.Text = "CONFIG SAVED";
            _saveStatusTimer.Stop();
            _saveStatusTimer.Start();
        }

        private void InputTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.KeyMapper == null) return;
            if (_inputTestWindow != null)
            {
                _inputTestWindow.Activate();
                return;
            }

            _inputTestWindow = new InputTestWindow(App.KeyMapper.InputTrace) { Owner = Window.GetWindow(this) };
            _inputTestWindow.Closed += (_, _) => _inputTestWindow = null;
            _inputTestWindow.Show();
        }

        private void ViewLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logPath = KeyLogger.LogPath;
                if (!System.IO.File.Exists(logPath))
                {
                    string? dir = System.IO.Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] Movement debug log initialized.\n");
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open log file: {ex.Message}", "Log Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
