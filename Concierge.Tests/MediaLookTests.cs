using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Letting a model look inside a media file.
///
/// Taken from Diffusion Studio, whose README makes the argument better than a
/// paraphrase: these are "the inspection tools an agent needs to work with media
/// it cannot watch". Found by cloning the six and reading them rather than
/// remembering them.
///
/// It named a real hole. Concierge had vision input, an encoder already wired, and
/// no way for a model to learn anything at all about an audio or video file. Told
/// "the second shot is too long" it could not check; handed a track it could not
/// tell whether the file was silence.
///
/// **These are real end-to-end runs against files the encoder makes**, for the
/// reason the export tests learned the hard way: four of those stood down on a
/// missing encoder and reported success for weeks. One test here goes red when
/// there is no encoder, so a green run means the work happened.
/// </summary>
public sealed class MediaLookTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-look-tests", Guid.NewGuid().ToString("N"));

    public MediaLookTests() => Directory.CreateDirectory(_root);

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

    private string Out(string name) => Path.Combine(_root, name);

    /// <summary>A file the encoder makes, so no binary is checked in.</summary>
    private async Task<string> MakeAsync(string name, params string[] arguments)
    {
        var path = Out(name);

        var start = new System.Diagnostics.ProcessStartInfo(FfmpegMediaExport.Find())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        string[] all = ["-hide_banner", "-y", .. arguments, path];

        foreach (var argument in all)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();

        return path;
    }

    private Task<string> SilenceAsync(int seconds = 2)
        => MakeAsync($"quiet-{Guid.NewGuid():N}.wav", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", seconds.ToString());

    private Task<string> ToneAsync(int seconds = 2)
        => MakeAsync($"tone-{Guid.NewGuid():N}.wav", "-f", "lavfi", "-i", "sine=frequency=440:r=44100", "-t", seconds.ToString());

    private Task<string> ClipAsync(int seconds = 6)
        => MakeAsync(
            $"clip-{Guid.NewGuid():N}.mp4",
            "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=10:duration={seconds}",
            "-pix_fmt", "yuv420p");

    // ── The tripwire ──────────────────────────────────────────────────────

    /// <summary>
    /// The lesson from the export tests, applied before it can be repeated: four
    /// of those returned early on a missing encoder and reported success. A green
    /// run has to mean the work happened, so this is the one test that goes red
    /// when it did not.
    /// </summary>
    [Fact]
    public void The_encoder_is_here_so_these_tests_really_ran()
        => Assert.True(
            MediaLook.Possible,
            $"No encoder, so nothing below looked inside anything. Looked for '{FfmpegMediaExport.Find()}'.");

    // ── What is in it ─────────────────────────────────────────────────────

    [Fact]
    public async Task How_long_a_file_runs_is_read_from_the_file()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var facts = await new MediaLook().FactsAsync(await ToneAsync(3));

        Assert.True(facts.Ok, facts.Problem);
        Assert.InRange(facts.Seconds, 2.5, 3.5);
    }

    [Fact]
    public async Task What_streams_are_in_it_come_back_in_words()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var facts = await new MediaLook().FactsAsync(await ClipAsync());

        Assert.True(facts.Ok, facts.Problem);
        Assert.Contains("Video:", facts.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_is_not_there_says_so_rather_than_guessing()
    {
        var facts = await new MediaLook().FactsAsync(Out("nothing-here.mp4"));

        Assert.False(facts.Ok);
        Assert.Contains("no file", facts.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A text file with a media extension is the ordinary way this goes wrong — a
    /// download that returned an error page. It must say so rather than reporting
    /// zero seconds as though that were a fact about the audio.
    /// </summary>
    [Fact]
    public async Task Something_that_is_not_media_at_all_is_refused()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var pretend = Out("not-really.mp3");
        await File.WriteAllTextAsync(pretend, "<html><body>404 Not Found</body></html>");

        var facts = await new MediaLook().FactsAsync(pretend);

        Assert.False(facts.Ok);
    }

    // ── Whether there is anything to hear ─────────────────────────────────

    /// <summary>
    /// The question this exists for. A track that downloaded wrong and a shot
    /// recorded with the microphone muted both look entirely normal in a file
    /// listing and in the stream description above.
    /// </summary>
    [Fact]
    public async Task Silence_is_recognised_as_silence()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var (silent, loudness, problem) = await new MediaLook().SilentAsync(await SilenceAsync());

        Assert.Null(problem);
        Assert.True(silent, $"loudness was {loudness}dB");
    }

    [Fact]
    public async Task A_file_with_sound_in_it_is_not_called_silent()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var (silent, loudness, problem) = await new MediaLook().SilentAsync(await ToneAsync());

        Assert.Null(problem);
        Assert.False(silent, $"loudness was {loudness}dB");
    }

    [Fact]
    public async Task A_video_with_no_sound_says_there_is_nothing_to_measure()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var (_, _, problem) = await new MediaLook().SilentAsync(await ClipAsync());

        Assert.NotNull(problem);
    }

    // ── Frames ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Frames_come_back_as_one_picture()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var (png, problem) = await new MediaLook().FilmstripAsync(await ClipAsync(), across: 4);

        Assert.Null(problem);
        Assert.NotNull(png);

        // A real PNG, not an empty file the encoder left behind.
        Assert.True(png!.Length > 1000, $"{png.Length} bytes");
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4).ToArray());
    }

    /// <summary>
    /// One picture rather than several, deliberately: a model given eight separate
    /// frames spends eight times the context and still has to work out the order.
    /// A strip reads left to right, which is how somebody scrubbing already
    /// thinks.
    /// </summary>
    [Fact]
    public async Task More_frames_asked_for_makes_a_wider_strip_not_more_pictures()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var clip = await ClipAsync();
        var look = new MediaLook();

        var (few, _) = await look.FilmstripAsync(clip, across: 2);
        var (many, _) = await look.FilmstripAsync(clip, across: 8);

        Assert.NotNull(few);
        Assert.NotNull(many);
        Assert.True(many!.Length > few!.Length, $"{many.Length} should exceed {few.Length}");
    }

    [Fact]
    public async Task A_file_with_no_length_gives_no_frames_and_says_why()
    {
        var (png, problem) = await new MediaLook().FilmstripAsync(Out("missing.mp4"));

        Assert.Null(png);
        Assert.NotNull(problem);
    }

    // ── The tools ─────────────────────────────────────────────────────────

    [Fact]
    public void All_three_only_read_so_none_of_them_asks()
        => Assert.All(
            new MediaLookToolSource(new MediaLook(), new CapturedImages()).Tools,
            tool => Assert.True(tool.IsReadOnly, $"{tool.Name} is not read-only"));

    /// <summary>
    /// A tool that produces a picture nothing can carry is a tool that reports
    /// success and shows nobody anything.
    /// </summary>
    [Fact]
    public void Without_somewhere_to_put_a_picture_the_filmstrip_is_not_offered()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var names = new MediaLookToolSource(new MediaLook()).Tools.Select(tool => tool.Name).ToList();

        Assert.DoesNotContain("media_frames", names);
        Assert.Contains("media_facts", names);
    }

    [Fact]
    public async Task The_filmstrip_puts_its_picture_where_the_next_turn_will_find_it()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var captured = new CapturedImages();
        var tools = new MediaLookToolSource(new MediaLook(), captured);

        var result = await tools.Tools.Single(tool => tool.Name == "media_frames")
            .InvokeAsync(new JsonObject { ["path"] = await ClipAsync() });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Single(captured.TakeAll());
    }

    /// <summary>
    /// Bounded rather than trusted. A model asking for two hundred frames gets a
    /// strip too wide to read and a context window spent on it.
    /// </summary>
    [Fact]
    public async Task A_ridiculous_number_of_frames_is_brought_back_to_something_readable()
    {
        if (!MediaLook.Possible)
        {
            return;
        }

        var captured = new CapturedImages();

        var result = await new MediaLookToolSource(new MediaLook(), captured).Tools
            .Single(tool => tool.Name == "media_frames")
            .InvokeAsync(new JsonObject { ["path"] = await ClipAsync(), ["frames"] = 200 });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Contains("12 frames", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_about_nothing_says_so_rather_than_running_the_encoder()
    {
        var result = await new MediaLookToolSource(new MediaLook(), new CapturedImages()).Tools
            .Single(tool => tool.Name == "media_facts")
            .InvokeAsync(new JsonObject { ["path"] = "" });

        Assert.False(result.Success);
    }
}
