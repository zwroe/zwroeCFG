using System;
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

            // Check ViGEm driver status and display warning if missing (Fix Bug #6)
            CheckViGEmStatus();

            // Subscribe to toggle events from the engine
            if (App.KeyMapper != null)
            {
                App.KeyMapper.OnToggleChanged += UpdateStatusUI;
            }
        }

        private void CheckViGEmStatus()
        {
            if (App.KeyMapper != null && !App.KeyMapper.IsViGEmConnected)
            {
                ViGEmWarningText.Visibility = Visibility.Visible;
                ViGEmWarningText.ToolTip = "ViGEmBus driver is not installed or failed to initialize. Controller inputs won't work until installed.";
            }
        }

        private void UpdateStatusUI(bool isEnabled)
        {
            // Update the dot in the title bar
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