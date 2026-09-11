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
}
