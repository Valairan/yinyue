using Yinyue.Models;

namespace Yinyue.Tests;

/// <summary>
/// The half of the hotkey story that has no platform in it: the action vocabulary, the
/// defaults, the hold-delay clamping, and the migration that runs when a default moves.
///
/// Parsing ("Ctrl+Alt+Plus" into a key) and registration (does the OS accept it) both stay
/// in the Windows suite, because both are bound to a platform key model and a platform API.
/// A macOS shell will need its own copies of those, testing the same behaviours.
/// </summary>
public static class HotkeyConfigTests
{
    public static void Run()
    {
        Check.Group("hotkeys — configuration", () =>
        {
            var cfg = new HotkeyConfig();

            Check.That("every action has a default",
                HotkeyActions.All.All(a => !string.IsNullOrWhiteSpace(cfg.For(a).Keys)));

            var keys = HotkeyActions.All.Select(a => cfg.For(a).Keys).ToList();
            Check.Equal("no two actions share a shortcut", keys.Count, keys.Distinct().Count());

            Check.That("hold is off by default", HotkeyActions.All.All(a => !cfg.For(a).Hold));

            foreach (var action in new[]
                     {
                         HotkeyActions.AddToQueue, HotkeyActions.RemoveFromQueue, HotkeyActions.PlayPause,
                         HotkeyActions.RestartOrPrevious, HotkeyActions.ShuffleFavorites,
                     })
            {
                Check.That($"{action} withholds the generic hold toggle",
                    !HotkeyActions.SupportsHoldToggle(action));
                Check.That($"{action} explains its gesture",
                    !string.IsNullOrEmpty(HotkeyActions.HoldNote(action)));
            }

            Check.That("an absent entry falls back to the default",
                new HotkeyConfig { Bindings = new Dictionary<string, HotkeyBindingConfig>() }
                    .For(HotkeyActions.ToggleOverlay).Keys == HotkeyConfig.DefaultToggleOverlay);
        });

        Check.Group("hotkeys — hold delay", () =>
        {
            var cfg = new HotkeyConfig();
            Check.Equal("default", 0.8, cfg.HoldDelaySeconds);

            cfg.HoldDelaySeconds = 0.001;
            Check.Equal("clamped up to the floor", HotkeyConfig.MinHoldDelaySeconds, cfg.HoldDelaySeconds);

            cfg.HoldDelaySeconds = 99;
            Check.Equal("clamped down to the ceiling", HotkeyConfig.MaxHoldDelaySeconds, cfg.HoldDelaySeconds);

            cfg.HoldDelaySeconds = double.NaN;
            Check.Equal("NaN falls back", 0.8, cfg.HoldDelaySeconds);

            cfg.HoldDelaySeconds = 0.375;
            Check.Equal("millisecond precision survives", 0.375, cfg.HoldDelaySeconds);
        });

        Check.Group("restart / previous", () =>
        {
            var cfg = new HotkeyConfig();

            Check.That("the action exists", HotkeyActions.All.Contains(HotkeyActions.RestartOrPrevious));
            Check.Equal("bound to Ctrl+Alt+O", "Ctrl+Alt+O", cfg.For(HotkeyActions.RestartOrPrevious).Keys);
            Check.Equal("offline mode moved aside", "Ctrl+Alt+L", cfg.For(HotkeyActions.OfflineMode).Keys);

            Check.That("its hold is spoken for",
                !HotkeyActions.SupportsHoldToggle(HotkeyActions.RestartOrPrevious));
            Check.That("and it says so",
                HotkeyActions.HoldNote(HotkeyActions.RestartOrPrevious)?.Contains("steps back") == true);

            var keys = HotkeyActions.All.Select(a => cfg.For(a).Keys).ToList();
            Check.Equal("still no collisions", keys.Count, keys.Distinct().Count());

            // A config written before Ctrl+Alt+O changed hands still holds the old value,
            // which would collide with its new owner and disable both shortcuts.
            var stale = new HotkeyConfig();
            stale.For(HotkeyActions.OfflineMode).Keys = "Ctrl+Alt+O";
            Check.That("a superseded default is migrated", stale.MigrateSupersededDefaults());
            Check.Equal("onto its replacement", "Ctrl+Alt+L", stale.For(HotkeyActions.OfflineMode).Keys);
            Check.That("and migrating again is a no-op", !stale.MigrateSupersededDefaults());

            var chosen = new HotkeyConfig();
            chosen.For(HotkeyActions.OfflineMode).Keys = "Ctrl+Alt+J";
            Check.That("a deliberate choice is left alone", !chosen.MigrateSupersededDefaults());
            Check.Equal("untouched", "Ctrl+Alt+J", chosen.For(HotkeyActions.OfflineMode).Keys);
        });
    }
}
