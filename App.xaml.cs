using System.Windows;
using Rebind.Services;

namespace Rebind
{
    public partial class App : Application
    {
        public static KeyMapperService? KeyMapper { get; private set; }

        /// <summary>
        /// The single shared ConfigManager instance — exposed so the UI page
        /// does not need to create a second one, preventing diverged state.
        /// </summary>
        public static ConfigManager? ConfigManager { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var configManager = new ConfigManager();
            var keyboardHook = new KeyboardHook();
            var mouseHook = new MouseHook();
            var vigemService = new ViGEmService();

            ConfigManager = configManager;
            KeyMapper = new KeyMapperService(configManager, keyboardHook, mouseHook, vigemService);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            KeyMapper?.Dispose();
            KeyMapper = null;
            ConfigManager = null;

            base.OnExit(e);
        }
    }
}
