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
    /// It registers whatever bundle is calling, so it works from the dev build as readily as
    /// from an installed copy — <c>NSBundle.MainBundle</c> resolves to the .app even when the
    /// binary inside it is launched directly. Verified from both, and from the packaged
    /// ad-hoc-signed bundle: registration succeeds and the item reports enabled.
    ///
    /// Signing does not gate it. What ad-hoc signing gates is the <em>first launch</em> of a
    /// downloaded copy, which Gatekeeper refuses until the user right-clicks and chooses
    /// Open — after that the quarantine flag is gone and a login item behaves normally.
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
