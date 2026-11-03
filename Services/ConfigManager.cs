using System;
using System.Text.Json;
using System.IO;
using Rebind.Core.Models;

namespace Rebind.Services
{
    public class ConfigManager
    {
        private const string ConfigFileName = "mappingConfig.json";
        private static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Rebind");
        private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, ConfigFileName);
        private static readonly string LegacyConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

        public MappingConfig LoadConfig()
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                TryMigrateLegacyConfig();

                if (!File.Exists(ConfigFilePath))
                {
                    var defaultConfig = new MappingConfig();
                    TrySaveConfig(defaultConfig, out _);
                    return defaultConfig;
                }

                string json = File.ReadAllText(ConfigFilePath);
                MappingConfig config = JsonSerializer.Deserialize<MappingConfig>(json)
                    ?? throw new JsonException("Expected a configuration object.");
                bool hasFastLootKey = HasJsonProperty(json, nameof(MappingConfig.FastLootKey));

                NormalizeConfig(config, hasFastLootKey);
                if (!hasFastLootKey)
                    TrySaveConfig(config, out _);

                return config;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Invalid config JSON: {ex.Message}");
                var defaultConfig = new MappingConfig();
                // Preserve the user's file before creating defaults so a bad edit
                // or interrupted write does not silently destroy their settings.
                if (TryArchiveInvalidConfig())
                    TrySaveConfig(defaultConfig, out _);
                return defaultConfig;
            }
            catch (Exception ex)
            {
                // I/O and other failures should not overwrite a config we could
                // not safely read.
                Console.WriteLine($"Error loading config: {ex.Message}");
                return new MappingConfig();
            }
        }

        public bool TrySaveConfig(MappingConfig config, out string? error)
        {
            string temporaryPath = Path.Combine(ConfigDirectory, $"{ConfigFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                NormalizeConfig(config, hasFastLootKey: true);

                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, ConfigFilePath, overwrite: true);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                KeyLogger.Log($"Error saving config: {error}");
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return false;
            }
        }

        private static void TryMigrateLegacyConfig()
        {
            if (File.Exists(ConfigFilePath) || !File.Exists(LegacyConfigFilePath))
                return;

            try
            {
                File.Copy(LegacyConfigFilePath, ConfigFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error migrating config: {ex.Message}");
            }
        }

        private static bool TryArchiveInvalidConfig()
        {
            if (!File.Exists(ConfigFilePath))
                return true;

            string archivedPath = ConfigFilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            try
            {
                File.Move(ConfigFilePath, archivedPath);
                Console.WriteLine($"Invalid config preserved at: {archivedPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unable to preserve invalid config: {ex.Message}");
                return false;
            }
        }

        private static bool HasJsonProperty(string json, string propertyName)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out _);
        }

        private static void NormalizeConfig(MappingConfig config, bool hasFastLootKey)
        {
            config.ToggleShortcut = DefaultIfBlank(config.ToggleShortcut, "Insert");
            config.DPadUp = DefaultIfBlank(config.DPadUp, "X");
            config.DPadDown = DefaultIfBlank(config.DPadDown, "V");
            config.Guide = string.IsNullOrWhiteSpace(config.Guide) ? null : config.Guide.Trim();
            config.LeftBumper = DefaultIfBlank(config.LeftBumper, "Space");
            config.JoystickXPositive = DefaultIfBlank(config.JoystickXPositive, "D");
            config.JoystickXNegative = DefaultIfBlank(config.JoystickXNegative, "A");
            config.JoystickYPositive = DefaultIfBlank(config.JoystickYPositive, "W");
            config.JoystickYNegative = DefaultIfBlank(config.JoystickYNegative, "S");
            config.AutoJumpIntervalMs = Math.Max(MappingConfig.MinimumAutoJumpIntervalMs, config.AutoJumpIntervalMs);
            config.MantleJumpFps = Math.Max(MappingConfig.MinimumMantleJumpFps, config.MantleJumpFps);

            if (!hasFastLootKey && !string.IsNullOrWhiteSpace(config.DPadRight))
            {
                config.FastLootKey = config.DPadRight;
                config.DPadRight = null;
            }

            config.FastLootKey = DefaultIfBlank(config.FastLootKey, "B");
            config.InspectKey = DefaultIfBlank(config.InspectKey, "G");

            config.TapStrafeForward  = DefaultIfBlank(config.TapStrafeForward,  "I");
            config.TapStrafeBackward = DefaultIfBlank(config.TapStrafeBackward, "K");
            config.TapStrafeLeft     = DefaultIfBlank(config.TapStrafeLeft,     "J");
            config.TapStrafeRight    = DefaultIfBlank(config.TapStrafeRight,    "L");
            config.TapStrafeJump     = DefaultIfBlank(config.TapStrafeJump,     "Y");

            if (KeysMatch(config.DPadLeft, config.FastLootKey))
                config.DPadLeft = null;

            if (KeysMatch(config.DPadRight, config.FastLootKey))
                config.DPadRight = null;
        }

        private static string DefaultIfBlank(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static bool KeysMatch(string? first, string? second)
        {
            return !string.IsNullOrWhiteSpace(first)
                && !string.IsNullOrWhiteSpace(second)
                && string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
