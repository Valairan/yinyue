using Foundation;
using ServiceManagement;

namespace Yinyue.Services
{
    /// <summary>
    /// Start at sign-in — the counterpart to <c>StartupService</c> and its Run-key entry.
    ///
    /// <c>SMAppService.MainApp</c> registers the <b>bundle</b>, not a path, which removes the
    /// failure the Windows one documents: the Run key holds an absolute path, so moving or
    /// republishing the app strands it and settings has to detect the stale entry and offer to
    /// repair it. macOS tracks the bundle identity instead, so moving the .app cannot leave a
    /// dangling registration.
    ///
    /// The cost is that it only works from a **real bundle**. Run from the binary during
    /// development and registration fails, which is correct rather than unfortunate — there
    /// would be nothing stable to register.
    /// </summary>
    public sealed class MacStartupService
    {
        public bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(13);

        /// <summary>
        /// Whether macOS will launch Yinyue at sign-in. <c>RequiresApproval</c> counts as off:
        /// the user has been asked and has not said yes, so nothing will launch.
        /// </summary>
        public bool IsEnabled
        {
            get
            {
                if (!IsSupported) return false;

                try
                {
                    return SMAppService.MainApp.Status == SMAppServiceStatus.Enabled;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Startup] {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Turns it on or off. Returns the message to show when that could not be done —
        /// typically because the app is not running from a bundle, or because the user has
        /// switched it off in System Settings, which the app cannot override.
        /// </summary>
        public string? SetEnabled(bool enabled)
        {
            if (!IsSupported) return "Start at sign-in needs macOS 13 or later.";

            try
            {
                NSError? error;

                if (enabled) SMAppService.MainApp.Register(out error);
                else SMAppService.MainApp.Unregister(out error);

                if (error is null) return null;

                return SMAppService.MainApp.Status == SMAppServiceStatus.RequiresApproval
                    ? "Approve Yinyue in System Settings › General › Login Items."
                    : error.LocalizedDescription;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
