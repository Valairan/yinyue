using System.Runtime.CompilerServices;

namespace Yinyue.Tests;

/// <summary>
/// A deliberately tiny harness rather than a test framework.
///
/// The suite needs an STA thread, a live WPF Application for resource lookups, and real
/// Win32 hotkey registration. Getting a conventional runner to provide all three costs
/// more than it saves for a project this size, and a plain exe returns an exit code that
/// any CI can read.
/// </summary>
public static class Check
{
    private static readonly List<string> Failures = new();

    public static int Passed { get; private set; }
    public static int Failed => Failures.Count;

    public static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine(name);
    }

    public static void That(string label, bool condition, string? detail = null)
    {
        string suffix = detail is null ? "" : $"  — {detail}";

        if (condition)
        {
            Passed++;
            Console.WriteLine($"  [ok] {label}{suffix}");
            return;
        }

        Failures.Add(label);
        Console.WriteLine($"  [FAIL] {label}{suffix}");
    }

    public static void Equal<T>(string label, T expected, T actual) =>
        That($"{label} (expected {expected}, got {actual})",
            EqualityComparer<T>.Default.Equals(expected, actual));

    public static void Throws<T>(string label, Action action) where T : Exception
    {
        try
        {
            action();
            That(label, false, "no exception");
        }
        catch (T)
        {
            That(label, true);
        }
        catch (Exception ex)
        {
            That(label, false, $"threw {ex.GetType().Name}");
        }
    }

    /// <summary>Runs a group, turning an unexpected exception into a failure rather than a crash.</summary>
    public static void Group(string name, Action body)
    {
        Section(name);
        try
        {
            body();
        }
        catch (Exception ex)
        {
            That($"{name} completed", false, ex.Message);
        }
    }

    public static async Task GroupAsync(string name, Func<Task> body)
    {
        Section(name);
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            That($"{name} completed", false, ex.Message);
        }
    }

    public static int Report()
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));

        if (Failed == 0)
        {
            Console.WriteLine($"PASS — {Passed} checks");
            return 0;
        }

        Console.WriteLine($"FAIL — {Failed} of {Passed + Failed} checks failed:");
        foreach (var failure in Failures) Console.WriteLine($"  · {failure}");
        return 1;
    }
}
