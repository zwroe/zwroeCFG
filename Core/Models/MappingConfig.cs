using System;
using System.Text.Json.Serialization;

namespace Rebind.Core.Models
{
    public class MappingConfig
    {
        internal const int MinimumAutoJumpIntervalMs = 10;
        internal const int MinimumMantleJumpFps = 30;

        public string? ToggleShortcut { get; set; } = "Insert";

        public string? DPadUp { get; set; } = "X";
        public bool UseAppMantleJumpTiming { get; set; }
        public int MantleJumpFps { get; set; } = 144;
        public string? DPadDown { get; set; } = "V";
        public int SuperglideFps { get; set; } = 144;
        public string? DPadLeft { get; set; }
        public string? DPadRight { get; set; }
        public string? Guide { get; set; } = null;
        public string? LeftBumper { get; set; } = "Space";

        public string? JoystickXPositive { get; set; } = "D";
        public string? JoystickXNegative { get; set; } = "A";
        public string? JoystickYPositive { get; set; } = "W";
        public string? JoystickYNegative { get; set; } = "S";

        public string? FastLootKey { get; set; } = "B";
        public string? InspectKey { get; set; } = "G";
        public int InspectDelayMs { get; set; } = 30;
        public bool IsStrafeEnabled { get; set; } = false;
        public string? TapStrafeKey { get; set; } = "Space";
        public bool IsStrafeToggleMode { get; set; } = false;
        public bool IsJumpSpamEnabled { get; set; } = false;
        public int AutoJumpIntervalMs { get; set; } = 30;

        // These output keys must match the corresponding in-game bindings.
        public string? TapStrafeForward { get; set; } = "I";
        public string? TapStrafeBackward { get; set; } = "K";
        public string? TapStrafeLeft { get; set; } = "J";
        public string? TapStrafeRight { get; set; } = "L";
        public string? TapStrafeJump { get; set; } = "Y";
    }
}
