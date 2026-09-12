using System.Diagnostics;
using System.IO;
using Yinyue.Services;

namespace Yinyue.Tests;

/// <summary>
/// The one indexer behaviour that needs a real ACL: a subfolder the user genuinely cannot
/// open. The Core suite covers the rest of the indexer; this denies read access with icacls
/// and checks that the scan skips the folder instead of dying on it, which is what the
/// SearchOption overload of GetFiles used to do.
/// </summary>
public static class ScanTests
{
    public static void Run()
    {
        Check.Group("a scan survives a subfolder it cannot read", () =>
        {
            using var tree = new TempTree();
            tree.Wav("open.wav");
            string locked = Path.GetDirectoryName(tree.Wav("locked/unreachable.wav"))!;

            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            Check.Equal("access denied by ACL", 0, Icacls($"\"{locked}\" /deny \"{user}\":(OI)(CI)(RX)"));
            try
            {
                Check.Throws<UnauthorizedAccessException>("the folder really is unreadable",
                    () => Directory.GetFiles(locked));

                var indexer = new LibraryIndexerService(AudioPlayerService.NativeContainers, tree.Db);

                // Task.Run so the awaits inside never post back to this STA thread, which is
                // blocked waiting on them.
                int indexed = Task.Run(() => indexer.IndexDirectoryAsync(tree.Root)).GetAwaiter().GetResult();
                Check.Equal("the readable file is indexed and the denied folder skipped", 1, indexed);
            }
            finally
            {
                Icacls($"\"{locked}\" /remove:d \"{user}\"");
            }
        });
    }

    private static int Icacls(string args)
    {
        using var process = Process.Start(new ProcessStartInfo("icacls", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode;
    }
}
