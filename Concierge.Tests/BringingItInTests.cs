using System.Text.Json.Nodes;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Bringing what somebody already has into a design.
///
/// **Nothing could.** A creator arrives with forty photographs, a folder of stems and a
/// manuscript, and the only door into this surface was a paperclip that takes one picture at
/// a time, 4 MB, one click each. Every other capability is worth more once their own work is
/// inside — which is why this came before the rest.
///
/// Said rather than browsed. A file dialog needs a mouse and a desk, and somebody talking
/// into a watch has neither.
/// </summary>
public sealed class BringingItInTests
{
    private static string AFolder(params (string Name, byte[] Bytes)[] files)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);

        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(path, name), bytes);
        }

        return path;
    }

    /// <summary>A real one-pixel PNG, so the bytes actually sniff as a picture.</summary>
    private static byte[] APicture() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static async Task<(bool Success, string Output, string? Failure, DesignDocument Document)> Bring(
        string folder, string what = "everything")
    {
        var session = new DesignSession();
        var bench = new DesignWorkbench();
        bench.Attach(session);

        var result = await new BringItIn(bench).InvokeAsync(new JsonObject
        {
            ["folder"] = folder,
            ["what"] = what,
        });

        return (result.Success, result.Output, result.FailureMessage, session.Current);
    }

    // ── What the sentence understands ────────────────────────────────────

    [Theory]
    [InlineData("bring in the music from D:\\Albums", "music", "D:\\Albums")]
    [InlineData("use the pictures in C:\\Photos", "pictures", "C:\\Photos")]
    [InlineData("import everything from my footage folder", "everything", "footage folder")]
    [InlineData("pull in all my photos from C:\\Users\\me\\Pictures", "photos", "C:\\Users\\me\\Pictures")]
    public void The_ways_people_ask_for_their_own_work(string said, string what, string folder)
    {
        var heard = DesignSpeech.HeardAnImport(said);

        Assert.NotNull(heard);
        Assert.Equal(what, heard!.Value.What);
        Assert.Equal(folder, heard.Value.Folder);
    }

    /// <summary>
    /// A path with spaces in it, which is every Windows path anybody actually has. A pattern
    /// that stopped at the first space would fail on the common case.
    /// </summary>
    [Fact]
    public void And_a_folder_with_spaces_in_its_name()
        => Assert.Equal(
            "C:\\Users\\me\\My Music\\Live Takes",
            DesignSpeech.HeardAnImport("bring in the music from C:\\Users\\me\\My Music\\Live Takes")!.Value.Folder);

    [Theory]
    [InlineData("add a wall")]
    [InlineData("save it")]
    [InlineData("make it warm")]
    [InlineData("add a picture")]
    public void But_nothing_else_is_an_import(string said)
        => Assert.Null(DesignSpeech.HeardAnImport(said));

    // ── What actually comes in ───────────────────────────────────────────

    /// <summary>
    /// A picture travels inside the design as a data URI — the rule the paperclip already
    /// follows, and what makes a design portable rather than a pointer at one machine.
    /// </summary>
    [Fact]
    public async Task A_picture_is_carried_into_the_design()
    {
        var folder = AFolder(("one.png", APicture()));

        var brought = await Bring(folder);

        Assert.True(brought.Success, brought.Failure);

        var picture = brought.Document.Nodes.Values.Single(node => node.Kind == DesignNodeKind.Image);

        Assert.StartsWith("data:image/png;base64,", picture.Props["src"], StringComparison.Ordinal);
        Assert.Equal("one", picture.Text);
    }

    /// <summary>
    /// **Short audio travels, which is the difference between a track you can hear back and a
    /// name on a list.** A page can decode a data URI and cannot open a file on a disk, so a
    /// carried track plays and can be drawn as a waveform while a pointed-at one is a
    /// placeholder. A voice note, a demo, a stinger — what people actually send on.
    /// </summary>
    [Fact]
    public async Task Short_audio_travels_inside_the_design()
    {
        var folder = AFolder(("note.mp3", new byte[64]));

        var brought = await Bring(folder);

        Assert.True(brought.Success, brought.Failure);

        var track = brought.Document.Nodes.Values.Single(node => node.Kind == DesignNodeKind.Sound);

        Assert.StartsWith("data:audio/mpeg;base64,", track.Props["src"], StringComparison.Ordinal);
        Assert.Equal("note", track.Text);
    }

    /// <summary>
    /// And anything bigger points at where it is, because a folder of albums is gigabytes and
    /// design.json is rewritten whenever anybody edits a heading. The same rule footage
    /// already follows.
    /// </summary>
    [Fact]
    public async Task But_a_long_one_is_pointed_at_rather_than_swallowed()
    {
        var folder = AFolder(("album.mp3", new byte[BringItIn.BiggestSound + 1]));

        var brought = await Bring(folder);

        var track = brought.Document.Nodes.Values.Single(node => node.Kind == DesignNodeKind.Sound);

        Assert.Equal(Path.Combine(folder, "album.mp3"), track.Props["src"]);
        Assert.Contains("stay on this machine", brought.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **A per-file cap on its own is not a bound.** Forty two-megabyte tracks is eighty
    /// megabytes of base64 in a document rewritten on every keystroke, so one sentence has a
    /// budget: the first travel, the rest point.
    /// </summary>
    [Fact]
    public async Task And_one_sentence_cannot_carry_an_unbounded_amount()
    {
        var each = BringItIn.BiggestSound;
        var enough = (int)(BringItIn.SoundBudget / each) + 2;

        var many = Enumerable.Range(0, enough)
            .Select(i => ($"track{i:00}.mp3", new byte[each - 1]))
            .ToArray();

        var brought = await Bring(AFolder(many));

        var tracks = brought.Document.Nodes.Values
            .Where(node => node.Kind == DesignNodeKind.Sound)
            .ToList();

        var travelling = tracks.Count(t => t.Props["src"].StartsWith("data:", StringComparison.Ordinal));

        Assert.True(travelling > 0, "nothing travelled at all");
        Assert.True(travelling < tracks.Count, "the budget did not stop anything");
    }

    /// <summary>
    /// Asking for one kind gets one kind. Somebody who says "bring in the music" and gets
    /// their holiday photographs has been ignored.
    /// </summary>
    [Fact]
    public async Task What_was_asked_for_is_what_arrives()
    {
        var folder = AFolder(("song.mp3", new byte[64]), ("one.png", APicture()));

        var brought = await Bring(folder, "music");

        Assert.Contains(brought.Document.Nodes.Values, node => node.Kind == DesignNodeKind.Sound);
        Assert.DoesNotContain(brought.Document.Nodes.Values, node => node.Kind == DesignNodeKind.Image);
    }

    /// <summary>
    /// The kind is read from the bytes, not the name — the same helper the paperclip and the
    /// vision path use. A text file called holiday.png would otherwise become a data URI
    /// claiming to be a picture, and the design would carry something that renders nowhere.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_really_a_picture_does_not_come_in_as_one()
    {
        var folder = AFolder(("liar.png", "this is not a png"u8.ToArray()));

        var brought = await Bring(folder);

        Assert.False(brought.Success);
        Assert.DoesNotContain(brought.Document.Nodes.Values, node => node.Kind == DesignNodeKind.Image);
        Assert.Contains(
            "could not be read", brought.Failure ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ── What it says about what it left ──────────────────────────────────

    /// <summary>
    /// **Always said.** A folder of sixty photographs that quietly becomes forty is the
    /// defect this repository has spent its history removing: a screen that looks like it
    /// worked. Somebody missing twenty pictures needs to know now, not when they publish.
    /// </summary>
    [Fact]
    public async Task What_it_left_behind_is_said_rather_than_dropped()
    {
        var many = Enumerable.Range(0, BringItIn.Most + 5)
            .Select(i => ($"track{i:00}.mp3", new byte[8]))
            .ToArray();

        var brought = await Bring(AFolder(many));

        Assert.True(brought.Success);
        Assert.Equal(
            BringItIn.Most,
            brought.Document.Nodes.Values.Count(node => node.Kind == DesignNodeKind.Sound));
        Assert.Contains("5 more", brought.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_that_is_not_there_says_so()
    {
        var brought = await Bring(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"));

        Assert.False(brought.Success);
        Assert.Contains("no folder", brought.Failure ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task And_an_empty_one_says_that_instead()
    {
        var brought = await Bring(AFolder());

        Assert.False(brought.Success);
        Assert.Contains(
            "nothing to bring in", brought.Failure ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
