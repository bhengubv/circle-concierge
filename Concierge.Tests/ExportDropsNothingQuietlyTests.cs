using System.Diagnostics;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// An export that leaves something out has to say so.
///
/// **Found by exporting a running order on the running app and reading the answer.** Six
/// tracks went in — three real recordings and three files that claim to be mp3s and hold two
/// kilobytes of zeros — and a file came out running 7.47 seconds, which is exactly the three
/// real ones. The message was "Saved to …\design.m4a." and nothing more.
///
/// Half the running order was missing and the export called that a success. On a marketplace
/// that is discovered by whoever downloads it.
///
/// **And the first fix was in the wrong place**, which is why this test uses real files. A
/// carried track always materialises — the bytes are in the document — so counting failures
/// at that step counted nothing. The drop happens inside the encoder's concat, in silence.
/// What catches it is asking the encoder whether each piece decodes at all.
///
/// Real encoder runs, no stubs of the thing under test. The file that must be rejected is
/// genuinely undecodable and the one that must survive is genuinely audio.
/// </summary>
public sealed class ExportDropsNothingQuietlyTests
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
    /// **A tripwire, not a skip.** Four export tests in this repository once stood down when
    /// the encoder was missing and reported success for weeks, so a run with no encoder says
    /// so out loud rather than looking identical to a run that proved something.
    /// </summary>
    [Fact]
    public void There_is_an_encoder_to_test_against()
        => Assert.False(Ffmpeg() is null, "no encoder on this machine, so the export tests prove nothing");

    [Fact]
    public async Task A_track_the_encoder_cannot_read_is_counted_and_named()
    {
        if (Ffmpeg() is not { } ffmpeg)
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        // One real second of tone, made by the encoder itself.
        var good = Path.Combine(folder, "good.mp3");

        using (var making = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", good },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!)
        {
            await making.WaitForExitAsync();
        }

        Assert.True(File.Exists(good), "the encoder did not make the good file");

        // And one that claims to be an mp3 and is not — the exact shape that fooled the
        // running app: right name, right extension, plausible size, no audio in it.
        var bad = Path.Combine(folder, "bad.mp3");
        await File.WriteAllBytesAsync(bad, new byte[2048]);

        var order = DesignDocument.Blank(medium: DesignMedium.Sound)
            .Add(DesignNode.New(
                DesignNodeKind.Sound, null, ("text", "Real"),
                ("src", $"data:audio/mpeg;base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(good))}")))
            .Add(DesignNode.New(
                DesignNodeKind.Sound, null, ("text", "Not really"),
                ("src", $"data:audio/mpeg;base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(bad))}")));

        var output = Path.Combine(folder, "out.m4a");
        var result = await new FfmpegMediaExport().SoundAsync(order, output);

        Assert.True(result.Ok, result.Problem);
        Assert.True(File.Exists(output), "no file was written");

        // The whole point: it says what is not in there.
        Assert.False(string.IsNullOrWhiteSpace(result.Left), "a track was dropped and nothing said so");
        Assert.Contains("1 of 2", result.Left!, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a running order where everything is readable says nothing, so the ordinary case
    /// stays quiet and the warning keeps its meaning.
    /// </summary>
    [Fact]
    public async Task But_a_running_order_that_is_all_there_says_nothing()
    {
        if (Ffmpeg() is not { } ffmpeg)
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var good = Path.Combine(folder, "good.mp3");

        using (var making = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", good },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!)
        {
            await making.WaitForExitAsync();
        }

        var carried = $"data:audio/mpeg;base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(good))}";

        var order = DesignDocument.Blank(medium: DesignMedium.Sound)
            .Add(DesignNode.New(DesignNodeKind.Sound, null, ("text", "One"), ("src", carried)))
            .Add(DesignNode.New(DesignNodeKind.Sound, null, ("text", "Two"), ("src", carried)));

        var result = await new FfmpegMediaExport().SoundAsync(order, Path.Combine(folder, "out.m4a"));

        Assert.True(result.Ok, result.Problem);
        Assert.Null(result.Left);
    }
}
