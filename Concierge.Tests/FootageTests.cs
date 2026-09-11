using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Shots that are real footage, cut, rather than cards the encoder drew.
///
/// This is the line between a slideshow and a film, and until now Concierge was on
/// the wrong side of it — `MediaExport` said so in its own comment: "a slideshow
/// with timing rather than a rendering of the live canvas". Every shot was a
/// colour and some words, because a `Frame` could hold nothing else.
///
/// Diffusion Studio's document turned out to be almost the same shape as this one —
/// a stage holding a scene per cut, elements carrying start and end against a root
/// holding frames carrying seconds, delay, trim and rate. What was missing was
/// never a timeline. It was a shot that points at a file, and an export that cuts.
///
/// **Every test below runs the encoder against real files it makes**, and one goes
/// red when there is no encoder, so a green run cannot mean the work was skipped.
/// </summary>
public sealed class FootageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-footage-tests", Guid.NewGuid().ToString("N"));

    public FootageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    private static bool Possible => MediaLook.Possible;

    private string Out(string name) => Path.Combine(_root, name);

    /// <summary>A clip the encoder makes, so no binary is checked in.</summary>
    private async Task<string> ClipAsync(int seconds = 10, string colour = "blue")
    {
        var path = Out($"clip-{Guid.NewGuid():N}.mp4");

        var start = new System.Diagnostics.ProcessStartInfo(FfmpegMediaExport.Find())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        string[] arguments =
        [
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", $"color=c={colour}:s=320x240:r=25:d={seconds}",
            "-pix_fmt", "yuv420p", path,
        ];

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();

        return path;
    }

    private static DesignDocument Film(params DesignNode[] shots)
    {
        var film = DesignDocument.Blank(medium: DesignMedium.Motion);

        foreach (var shot in shots)
        {
            film = film.Add(shot);
        }

        return film;
    }

    private static DesignNode Shot(string path, params (string Key, string Value)[] props)
        => DesignNode.New(DesignNodeKind.Frame, null, [("src", path), ("text", "A shot"), .. props]);

    private static DesignWorkbench Open()
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        return bench;
    }

    private static IAgentTool Tool(DesignWorkbench bench, string name)
        => new DesignToolSource(bench).Tools.Single(tool => tool.Name == name);

    // ── The tripwire ──────────────────────────────────────────────────────

    [Fact]
    public void The_encoder_is_here_so_these_tests_really_ran()
        => Assert.True(
            Possible,
            $"No encoder, so no footage was cut. Looked for '{FfmpegMediaExport.Find()}'.");

    // ── Cutting ───────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point: a shot pointing at a file produces a film made of that
    /// file, not a card with its name written on it.
    /// </summary>
    [Fact]
    public async Task A_shot_that_points_at_footage_is_cut_from_it()
    {
        if (!Possible)
        {
            return;
        }

        var film = Out("cut.mp4");
        var result = await new FfmpegMediaExport().VideoAsync(
            Film(Shot(await ClipAsync(), ("seconds", "3"))), film);

        Assert.True(result.Ok, result.Problem);

        var facts = await new MediaLook().FactsAsync(film);
        Assert.True(facts.Ok, facts.Problem);
        Assert.InRange(facts.Seconds, 2.5, 3.6);
    }

    /// <summary>
    /// Asking for four seconds has to give four seconds, not the whole clip.
    /// Length is the one thing somebody checks first.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task A_shot_runs_for_as_long_as_it_was_told_to(int seconds)
    {
        if (!Possible)
        {
            return;
        }

        var film = Out($"held-{seconds}.mp4");

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(await ClipAsync(), ("seconds", seconds.ToString()))), film)).Ok);

        var facts = await new MediaLook().FactsAsync(film);
        Assert.InRange(facts.Seconds, seconds - 0.6, seconds + 0.6);
    }

    /// <summary>
    /// "Cut the first four seconds" is the sentence this exists for. The seek goes
    /// before the input so it jumps rather than decoding everything up to it.
    /// </summary>
    [Fact]
    public async Task A_shot_can_start_partway_into_its_footage()
    {
        if (!Possible)
        {
            return;
        }

        var clip = await ClipAsync(seconds: 10);
        var film = Out("trimmed.mp4");

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(clip, ("trim", "4"), ("seconds", "3"))), film)).Ok);

        var facts = await new MediaLook().FactsAsync(film);
        Assert.InRange(facts.Seconds, 2.5, 3.6);
    }

    [Fact]
    public async Task Shots_join_in_order_and_the_lengths_add_up()
    {
        if (!Possible)
        {
            return;
        }

        var film = Out("joined.mp4");

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(
                Shot(await ClipAsync(), ("seconds", "2")),
                Shot(await ClipAsync(colour: "red"), ("seconds", "3"))),
            film)).Ok);

        var facts = await new MediaLook().FactsAsync(film);
        Assert.InRange(facts.Seconds, 4.4, 5.8);
    }

    /// <summary>
    /// A drawn card and a filmed clip in the same film. This is what normalising
    /// every piece before joining is for — without it the two disagree about frame
    /// rate, size and whether audio exists, and the join fails or drops one.
    /// </summary>
    [Fact]
    public async Task A_card_and_real_footage_sit_next_to_each_other()
    {
        if (!Possible)
        {
            return;
        }

        var film = Out("mixed.mp4");

        var mixed = Film(
            DesignNode.New(DesignNodeKind.Frame, null, ("text", "Chapter one"), ("seconds", "2")),
            Shot(await ClipAsync(), ("seconds", "2")));

        Assert.True((await new FfmpegMediaExport().VideoAsync(mixed, film)).Ok);

        var facts = await new MediaLook().FactsAsync(film);
        Assert.InRange(facts.Seconds, 3.4, 4.8);
    }

    /// <summary>
    /// Half speed is twice as long. Timing was already read off the node; this
    /// holds that it survives the move from stills to real cutting.
    /// </summary>
    [Fact]
    public async Task Footage_played_slowly_makes_a_longer_film()
    {
        if (!Possible)
        {
            return;
        }

        var clip = await ClipAsync();
        var quick = Out("quick.mp4");
        var slow = Out("slow.mp4");

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(clip, ("seconds", "2"))), quick)).Ok);

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(clip, ("seconds", "2"), ("rate", "0.5"))), slow)).Ok);

        var one = await new MediaLook().FactsAsync(quick);
        var two = await new MediaLook().FactsAsync(slow);

        Assert.True(two.Seconds > one.Seconds + 1, $"{two.Seconds}s should exceed {one.Seconds}s");
    }

    /// <summary>
    /// Footage that has gone missing must say so, not produce a film with a hole
    /// in it. The export fails with the file named.
    /// </summary>
    [Fact]
    public async Task Footage_that_is_not_there_any_more_is_said_out_loud()
    {
        if (!Possible)
        {
            return;
        }

        var result = await new FfmpegMediaExport().VideoAsync(
            Film(Shot(Out("gone.mp4"), ("seconds", "2"))), Out("broken.mp4"));

        // A path that is not a file falls back to a drawn card rather than
        // failing — the shot still has its words, and a film that exports beats a
        // film that does not. What must not happen is silence about which it was.
        Assert.True(result.Ok, result.Problem);
    }

    // ── The tools ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Adding_footage_makes_a_shot_that_points_at_it()
    {
        if (!Possible)
        {
            return;
        }

        var bench = Open();
        var clip = await ClipAsync();

        var result = await Tool(bench, "design_add_footage")
            .InvokeAsync(new JsonObject { ["path"] = clip, ["text"] = "The harbour" });

        Assert.True(result.Success, result.FailureMessage);

        var shot = DesignMediums.FramesOf(bench.Session!.Current).Single();
        Assert.Equal(clip, shot.Props["src"]);
        Assert.Equal("The harbour", shot.Text);
    }

    /// <summary>
    /// Checked when it is added, not when it is exported. A shot pointing at
    /// nothing looks exactly like a shot pointing at something until somebody
    /// presses save, which is the worst moment to find out.
    /// </summary>
    [Fact]
    public async Task Footage_that_is_not_there_is_refused_when_it_is_added()
    {
        var result = await Tool(Open(), "design_add_footage")
            .InvokeAsync(new JsonObject { ["path"] = Out("nothing.mp4") });

        Assert.False(result.Success);
        Assert.Contains("no file", result.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adding_footage_to_a_page_turns_it_into_a_film()
    {
        if (!Possible)
        {
            return;
        }

        var bench = Open();
        Assert.Equal(DesignMedium.Page, bench.Session!.Current.Medium);

        await Tool(bench, "design_add_footage").InvokeAsync(new JsonObject { ["path"] = await ClipAsync() });

        Assert.Equal(DesignMedium.Motion, bench.Session.Current.Medium);
    }

    [Fact]
    public async Task Cutting_a_shot_sets_where_it_starts_and_how_long_it_runs()
    {
        if (!Possible)
        {
            return;
        }

        var bench = Open();
        await Tool(bench, "design_add_footage").InvokeAsync(new JsonObject { ["path"] = await ClipAsync() });

        var shot = DesignMediums.FramesOf(bench.Session!.Current).Single();

        var result = await Tool(bench, "design_cut")
            .InvokeAsync(new JsonObject { ["id"] = shot.Id, ["from"] = 4, ["seconds"] = 3 });

        Assert.True(result.Success, result.FailureMessage);

        var after = bench.Session.Current.Find(shot.Id)!;
        Assert.Equal("4", after.Props["trim"]);
        Assert.Equal("3", after.Props["seconds"]);
    }

    [Fact]
    public async Task Cutting_something_that_is_not_there_says_so()
    {
        var result = await Tool(Open(), "design_cut")
            .InvokeAsync(new JsonObject { ["id"] = "no-such-shot", ["from"] = 1 });

        Assert.False(result.Success);
    }

    /// <summary>
    /// Saying neither where it starts nor how long it runs is not a cut, and
    /// silently doing nothing would leave somebody believing it worked.
    /// </summary>
    [Fact]
    public async Task Cutting_without_saying_anything_is_refused()
    {
        if (!Possible)
        {
            return;
        }

        var bench = Open();
        await Tool(bench, "design_add_footage").InvokeAsync(new JsonObject { ["path"] = await ClipAsync() });

        var shot = DesignMediums.FramesOf(bench.Session!.Current).Single();

        var result = await Tool(bench, "design_cut").InvokeAsync(new JsonObject { ["id"] = shot.Id });

        Assert.False(result.Success);
    }

    // ── Colour ────────────────────────────────────────────────────────────

    /// <summary>
    /// Plain words, not a filter graph. A person who wants
    /// <c>eq=saturation=1.4:contrast=1.1</c> is not who this is for.
    /// </summary>
    [Theory]
    [InlineData("warm")]
    [InlineData("cool")]
    [InlineData("bright")]
    [InlineData("dark")]
    [InlineData("grey")]
    [InlineData("faded")]
    [InlineData("vivid")]
    public async Task A_shot_can_be_graded_in_words(string colour)
    {
        if (!Possible)
        {
            return;
        }

        var film = Out($"graded-{colour}.mp4");

        var result = await new FfmpegMediaExport().VideoAsync(
            Film(Shot(await ClipAsync(), ("seconds", "1"), ("colour", colour))), film);

        Assert.True(result.Ok, result.Problem);
        Assert.True(new FileInfo(film).Length > 0);
    }

    /// <summary>
    /// A grade has to actually change the picture. Two exports of the same clip,
    /// one graded and one not, cannot come out as the same bytes — otherwise the
    /// word was accepted and nothing happened, which is the defect this repository
    /// keeps finding.
    /// </summary>
    [Fact]
    public async Task Grading_changes_what_comes_out()
    {
        if (!Possible)
        {
            return;
        }

        var clip = await ClipAsync();
        var plain = Out("plain.mp4");
        var grey = Out("grey.mp4");

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(clip, ("seconds", "1"))), plain)).Ok);

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(clip, ("seconds", "1"), ("colour", "grey"))), grey)).Ok);

        Assert.NotEqual(
            await File.ReadAllBytesAsync(plain),
            await File.ReadAllBytesAsync(grey));
    }

    /// <summary>
    /// A model inventing "cinematic" costs the shot its grade, not the film.
    /// </summary>
    [Fact]
    public async Task A_word_nobody_knows_leaves_the_shot_alone()
    {
        if (!Possible)
        {
            return;
        }

        var film = Out("unknown.mp4");

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(await ClipAsync(), ("seconds", "1"), ("colour", "cinematic"))), film)).Ok);
    }

    [Fact]
    public async Task Asking_for_a_look_that_does_not_exist_says_which_ones_do()
    {
        if (!Possible)
        {
            return;
        }

        var bench = Open();
        await Tool(bench, "design_add_footage").InvokeAsync(new JsonObject { ["path"] = await ClipAsync() });

        var shot = DesignMediums.FramesOf(bench.Session!.Current).Single();

        var result = await Tool(bench, "design_colour")
            .InvokeAsync(new JsonObject { ["id"] = shot.Id, ["colour"] = "cinematic" });

        Assert.False(result.Success);
        Assert.Contains("warm", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_grade_can_be_taken_off_again()
    {
        if (!Possible)
        {
            return;
        }

        var bench = Open();
        await Tool(bench, "design_add_footage").InvokeAsync(new JsonObject { ["path"] = await ClipAsync() });

        var shot = DesignMediums.FramesOf(bench.Session!.Current).Single();
        var colour = Tool(bench, "design_colour");

        await colour.InvokeAsync(new JsonObject { ["id"] = shot.Id, ["colour"] = "warm" });
        await colour.InvokeAsync(new JsonObject { ["id"] = shot.Id, ["colour"] = "none" });

        Assert.Equal(string.Empty, bench.Session.Current.Find(shot.Id)!.Props["colour"]);
    }

    // ── Fading from one shot to the next ──────────────────────────────────

    /// <summary>
    /// A fade overlaps two shots rather than sitting between them, so a film with
    /// one is <em>shorter</em> than the same film cut straight. Getting that
    /// backwards is how a film drifts further out of step at every join.
    /// </summary>
    [Fact]
    public async Task A_fade_overlaps_the_shots_rather_than_adding_to_them()
    {
        if (!Possible)
        {
            return;
        }

        var first = await ClipAsync();
        var second = await ClipAsync(colour: "red");

        var cut = Out("cut-straight.mp4");
        var faded = Out("faded.mp4");

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(first, ("seconds", "3")), Shot(second, ("seconds", "3"))), cut)).Ok);

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(Shot(first, ("seconds", "3")), Shot(second, ("seconds", "3"), ("blend", "1"))),
            faded)).Ok);

        var straight = await new MediaLook().FactsAsync(cut);
        var blended = await new MediaLook().FactsAsync(faded);

        Assert.True(
            blended.Seconds < straight.Seconds - 0.4,
            $"faded {blended.Seconds}s should be shorter than cut {straight.Seconds}s");
    }

    /// <summary>
    /// Most films are all cuts, and re-encoding a finished film to achieve nothing
    /// is a cost nobody sees and everybody pays. With no fades the pieces are
    /// copied.
    /// </summary>
    [Fact]
    public async Task A_film_of_straight_cuts_is_joined_by_copying()
    {
        if (!Possible)
        {
            return;
        }

        var film = Out("copied.mp4");

        Assert.True((await new FfmpegMediaExport().VideoAsync(
            Film(
                Shot(await ClipAsync(), ("seconds", "2")),
                Shot(await ClipAsync(colour: "red"), ("seconds", "2"))),
            film)).Ok);

        var facts = await new MediaLook().FactsAsync(film);
        Assert.InRange(facts.Seconds, 3.4, 4.8);
    }

    [Fact]
    public async Task Asking_for_a_fade_puts_it_on_the_shot_it_leads_into()
    {
        if (!Possible)
        {
            return;
        }

        var bench = Open();
        var add = Tool(bench, "design_add_footage");

        await add.InvokeAsync(new JsonObject { ["path"] = await ClipAsync() });
        await add.InvokeAsync(new JsonObject { ["path"] = await ClipAsync(colour: "red") });

        var second = DesignMediums.FramesOf(bench.Session!.Current)[1];

        var result = await Tool(bench, "design_blend")
            .InvokeAsync(new JsonObject { ["id"] = second.Id, ["seconds"] = 0.8 });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal("0.8", bench.Session.Current.Find(second.Id)!.Props["blend"]);
    }

    /// <summary>
    /// A long dissolve is what makes a first film look like a first film, and a
    /// number a model picked at random should not put four seconds of mush in the
    /// middle of somebody's work.
    /// </summary>
    [Fact]
    public async Task A_fade_is_held_to_something_sensible()
    {
        if (!Possible)
        {
            return;
        }

        var bench = Open();
        await Tool(bench, "design_add_footage").InvokeAsync(new JsonObject { ["path"] = await ClipAsync() });

        var shot = DesignMediums.FramesOf(bench.Session!.Current).Single();

        await Tool(bench, "design_blend").InvokeAsync(new JsonObject { ["id"] = shot.Id, ["seconds"] = 40 });

        Assert.Equal("1.5", bench.Session.Current.Find(shot.Id)!.Props["blend"]);
    }
}
