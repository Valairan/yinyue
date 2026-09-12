using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Yinyue.Models
{
    /// <summary>
    /// A parsed global hotkey, e.g. "Ctrl+Alt+Space". Round-trips through the string form
    /// stored in config.json, so the file stays human-editable.
    /// </summary>
    public readonly record struct HotkeyBinding(ModifierKeys Modifiers, Key Key)
    {
        // RegisterHotKey modifier flags.
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public bool IsValid => Key != Key.None && Modifiers != ModifierKeys.None;

        /// <summary>Win32 modifier flags. NOREPEAT stops a held key repeating the action.</summary>
        public uint Win32Modifiers
        {
            get
            {
                uint flags = MOD_NOREPEAT;
                if (Modifiers.HasFlag(ModifierKeys.Alt)) flags |= MOD_ALT;
                if (Modifiers.HasFlag(ModifierKeys.Control)) flags |= MOD_CONTROL;
                if (Modifiers.HasFlag(ModifierKeys.Shift)) flags |= MOD_SHIFT;
                if (Modifiers.HasFlag(ModifierKeys.Windows)) flags |= MOD_WIN;
                return flags;
            }
        }

        public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

        /// <summary>
        /// Readable names for punctuation keys, whose enum names ("OemPlus") are meaningless
        /// to anyone reading config.json or the settings dialog.
        ///
        /// Note these are words, not symbols: "+" is the separator between parts, so a
        /// binding written "Ctrl+Alt++" could not be parsed unambiguously.
        /// </summary>
        private static readonly Dictionary<Key, string> DisplayNames = new()
        {
            [Key.Oem5] = "Pipe",          // the \| key; enum name Oem5 means nothing to a user
            [Key.Back] = "Backspace",     // enum name is Back, which reads as 'go back'
            [Key.OemPlus] = "Plus",
            [Key.OemMinus] = "Minus",
            [Key.Add] = "NumPadPlus",
            [Key.Subtract] = "NumPadMinus",
            [Key.OemComma] = "Comma",
            [Key.OemPeriod] = "Period",
            [Key.OemQuestion] = "Slash",
            [Key.OemTilde] = "Tilde",
            [Key.OemOpenBrackets] = "LeftBracket",
            [Key.OemCloseBrackets] = "RightBracket",
            [Key.OemSemicolon] = "Semicolon",
            [Key.OemQuotes] = "Quote",
            [Key.OemBackslash] = "Backslash",
        };

        private static readonly Dictionary<string, Key> ParseNames =
            BuildParseNames();

        private static Dictionary<string, Key> BuildParseNames()
        {
            var map = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in DisplayNames)
                map[pair.Value] = pair.Key;

            // Aliases people reach for anyway.
            map["Equals"] = Key.OemPlus;
            map["Dash"] = Key.OemMinus;
            map["Hyphen"] = Key.OemMinus;
            map["Bar"] = Key.Oem5;
            map["VerticalBar"] = Key.Oem5;

            return map;
        }

        public override string ToString()
        {
            if (!IsValid) return string.Empty;

            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            parts.Add(DisplayNames.TryGetValue(Key, out string? name) ? name : Key.ToString());

            return string.Join("+", parts);
        }

        public static bool TryParse(string? text, out HotkeyBinding binding)
        {
            binding = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var modifiers = ModifierKeys.None;
            Key key = Key.None;

            foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();

                switch (part.ToLowerInvariant())
                {
                    case "ctrl" or "control": modifiers |= ModifierKeys.Control; continue;
                    case "alt": modifiers |= ModifierKeys.Alt; continue;
                    case "shift": modifiers |= ModifierKeys.Shift; continue;
                    case "win" or "windows": modifiers |= ModifierKeys.Windows; continue;
                }

                if (ParseNames.TryGetValue(part, out Key named))
                {
                    key = named;
                    continue;
                }

                if (!Enum.TryParse(part, ignoreCase: true, out Key parsed)) return false;
                key = parsed;
            }

            binding = new HotkeyBinding(modifiers, key);
            return binding.IsValid;
        }

        /// <summary>
        /// Parses, falling back to a default when the stored value is missing or malformed.
        /// A bad entry in config.json must not leave the user with no way to summon the app.
        /// </summary>
        public static HotkeyBinding ParseOrDefault(string? text, string fallback)
        {
            if (TryParse(text, out var binding)) return binding;
            TryParse(fallback, out binding);
            return binding;
        }

        /// <summary>True for keys that are only modifiers, which cannot stand alone.</summary>
        public static bool IsModifierKey(Key key) => key is
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin or
            Key.System;
    }
}
