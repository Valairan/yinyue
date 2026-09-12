using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Yinyue.Services
{
    /// <summary>
    /// Start-with-Windows, via the per-user Run key.
    ///
    /// Deliberately HKCU and not a scheduled task or a service: it needs no elevation, it
    /// follows the user rather than the machine, and it is somewhere people know to look
    /// when they want to turn it off.
    /// </summary>
    public static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Yinyue";

        /// <summary>
        /// The launcher, not the managed assembly. On .NET the managed DLL is not directly
        /// executable, so registering it would produce an entry that silently does nothing.
        /// </summary>
        public static string? ExecutablePath
        {
            get
            {
                try
                {
                    string? path = Process.GetCurrentProcess().MainModule?.FileName;
                    return string.IsNullOrWhiteSpace(path) ? null : path;
                }
                catch
                {
                    return null;
                }
            }
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Startup] Read failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Returns false when the registry refused the change, so the UI can report it
        /// rather than showing a toggle that silently did nothing.
        /// </summary>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null) return false;

                if (!enabled)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    return true;
                }

                string? exe = ExecutablePath;
                if (exe == null) return false;

                // Quoted: the path routinely contains spaces, and an unquoted Run entry is
                // parsed at the first one.
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Startup] Write failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// True when the registered path no longer matches this build — after the app has
        /// been moved or republished, the old entry would launch nothing.
        /// </summary>
        public static bool IsStale()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string registered) return false;

                string? current = ExecutablePath;
                if (current == null) return false;

                return !string.Equals(registered.Trim('"'), current, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
