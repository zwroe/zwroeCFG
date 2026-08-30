using System;
using System.IO;
using System.Text.Json;
using Rebind.Core.Models;

namespace Rebind.Services
{
    /// <summary>
    /// Manages loading and saving user keybind configurations to disk.
    /// Serializes and deserializes the MappingConfig class to mappingConfig.json.
    /// </summary>
    public class ConfigManager
    {
        private const string ConfigFileName = "mappingConfig.json";
        private static readonly string ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Rebind");
        private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, ConfigFileName);
        private static readonly string LegacyConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

        /// <summary>
        /// Loads the configuration from mappingConfig.json.
        /// If the file does not exist, creates a default configuration and saves it.
        /// </summary>
        public MappingConfig LoadConfig()
        {
            EnsureConfigDirectory();
            TryMigrateLegacyConfig();

            if (!File.Exists(ConfigFilePath))
            {
                var defaultConfig = new MappingConfig();
                SaveConfig(defaultConfig);
                return defaultConfig;
            }

            try
            {
                string json = File.ReadAllText(ConfigFilePath);
                bool hasFastLootKey = HasJsonProperty(json, nameof(MappingConfig.FastLootKey));
                MappingConfig config = JsonSerializer.Deserialize<MappingConfig>(json) ?? new MappingConfig();

                NormalizeConfig(config, hasFastLootKey);
                if (!hasFastLootKey)
                    SaveConfig(config);

                return config;
            }
            catch
            {
                var defaultConfig = new MappingConfig();
                SaveConfig(defaultConfig);
                return defaultConfig;
            }
        }

        /// <summary>
        /// Saves the given configuration object to mappingConfig.json as formatted JSON.
        /// </summary>
        public void SaveConfig(MappingConfig config)
        {
            try
            {
                EnsureConfigDirectory();
                NormalizeConfig(config, hasFastLootKey: true);

                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }

        private static void EnsureConfigDirectory()
        {
            Directory.CreateDirectory(ConfigDirectory);
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
