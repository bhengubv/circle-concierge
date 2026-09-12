using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// A file goes somewhere a person can find it, on every head.
///
/// **Four call sites asked the operating system for Music, Video, Pictures or
/// Documents and used the answer unchecked.** `GetFolderPath` hands back an empty
/// string rather than throwing where a platform has no such folder — the ordinary
/// case on Android, where an app has its own sandbox and no music folder of its
/// own — and `Path.Combine("", "Concierge", "Footage")` is then a relative path.
/// So a clip landed wherever the working directory happened to be, and the answer
/// still read "Saved to Concierge\Footage\clip.mp4".
///
/// One of the five already had the fallback, and said in its own comment exactly
/// why: a saved file nobody can find is the same as no saved file. The other four
/// were written after it and did not.
///
/// Found by reading the handheld against the desktop rather than by somebody
/// losing a deck.
/// </summary>
public sealed class WhereThingsGoTests
{
    public static TheoryData<string, string> EveryFolder => new()
    {
        { "music", WhereThingsGo.Music },
        { "video", WhereThingsGo.Video },
        { "pictures", WhereThingsGo.Pictures },
        { "documents", WhereThingsGo.Documents },
    };

    /// <summary>
    /// **The whole point: never empty**, because empty is what turns an absolute
    /// path into a relative one three lines later.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFolder))]
    public void None_of_them_comes_back_empty(string which, string folder)
        => Assert.False(string.IsNullOrWhiteSpace(folder), $"{which} came back empty");

    /// <summary>
    /// And rooted, which is the property a caller is actually relying on when it
    /// combines a name onto the end and reports the result to somebody.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFolder))]
    public void And_every_one_of_them_is_a_real_path(string which, string folder)
        => Assert.True(Path.IsPathRooted(folder), $"{which} is not rooted: '{folder}'");

    /// <summary>
    /// On a desktop they are the real folders rather than the home directory, so
    /// the fallback has not quietly become the answer everywhere — which would
    /// hide a broken lookup behind a test that still passes.
    /// </summary>
    [Fact]
    public void On_a_machine_that_has_them_they_are_the_real_ones()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), WhereThingsGo.Music);
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), WhereThingsGo.Documents);
    }

    /// <summary>
    /// And nothing anywhere reaches past it to ask the operating system directly
    /// for one of the four — the rule, rather than the four places that broke it.
    /// A fifth call site written next month is the same defect again.
    /// </summary>
    [Fact]
    public void Nothing_asks_the_operating_system_for_one_of_these_directly()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));

        var shared = Path.Combine(root, "Concierge.Shared");

        // Running from somewhere the sources are not is a fact about the run, not
        // a pass. Said out loud rather than skipped, because four export tests in
        // this repository once stood down quietly and reported success for weeks.
        Assert.True(Directory.Exists(shared), $"the sources are not at {shared}");

        string[] wanted = ["MyMusic", "MyVideos", "MyPictures", "MyDocuments"];

        var offenders = Directory
            .EnumerateFiles(shared, "*.cs", SearchOption.AllDirectories)
            .Where(file => !string.Equals(
                Path.GetFileName(file), "WhereThingsGo.cs", StringComparison.Ordinal))
            .Where(file => wanted.Any(name =>
                File.ReadAllText(file).Contains($"SpecialFolder.{name}", StringComparison.Ordinal)))
            .Select(file => Path.GetFileName(file))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "these ask the operating system directly instead of WhereThingsGo: "
            + string.Join(", ", offenders));
    }
}
