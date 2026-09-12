using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// What an export leaves behind, and whether anybody is told.
///
/// **Found by exporting a running order on the running app and reading the answer.** Six
/// tracks went in — three real recordings and three stubs that are not decodable audio — and
/// a 79 KB file came out running 7.47 seconds, which is exactly the three real ones. The
/// message was "Saved to C:\Users\user\Music\design.m4a." and nothing else.
///
/// Half the running order was not in the file and the export called that a success. On a
/// marketplace that is found out by whoever downloads it, which is the worst possible reader.
///
/// The failure path was already honest — "None of these sounds could be read" when every one
/// fails. It was the *partial* case that said nothing, which is the harder one to notice and
/// the one that actually happens.
/// </summary>
public sealed class ExportSaysWhatIsMissingTests
{
    [Fact]
    public void A_result_carries_what_was_left_out()
    {
        var made = ExportResult.Made(@"C:\Music\design.m4a", "3 of 6 could not be read and are not in it.");

        Assert.True(made.Ok);
        Assert.Contains("3 of 6", made.Left!, StringComparison.Ordinal);
    }

    /// <summary>
    /// And says nothing when there is nothing to say, so the ordinary case stays quiet.
    /// </summary>
    [Fact]
    public void And_says_nothing_when_everything_went_in()
        => Assert.Null(ExportResult.Made(@"C:\Music\design.m4a").Left);

    /// <summary>
    /// A failure has no file and no leftovers to report — the existing shape, unchanged.
    /// </summary>
    [Fact]
    public void And_a_failure_is_still_just_a_failure()
    {
        var failed = ExportResult.Failed("Nothing could be read.");

        Assert.False(failed.Ok);
        Assert.Null(failed.Path);
        Assert.Null(failed.Left);
    }
}
