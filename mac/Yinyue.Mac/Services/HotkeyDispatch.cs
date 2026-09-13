using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// When a shortcut acts: on press, or on release.
    ///
    /// Pulled out of <see cref="MacHotkeyManager"/> as plain functions because it is the part
    /// that is easy to get wrong and impossible to test through Carbon — a real key press
    /// cannot be synthesised without Accessibility. Getting it wrong once made every plain
    /// shortcut fire twice, which showed up as the overlay appearing only while its keys were
    /// held: it toggled on press and straight back off on release.
    /// </summary>
    public static class HotkeyDispatch
    {
        /// <summary>
        /// True when the tap and the hold mean different things, so the tap cannot be acted
        /// on until the key comes up and says which was meant.
        ///
        /// These are exactly the actions whose hold is already spoken for, which is what
        /// <see cref="HotkeyActions.SupportsHoldToggle"/> reports the inverse of.
        /// </summary>
        public static bool HasEscalation(string action) => !HotkeyActions.SupportsHoldToggle(action);

        /// <summary>
        /// Volume steps on press and keeps stepping while the key is down, like a keyboard's
        /// own repeat. Waiting for release would make a single tap feel late and holding do
        /// nothing at all.
        /// </summary>
        public static bool RepeatsWhileHeld(string action) =>
            action is HotkeyActions.VolumeUp or HotkeyActions.VolumeDown;

        /// <summary>
        /// Whether this shortcut waits for the key to come up before acting.
        ///
        /// Only two kinds do: one whose tap and hold differ, and one the user has set to
        /// hold-to-activate. Everything else acts immediately, and must NOT act again on
        /// release.
        /// </summary>
        public static bool FiresOnRelease(string action, bool requiresHold)
        {
            if (RepeatsWhileHeld(action)) return false;

            return HasEscalation(action) || requiresHold;
        }
    }
}
