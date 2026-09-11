using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Turning a design into a file somebody can keep.
///
/// Video played and produced nothing; sound played and produced nothing. Both
/// renderers said so honestly in their own comments and neither could do anything
/// about it, because that is a job for an encoder and there was not one.
///
/// The tests that need a real encoder say so and stand down without it, so the
/// suite stays green on a machine that has none — but they are real end-to-end
/// runs when it is there, because an export that has never produced a file is not
/// an export.
/// </summary>
public sealed class MediaExportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-export-tests", Guid.NewGuid().ToString("N"));

    public MediaExportTests() => Directory.CreateDirectory(_root);

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

    private static bool EncoderHere => File.Exists(FfmpegMediaExport.Find());

    /// <summary>
    /// The tripwire for the four end-to-end tests below.
    ///
    /// They stand down without an encoder, which keeps them from failing for a
    /// reason that is not about this code — and that is exactly how all four sat
    /// here reporting success while producing no file at all. Four silent greens
    /// look identical to four real ones. This is the one test that goes red
    /// instead, so the run says which it was. xunit 2.9 has no conditional skip,
    /// so an explicit failure is the honest version of it.
    /// </summary>
    [Fact]
    public void The_encoder_is_here_and_the_end_to_end_tests_really_ran()
        => Assert.True(
            EncoderHere,
            $"No encoder found, so the four export tests stood down and proved nothing. "
            + $"Looked for '{FfmpegMediaExport.Find()}'. Install FFmpeg to run them.");

    private string Out(string name) => Path.Combine(_root, name);

    /// <summary>A second of quiet, as a real file the encoder made.</summary>
    private async Task<string> ToneAsync()
    {
        var path = Out($"tone-{Guid.NewGuid():N}.m4a");

        var made = await new FfmpegMediaExport().SoundAsync(
            WithSound(await SilenceAsync()), path);

        Assert.True(made.Ok, made.Problem);
        return path;
    }

    private async Task<string> SilenceAsync()
    {
        // Made by the encoder rather than checked in, so the test does not carry a
        // binary and does not depend on one being decodable years from now.
        var wav = Out($"silence-{Guid.NewGuid():N}.wav");

        var start = new System.Diagnostics.ProcessStartInfo(FfmpegMediaExport.Find())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        foreach (var argument in new[]
                 {
                     "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono", "-t", "1", wav,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();

        return "data:audio/wav;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(wav));
    }

    private static DesignDocument WithSound(params string[] sources)
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Sound);

        foreach (var source in sources)
        {
            document = document.Add(DesignNode.New(
                DesignNodeKind.Sound, null, ("text", "A sound"), ("src", source)));
        }

        return document;
    }

    private static DesignDocument WithShots(params (string Words, int Seconds)[] shots)
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Motion);

        foreach (var shot in shots)
        {
            document = document.Add(DesignNode.New(
                DesignNodeKind.Frame, null,
                ("text", shot.Words),
                ("seconds", shot.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        return document;
    }

    // ── What happens without anything to save ─────────────────────────────

    [Fact]
    public async Task Saving_a_sound_with_no_sounds_says_so()
    {
        var result = await new FfmpegMediaExport().SoundAsync(
            DesignDocument.Blank(medium: DesignMedium.Sound), Out("nothing.m4a"));

        Assert.False(result.Ok);
        Assert.Contains("nothing", result.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_a_video_with_no_shots_says_so()
    {
        var result = await new FfmpegMediaExport().VideoAsync(
            DesignDocument.Blank(medium: DesignMedium.Motion), Out("nothing.mp4"));

        Assert.False(result.Ok);
        Assert.Contains("no shots", result.Problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A remote address is left out rather than fetched: an export is not the place
    /// to start making network requests to a string a model may have written.
    /// </summary>
    [Fact]
    public async Task A_sound_that_lives_somewhere_else_is_left_out()
    {
        var result = await new FfmpegMediaExport().SoundAsync(
            WithSound("https://example.com/music.mp3"), Out("remote.m4a"));

        Assert.False(result.Ok);
        Assert.Contains("could be read", result.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_source_that_is_not_the_base64_it_claims_is_left_out()
    {
        var result = await new FfmpegMediaExport().SoundAsync(
            WithSound("data:audio/wav;base64,????not base64????"), Out("bad.m4a"));

        Assert.False(result.Ok);
    }

    [Fact]
    public void An_encoder_that_is_not_there_still_gives_a_path_to_name()
        => Assert.False(string.IsNullOrWhiteSpace(FfmpegMediaExport.Find()));

    // ── Real files ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_running_order_becomes_one_audio_file()
    {
        if (!EncoderHere)
        {
            return;
        }

        var silence = await SilenceAsync();
        var path = Out("running-order.m4a");

        var result = await new FfmpegMediaExport().SoundAsync(WithSound(silence, silence), path);

        Assert.True(result.Ok, result.Problem);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public async Task Shots_become_one_video_file()
    {
        if (!EncoderHere)
        {
            return;
        }

        var path = Out("film.mp4");

        var result = await new FfmpegMediaExport().VideoAsync(
            WithShots(("The first thing", 1), ("The second thing", 1)), path);

        Assert.True(result.Ok, result.Problem);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    /// <summary>
    /// Words come from a person, so colons, quotes and backslashes turn up — and
    /// all three are syntax to the encoder's filter parser. A shot titled
    /// <c>Act 1: "the fall"</c> must produce a file rather than an error.
    /// </summary>
    [Fact]
    public async Task Words_the_encoder_would_choke_on_still_produce_a_file()
    {
        if (!EncoderHere)
        {
            return;
        }

        var path = Out("awkward.mp4");

        var result = await new FfmpegMediaExport().VideoAsync(
            WithShots((@"Act 1: ""the fall"" \ 50% off", 1)), path);

        Assert.True(result.Ok, result.Problem);
        Assert.True(new FileInfo(path).Length > 0);
    }

    /// <summary>
    /// Timing is in the file rather than only in the preview. A four-second shot
    /// at half speed is eight seconds of video, and an export that ignored that
    /// would quietly disagree with what was on screen.
    /// </summary>
    [Fact]
    public async Task A_shot_played_slowly_makes_a_longer_film()
    {
        if (!EncoderHere)
        {
            return;
        }

        var quick = Out("quick.mp4");
        var slow = Out("slow.mp4");

        await new FfmpegMediaExport().VideoAsync(WithShots(("Same words", 4)), quick);

        var slowed = DesignDocument.Blank(medium: DesignMedium.Motion)
            .Add(DesignNode.New(DesignNodeKind.Frame, null,
                ("text", "Same words"), ("seconds", "4"), ("rate", "0.5")));

        await new FfmpegMediaExport().VideoAsync(slowed, slow);

        Assert.True(new FileInfo(slow).Length > new FileInfo(quick).Length);
    }

    /// <summary>
    /// A movement the encoder refuses is a film that does not export, so both of the ones
    /// that only work on a still are run for real rather than checked as strings.
    ///
    /// This is the test the filter expressions needed: `zoompan` takes its length in frames
    /// and its position as expressions, and a wrong one is not a worse-looking shot — it is
    /// "Error while filtering" and no file at all.
    /// </summary>
    [Theory]
    [InlineData("grow")]
    [InlineData("drift")]
    [InlineData("fade")]
    public async Task A_shot_that_moves_still_produces_a_film(string move)
    {
        if (!EncoderHere)
        {
            return;
        }

        var path = Out($"moving-{move}.mp4");

        var moving = DesignDocument.Blank(medium: DesignMedium.Motion)
            .Add(DesignNode.New(DesignNodeKind.Frame, null,
                ("text", "A held picture"), ("seconds", "2"), ("move", move)));

        var result = await new FfmpegMediaExport().VideoAsync(moving, path);

        Assert.True(result.Ok, result.Problem);
        Assert.True(new FileInfo(path).Length > 0);
    }
}
