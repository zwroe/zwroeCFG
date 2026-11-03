using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rebind.Views;
using Rebind.Services;

namespace Rebind
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            
            MainFrame.Navigate(new MappingPageV2());

            CheckViGEmStatus();

            if (App.KeyMapper != null)
            {
                UpdateStatusUI(App.KeyMapper.IsEnabled);
                App.KeyMapper.OnToggleChanged += UpdateStatusUI;
            }
        }

        private void CheckViGEmStatus()
        {
            if (App.KeyMapper == null) return;

            var warnings = new List<string>();
            var details = new List<string>();

            if (!App.KeyMapper.IsViGEmConnected)
            {
                warnings.Add("ViGEm unavailable");
                details.Add("ViGEmBus driver is not installed or failed to initialize. Controller inputs won't work until installed.");
            }

            if (!string.IsNullOrWhiteSpace(App.KeyMapper.InputHookErrorMessage))
            {
                warnings.Add("Input hook unavailable");
                details.Add(App.KeyMapper.InputHookErrorMessage);
            }

            if (warnings.Count > 0)
            {
                SystemWarningText.Text = "⚠️ " + string.Join(" | ", warnings);
                SystemWarningText.ToolTip = string.Join(Environment.NewLine, details);
                SystemWarningText.Visibility = Visibility.Visible;
            }
        }

        private void UpdateStatusUI(bool isEnabled)
        {
            this.Dispatcher.Invoke(() => {
                StatusDot.Fill = new SolidColorBrush(isEnabled ? 
                    (Color)ColorConverter.ConvertFromString("#00CC66") : 
                    (Color)ColorConverter.ConvertFromString("#FF4B4B"));
            });
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
