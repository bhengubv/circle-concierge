using System.Runtime.CompilerServices;

namespace Concierge.Tests;

/// <summary>
/// Gives the whole test run one temp directory, and removes it afterwards.
///
/// The problem this solves: sixty-one places across thirty-two test files build
/// their own path with <c>Path.Combine(Path.GetTempPath(), $"concierge-x-{Guid}")</c>
/// and most never delete it. Two days of runs left 6,539 directories and files
/// behind — <c>concierge-eventlog</c> alone had 2,540 — along with 168 GB from a
/// download bug that was itself fixed the same night.
///
/// Fixing sixty-one call sites means editing thirty-two files and trusting that
/// nobody adds a sixty-second. Instead this redirects TEMP for the test process
/// before any test runs, so every one of those call sites lands inside a single
/// run-scoped root without changing a line of them, and the root is deleted when
/// the run ends.
///
/// Two safety nets, because a test host can be killed:
///   * previous run roots are swept at startup, so a run that never got to
///     clean up is cleared by the next one;
///   * the legacy prefixes are swept too, so the old litter cannot come back.
/// </summary>
internal static class TestTempRoot
{
    private const string RunPrefix = "concierge-testrun-";

    /// <summary>The prefixes tests used before this existed. Swept once so a
    /// checkout that ran the old code does not keep its leftovers forever.</summary>
    private static readonly string[] LegacyPrefixes =
    [
        "concierge-models-", "concierge-present-", "concierge-here-",
        "concierge-consent-", "concierge-lifetime-", "concierge-offer-",
        "concierge-chat-", "concierge-eventlog-", "concierge-lifecycle-",
        "concierge-resume-", "concierge-acp-", "concierge-prefix-tests",
        "concierge-doesnt-exist-",
    ];

    [ModuleInitializer]
    internal static void Redirect()
    {
        // The real temp directory, read before it is redirected.
        var systemTemp = Path.GetTempPath();

        Sweep(systemTemp);

        var root = Path.Combine(systemTemp, RunPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // GetTempPath reads TMP, then TEMP, then USERPROFILE from the process
        // environment on every call, so setting these redirects every existing
        // call site without touching one of them.
        Environment.SetEnvironmentVariable("TMP", root);
        Environment.SetEnvironmentVariable("TEMP", root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(root);
    }

    /// <summary>Clear what earlier runs left. Best effort throughout: a directory
    /// another process still holds is skipped rather than failing the run, and
    /// the next sweep will take it.</summary>
    private static void Sweep(string systemTemp)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(systemTemp, "concierge-*"))
            {
                var name = Path.GetFileName(entry);
                var ours = name.StartsWith(RunPrefix, StringComparison.Ordinal)
                           || LegacyPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
                if (ours)
                {
                    Delete(entry);
                }
            }
        }
        catch
        {
            // A temp directory that cannot be enumerated is not a reason to fail
            // every test in the assembly.
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Locked by something still running. The next run sweeps it.
        }
    }
}
