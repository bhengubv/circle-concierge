using System.Diagnostics;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A card the export draws has to actually come out.
///
/// **Found on the phone, and it was never an Android bug.** Writing one still image
/// with ffmpeg means saying `-update 1` — otherwise the output path is treated as a
/// numbered sequence with no number in it, and ffmpeg says so every single time:
/// *"Use a pattern such as %03d for an image sequence or use the -update option"*.
///
/// Whether it then writes the frame anyway is a matter of how forgiving that build
/// is. The desktop encoder writes it and warns. The Android build, configured
/// `--enable-small`, does not: `frame= 0`, *"Finishing stream without any data
/// written to it"*, and an export that then failed three steps later complaining
/// about a file that was never created — the symptom, a long way from the cause.
///
/// So the command was wrong on every head, and only the strictest one punished it.
/// That is the useful kind of thing a second platform finds, and it is the reason
/// this test asserts the file exists rather than asserting the flag is in the
/// argument list: a flag nobody checks the effect of is how this got here.
/// </summary>
public sealed class CardsAreActuallyWrittenTests
{
    private static string? Ffmpeg()
    {
        foreach (var candidate in new[] { "ffmpeg", "ffmpeg.exe" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (probe is null)
                {
                    continue;
                }

                probe.WaitForExit(10_000);

                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Next candidate.
            }
        }

        return null;
    }

    /// <summary>
    /// A tripwire rather than a skip, for the reason this repository keeps writing
    /// down: four export tests once stood down when the encoder was missing and
    /// reported success for weeks.
    /// </summary>
    [Fact]
    public void There_is_an_encoder_to_test_against()
        => Assert.False(Ffmpeg() is null, "no encoder on this machine, so this proves nothing");

    /// <summary>
    /// The video export draws a card per shot. If a card is empty there is no film.
    /// </summary>
    [Fact]
    public async Task A_shot_becomes_a_film_with_something_in_it()
    {
        if (Ffmpeg() is null)
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"card-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var shot = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Sports Day"));
        var design = DesignDocument.Blank(medium: DesignMedium.Motion).Add(shot);

        var output = Path.Combine(folder, "film.mp4");
        var result = await new FfmpegMediaExport().VideoAsync(design, output);

        Assert.True(result.Ok, result.Problem);
        Assert.True(File.Exists(output), "no film was written");
        Assert.True(new FileInfo(output).Length > 0, "the film is empty");
    }

    /// <summary>
    /// **And the words are on it**, which is a different claim from "a file came out".
    ///
    /// A shot whose font cannot be found keeps its ground and loses its words, on
    /// purpose — losing a title beats losing the film. The cost is that an export
    /// with no font at all still succeeds, still writes an mp4, and still says
    /// "Saved to …", and what it hands somebody is a plain coloured rectangle held
    /// for four seconds. That is the shape of defect this repository exists to
    /// remove, and asserting on the file's existence does not catch it.
    ///
    /// So the frame is pulled back out and looked at: a card with a title on it is
    /// not one flat colour. Crude, and it is the difference between proving the
    /// export ran and proving it drew.
    /// </summary>
    [Fact]
    public async Task And_the_words_are_actually_drawn_on_it()
    {
        if (Ffmpeg() is not { } ffmpeg)
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"drawn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var design = DesignDocument.Blank(medium: DesignMedium.Motion)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Sports Day")));

        var film = Path.Combine(folder, "film.mp4");
        var made = await new FfmpegMediaExport().VideoAsync(design, film);

        Assert.True(made.Ok, made.Problem);

        // One frame back out, as raw greys, so "is there anything but the ground on
        // this" is a question about bytes rather than about a picture format.
        var frame = Path.Combine(folder, "frame.gray");

        using var pull = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList =
            {
                "-v", "error", "-y", "-i", film, "-frames:v", "1",
                "-vf", "scale=160:90", "-f", "rawvideo", "-pix_fmt", "gray", frame,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var said = await pull.StandardError.ReadToEndAsync();
        await pull.WaitForExitAsync();

        Assert.True(File.Exists(frame), $"no frame came back out of the film: {said}");

        var pixels = await File.ReadAllBytesAsync(frame);

        Assert.True(
            pixels.Distinct().Count() > 1,
            "every pixel is the same shade, so the card is blank and the title was lost");
    }

    /// <summary>
    /// And there is a font to draw with on Android, which is the head where there was
    /// not one.
    ///
    /// The list is checked here; that these files are actually on a device was checked
    /// on the running emulator with `ls /system/fonts` — all three are there. Neither
    /// half proves the other, and both are cheap.
    /// </summary>
    [Fact]
    public void There_is_a_font_named_for_android()
    {
        var candidates = FfmpegMediaExport.TypefaceCandidates().ToList();

        Assert.Contains(candidates, path => path.StartsWith("/system/fonts/", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the encoder itself is told to write one still rather than a sequence.
    ///
    /// Asserted against the encoder rather than against our argument list: the point
    /// is that the file appears, and a version that changes its mind about this
    /// should turn this red rather than pass because the flag is still being sent.
    /// </summary>
    [Fact]
    public async Task One_still_image_is_asked_for_as_one_still_image()
    {
        if (Ffmpeg() is not { } ffmpeg)
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"still-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var card = Path.Combine(folder, "card.png");

        using var run = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList =
            {
                "-y", "-f", "lavfi", "-i", "color=c=0xFFFFFF:s=1280x720:d=1",
                "-frames:v", "1", "-update", "1", card,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var said = await run.StandardError.ReadToEndAsync();
        await run.WaitForExitAsync();

        Assert.True(File.Exists(card), $"no card was written: {said}");

        // And the encoder is no longer telling us the command is wrong. That warning
        // was on screen for every export this product ever ran and nobody read it.
        Assert.DoesNotContain("-update option", said, StringComparison.Ordinal);
    }
}
