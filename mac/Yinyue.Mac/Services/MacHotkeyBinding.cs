using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// A parsed global hotkey on macOS — the counterpart to <c>HotkeyBinding</c> in win/,
    /// which parses onto WPF's <c>Key</c> enum and therefore cannot be shared.
    ///
    /// <b>The strings are the contract.</b> Both apps read the same `config.json`, so the
    /// vocabulary here must match the Windows one exactly: "Ctrl+Alt+Space", "Ctrl+Alt+Plus",
    /// "Ctrl+Alt+Pipe". Only the mapping to a platform key code differs. Punctuation is
    /// written as words because "+" separates the parts — "Ctrl+Alt++" could not be parsed.
    /// </summary>
    public readonly record struct MacHotkeyBinding(uint Modifiers, uint KeyCode, string Text)
    {
        // Carbon modifier masks, from Events.h. Not the same values as Win32's.
        public const uint CmdKey = 0x0100;
        public const uint ShiftKey = 0x0200;
        public const uint OptionKey = 0x0800;
        public const uint ControlKey = 0x1000;

        public bool IsValid => Modifiers != 0;

        /// <summary>
        /// Virtual key codes from HIToolbox's Events.h. These are positional — kVK_ANSI_A is
        /// the key where A sits on a US layout — so they are layout-independent in the same
        /// way Win32 virtual key codes are.
        /// </summary>
        private static readonly Dictionary<string, uint> Keys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = 0x00, ["S"] = 0x01, ["D"] = 0x02, ["F"] = 0x03, ["H"] = 0x04,
            ["G"] = 0x05, ["Z"] = 0x06, ["X"] = 0x07, ["C"] = 0x08, ["V"] = 0x09,
            ["B"] = 0x0B, ["Q"] = 0x0C, ["W"] = 0x0D, ["E"] = 0x0E, ["R"] = 0x0F,
            ["Y"] = 0x10, ["T"] = 0x11, ["O"] = 0x1F, ["U"] = 0x20, ["I"] = 0x22,
            ["P"] = 0x23, ["L"] = 0x25, ["J"] = 0x26, ["K"] = 0x28, ["N"] = 0x2D,
            ["M"] = 0x2E,

            ["1"] = 0x12, ["2"] = 0x13, ["3"] = 0x14, ["4"] = 0x15, ["5"] = 0x17,
            ["6"] = 0x16, ["7"] = 0x1A, ["8"] = 0x1C, ["9"] = 0x19, ["0"] = 0x1D,

            ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76,
            ["F5"] = 0x60, ["F6"] = 0x61, ["F7"] = 0x62, ["F8"] = 0x64,
            ["F9"] = 0x65, ["F10"] = 0x6D, ["F11"] = 0x67, ["F12"] = 0x6F,

            ["Space"] = 0x31,
            ["Return"] = 0x24, ["Enter"] = 0x24,
            ["Tab"] = 0x30,
            ["Escape"] = 0x35, ["Esc"] = 0x35,
            ["Delete"] = 0x75,          // forward delete, matching Windows' Delete
            ["Backspace"] = 0x33,       // Windows calls this Back; the config says Backspace
            ["Left"] = 0x7B, ["Right"] = 0x7C, ["Down"] = 0x7D, ["Up"] = 0x7E,
            ["Home"] = 0x73, ["End"] = 0x77,
            ["PageUp"] = 0x74, ["PageDown"] = 0x79,

            // Punctuation, written as words for the reason above.
            ["Plus"] = 0x18,            // the =/+ key, as Windows' OemPlus is
            ["Minus"] = 0x1B,
            ["Pipe"] = 0x2A,            // the \| key, Windows' Oem5
            ["Backslash"] = 0x2A,
            ["Comma"] = 0x2B,
            ["Period"] = 0x2F,
            ["Slash"] = 0x2C,
            ["Tilde"] = 0x32,
            ["LeftBracket"] = 0x21,
            ["RightBracket"] = 0x1E,
            ["Semicolon"] = 0x29,
            ["Quote"] = 0x27,

            ["NumPadPlus"] = 0x45, ["NumPadMinus"] = 0x4E,
            ["NumPad0"] = 0x52, ["NumPad1"] = 0x53, ["NumPad2"] = 0x54,
            ["NumPad3"] = 0x55, ["NumPad4"] = 0x56, ["NumPad5"] = 0x57,
            ["NumPad6"] = 0x58, ["NumPad7"] = 0x59, ["NumPad8"] = 0x5B,
            ["NumPad9"] = 0x5C,
        };

        public static bool TryParse(string? text, out MacHotkeyBinding binding)
        {
            binding = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false;   // a bare key is not a global hotkey

            uint modifiers = 0;
            string? keyName = null;

            foreach (var part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl" or "control": modifiers |= ControlKey; break;
                    case "alt" or "option" or "opt": modifiers |= OptionKey; break;
                    case "shift": modifiers |= ShiftKey; break;

                    // "Win" is what the config calls it, because Windows wrote it first.
                    // Command is the right key to land on here: it is the same physical
                    // position and the same role as a modifier.
                    case "win" or "cmd" or "command": modifiers |= CmdKey; break;

                    default:
                        if (keyName is not null) return false;   // two keys, no
                        keyName = part;
                        break;
                }
            }

            if (keyName is null || modifiers == 0) return false;
            if (!Keys.TryGetValue(keyName, out uint code)) return false;

            binding = new MacHotkeyBinding(modifiers, code, text.Trim());
            return true;
        }

        public static MacHotkeyBinding ParseOrDefault(string? text, string fallback) =>
            TryParse(text, out var b) ? b
            : TryParse(fallback, out var f) ? f
            : default;

        public override string ToString() => Text;
    }
}
