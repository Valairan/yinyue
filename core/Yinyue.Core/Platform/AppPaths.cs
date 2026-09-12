using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Yinyue.Services
{
    /// <summary>
    /// Where Yinyue keeps config.json, queue.json, tracks.db, art/ and yinyue.log.
    ///
    /// One resolver rather than each service computing its own, because they must agree:
    /// the pruner and the writer of the artwork cache pointing at different folders is the
    /// kind of bug that only shows up as "the cache never shrinks".
    /// </summary>
    public static class AppPaths
    {
        /// <summary>
        /// <c>%APPDATA%\Yinyue</c> on Windows, <c>~/Library/Application Support/Yinyue</c>
        /// on macOS.
        ///
        /// The macOS branch is explicit because .NET maps
        /// <see cref="Environment.SpecialFolder.ApplicationData"/> to <c>~/.config</c>
        /// there, following the XDG convention rather than the Apple one. A Mac user looking
        /// for this folder will look in Application Support.
        /// </summary>
        public static string DataFolder
        {
            get
            {
                string root = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "Application Support")
                    : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                string folder = Path.Combine(root, "Yinyue");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }
    }
}
