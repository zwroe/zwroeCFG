using System;
using System.Text.Json.Serialization;

namespace Rebind.Core.Models
{
    /// <summary>
    /// Represents the user's keybind configuration.
    /// Serialized to and from mappingConfig.json.
    /// </summary>
    public class MappingConfig
    {
        /// <summary>Shortcut key used to toggle the entire remapper engine on and off.</summary>
        public string? ToggleShortcut { get; set; } = "Insert";

        /// <summary>Keyboard key mapped to the controller's DPad Up (Mantle Jump).</summary>
        public string? DPadUp { get; set; } = "X";
        /// <summary>Keyboard key mapped to the controller's DPad Down (Superglide).</summary>
        public string? DPadDown { get; set; } = "V";
        /// <summary>Your in-game FPS cap. Used to calculate the exact 1-frame window for superglide timing.</summary>
        public int SuperglideFps { get; set; } = 144;
        public string? DPadLeft { get; set; }
        /// <summary>Keyboard key mapped to the controller's DPad Right.</summary>
        public string? DPadRight { get; set; }
        public string? Guide { get; set; } = null;
        /// <summary>Keyboard key mapped to the controller's Left Bumper (Jump).</summary>
        public string? LeftBumper { get; set; } = "Space";

        // Directional movement mappings
        public string? JoystickXPositive { get; set; } = "D"; // Right
        public string? JoystickXNegative { get; set; } = "A"; // Left
        public string? JoystickYPositive { get; set; } = "W"; // Forward
        public string? JoystickYNegative { get; set; } = "S"; // Backward

        /// <summary>Keyboard key used for the fast-loot E spam helper.</summary>
        public string? FastLootKey { get; set; } = "B";

        /// <summary>Keyboard key used for the inspect-spam N helper.</summary>
        public string? InspectKey { get; set; } = "G";

        /// <summary>Delay in milliseconds between inspect key toggles when spamming inspect.</summary>
        public int InspectDelayMs { get; set; } = 30;

        /// <summary>Enables or disables the Tap Strafe (Lurch) macro engine.</summary>
        public bool IsStrafeEnabled { get; set; } = false;

        /// <summary>Trigger key bound to start tap strafe (defaults to Space).</summary>
        public string? TapStrafeKey { get; set; } = "Space";

        /// <summary>When true, tap strafe triggers in toggle mode instead of hold mode.</summary>
        public bool IsStrafeToggleMode { get; set; } = false;

        /// <summary>Enables or disables the optional continuous jump pulse.</summary>
        public bool IsJumpSpamEnabled { get; set; } = false;

        // ── Tap Strafe Output Keys ────────────────────────────────────────────────
        // These are the in-game keys the tap strafe engine SENDS to the game.
        // Must match whatever movement keys are bound inside the game.

        /// <summary>In-game key the tap strafe engine sends for Forward lurch.</summary>
        public string? TapStrafeForward { get; set; } = "I";
        /// <summary>In-game key the tap strafe engine sends for Backward lurch.</summary>
        public string? TapStrafeBackward { get; set; } = "K";
        /// <summary>In-game key the tap strafe engine sends for Left lurch.</summary>
        public string? TapStrafeLeft { get; set; } = "J";
        /// <summary>In-game key the tap strafe engine sends for Right lurch.</summary>
        public string? TapStrafeRight { get; set; } = "L";
        /// <summary>In-game key the tap strafe engine sends for Jump during tap strafe.</summary>
        public string? TapStrafeJump { get; set; } = "Y";
    }
}
