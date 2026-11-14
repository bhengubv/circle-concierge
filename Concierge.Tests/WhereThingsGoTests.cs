using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// Every durable creation is rooted under Concierge content so one deliberate
/// export can gather it. The OS media folders are destinations, not defaults.
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
    /// User creations stay below one portable Concierge content root even on a
    /// desktop with Music and Documents folders of its own.
    /// </summary>
    [Fact]
    public void On_a_machine_that_has_them_they_still_use_one_concierge_root()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(ConciergeDataPaths.Default.Music, WhereThingsGo.Music);
        Assert.Equal(ConciergeDataPaths.Default.Documents, WhereThingsGo.Documents);
        Assert.NotEqual(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), WhereThingsGo.Music);
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
