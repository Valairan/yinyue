namespace Yinyue.Tests;

/// <summary>
/// The platform-neutral suite: everything that can be proven without a window, a message
/// loop, or a Win32 call. It runs on macOS and on Windows alike, which is the whole point —
/// the behaviour it covers is shared between the two apps, so it should not need one of them
/// to be verified.
///
/// Its Windows-only sibling in tests/Yinyue.Tests keeps what genuinely needs a desktop:
/// XAML that must parse, panels that must land in the right place, and hotkeys the OS has to
/// actually accept.
/// </summary>
public static class Program
{
    public static int Main()
    {
        HotkeyConfigTests.Run();
        QueueTests.Run().GetAwaiter().GetResult();
        VolumeTests.Run();
        PersistenceTests.Run().GetAwaiter().GetResult();

        return Check.Report();
    }
}
