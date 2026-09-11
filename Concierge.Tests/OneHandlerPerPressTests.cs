using System.Text.RegularExpressions;

namespace Concierge.Tests;

/// <summary>
/// One press, one handler.
///
/// Thirty-five buttons carried both <c>@onclick</c> and <c>@onpointerup</c> bound
/// to the same method. A single press fires both, so every one of them ran its
/// handler twice — saving a key twice, fetching a diagram twice, answering an
/// approval twice. On anything that toggles it was worse than twice: the section
/// opened and shut again in the same press, so **no folding section in any room
/// could be opened at all.** Found by tapping "Can change things" on a phone and
/// watching the row light up and stay closed.
///
/// **The reason it was there, and what was actually measured.** A comment repeated
/// in three files said "MAUI's BlazorWebView drops touch-originated clicks on
/// buttons intermittently". Nobody had recorded a measurement, and this file's
/// history is a run of confident claims that turned out to be wrong — so rather
/// than delete it on a hunch or keep it on one, it was tested: fourteen alternating
/// taps on the permission-mode buttons in the Android head, driven through
/// `input tap`, with the state read back from the accessibility tree after each.
/// **Fourteen of fourteen registered. None dropped.**
///
/// The honest limit on that: it is one emulator, one Android version, one WebView
/// build. It does not prove clicks are never dropped anywhere. It does mean the
/// workaround was costing a real, reproducible defect to prevent one nobody had
/// shown, and if dropped clicks turn up on a real device the fix is a handler that
/// runs once — not two handlers that both run.
///
/// (The measuring was wrong first, naturally: the first run reported 12 of 12
/// dropped because the parser reading the accessibility tree matched nothing. The
/// buttons had worked the whole time. Fifth time in this repository that a
/// measurement, not the thing measured, was the broken part.)
/// </summary>
public sealed class OneHandlerPerPressTests
{
    private static IEnumerable<string> Pages()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !Directory.Exists(Path.Combine(here.FullName, "Concierge.Shared.Components")))
        {
            here = here.Parent;
        }

        Assert.NotNull(here);

        return Directory
            .EnumerateFiles(here!.FullName, "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    /// <summary>
    /// Every element that reacts to a press reacts once.
    ///
    /// Written against the markup rather than a rendered component because bUnit
    /// raises one event at a time: a test that clicks sees one call and passes,
    /// while a finger raises pointerup *and* click and sees two. The defect lives
    /// in the pairing, so the pairing is what is checked.
    /// </summary>
    [Fact]
    public void No_element_answers_the_same_press_twice()
    {
        var doubled = new List<string>();

        foreach (var page in Pages())
        {
            var markup = File.ReadAllText(page);

            // Each element, from its opening angle bracket to the end of the tag.
            foreach (Match element in Regex.Matches(markup, @"<[a-zA-Z][^<>]*>", RegexOptions.Singleline))
            {
                var tag = element.Value;

                if (tag.Contains("@onclick", StringComparison.Ordinal)
                    && tag.Contains("@onpointerup", StringComparison.Ordinal))
                {
                    doubled.Add($"{Path.GetFileName(page)}: {tag.Split('\n')[0].Trim()}");
                }
            }
        }

        Assert.True(
            doubled.Count == 0,
            "These answer one press twice:" + Environment.NewLine + string.Join(Environment.NewLine, doubled));
    }

    /// <summary>
    /// And nothing reacts to a pointer alone.
    ///
    /// `@onpointerup` without `@onclick` is a button a keyboard cannot press —
    /// Enter and Space send a click and nothing else. On the Approve and Deny
    /// buttons that would mean somebody who does not use a mouse cannot answer an
    /// approval at all, on the one surface the product's whole claim rests on.
    /// </summary>
    [Fact]
    public void Nothing_can_only_be_pressed_with_a_pointer()
    {
        var pointerOnly = new List<string>();

        foreach (var page in Pages())
        {
            var markup = File.ReadAllText(page);

            foreach (Match element in Regex.Matches(markup, @"<[a-zA-Z][^<>]*>", RegexOptions.Singleline))
            {
                var tag = element.Value;

                if (tag.Contains("@onpointerup", StringComparison.Ordinal)
                    && !tag.Contains("@onclick", StringComparison.Ordinal))
                {
                    pointerOnly.Add($"{Path.GetFileName(page)}: {tag.Split('\n')[0].Trim()}");
                }
            }
        }

        Assert.True(
            pointerOnly.Count == 0,
            "These cannot be pressed with a keyboard:" + Environment.NewLine
            + string.Join(Environment.NewLine, pointerOnly));
    }

    /// <summary>
    /// The test that would have caught the original defect on its own terms: a
    /// folding section opens when it is pressed once.
    /// </summary>
    [Fact]
    public void A_folding_section_opens_on_one_press()
    {
        var section = Pages().Single(page => Path.GetFileName(page) == "RoomSection.razor");
        var markup = File.ReadAllText(section);

        Assert.Contains("@onclick=\"Toggle\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("@onpointerup", markup.Replace("@onclick and @onpointerup", string.Empty, StringComparison.Ordinal));
    }
}
