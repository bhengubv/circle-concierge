using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Concierge.Shared.Design;

/// <summary>What is in a media file, in words.</summary>
/// <param name="Ok">Whether anything could be read.</param>
/// <param name="Seconds">How long it runs, or 0.</param>
/// <param name="Description">The streams, one per line, in plain words.</param>
/// <param name="Problem">Why not, when it could not be read.</param>
public sealed record MediaFacts(bool Ok, double Seconds, string Description, string? Problem);

/// <summary>
/// Letting a model look inside a media file.
///
/// Taken from Diffusion Studio, whose README puts the argument better than a
/// paraphrase would: these are "the inspection tools an agent needs to work with
/// media it cannot watch". Found by cloning the six and reading them rather than
/// remembering them, and it named a hole exactly.
///
/// Concierge had vision input, an encoder already wired, and **no way for a model
/// to learn anything at all about an audio or video file.** Told "the second shot
/// is too long", it could not check. Handed a track, it could not tell whether the
/// file was silence. It could put media into a design and export it, and was blind
/// to everything in between.
///
/// **What is here is the half that costs nothing**, because `FfmpegMediaExport`
/// already finds and runs the encoder: how long a file is, what streams are in it,
/// a frame or a row of frames as pictures the vision channel already carries, and
/// whether audio is silent.
///
/// **What is deliberately not here:** transcription, and asking a model about
/// footage. Both need a model that can hear, which is a different decision and a
/// different dependency. Named so the small half does not drag the large half in
/// behind it.
///
/// Every one of these only reads. That is why none of them asks — the same reason
/// `read_file` does not and `write_file` does.
/// </summary>
public sealed class MediaLook
{
    private readonly string _ffmpeg;

    /// <param name="ffmpegPath">Where the encoder is. Found when not given.</param>
    public MediaLook(string? ffmpegPath = null)
        => _ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? FfmpegMediaExport.Find() : ffmpegPath;

    /// <summary>Whether this machine can look inside anything.</summary>
    public static bool Possible => File.Exists(FfmpegMediaExport.Find());

    /// <summary>
    /// What is in the file.
    ///
    /// Read from the encoder's own report rather than a parser of our own. ffmpeg
    /// prints what it found to standard error before it does anything, so a plain
    /// run with no output file is a description — and it is the same description
    /// the export would be working from, which is the point of not writing a
    /// second one.
    /// </summary>
    public async Task<MediaFacts> FactsAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new MediaFacts(false, 0, string.Empty, $"There is no file at {path}.");
        }

        var (ran, said) = await RunAsync(["-hide_banner", "-i", path], cancellationToken).ConfigureAwait(false);

        // A bare -i with no output always "fails" — "At least one output file must
        // be specified" — and that failure carries exactly the report wanted. So
        // the exit code says nothing here and is not consulted.
        _ = ran;

        var seconds = DurationIn(said);
        var streams = new StringBuilder();

        foreach (var line in said.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Stream #", StringComparison.Ordinal))
            {
                streams.AppendLine(trimmed);
            }
        }

        if (streams.Length == 0 && seconds == 0)
        {
            return new MediaFacts(false, 0, string.Empty, $"Nothing in {Path.GetFileName(path)} could be read.");
        }

        return new MediaFacts(true, seconds, streams.ToString().TrimEnd(), null);
    }

    /// <summary>
    /// A row of frames from across the whole file, as one picture.
    ///
    /// One image rather than several on purpose. A model given eight separate
    /// frames spends eight times the context and still has to work out the order;
    /// a filmstrip is one picture that reads left to right, which is how somebody
    /// scrubbing a video already thinks.
    /// </summary>
    /// <param name="path">The video.</param>
    /// <param name="across">How many frames, spread evenly.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<(byte[]? Png, string? Problem)> FilmstripAsync(
        string path, int across = 6, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(across);

        if (!File.Exists(path))
        {
            return (null, $"There is no file at {path}.");
        }

        var facts = await FactsAsync(path, cancellationToken).ConfigureAwait(false);

        if (!facts.Ok || facts.Seconds <= 0)
        {
            return (null, facts.Problem ?? "That file has no length to take frames from.");
        }

        var into = Path.Combine(
            Path.GetTempPath(), "concierge-look", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(into);
        var sheet = Path.Combine(into, "strip.png");

        try
        {
            // One frame every so many seconds, tiled in a single row. The rate is
            // worked out from the length rather than fixed, so a ten-second clip
            // and an hour-long one both give the same number of frames.
            var every = Math.Max(facts.Seconds / across, 0.001);

            var (ran, said) = await RunAsync(
                [
                    "-hide_banner", "-y", "-i", path,
                    "-vf", string.Create(CultureInfo.InvariantCulture, $"fps=1/{every},scale=320:-1,tile={across}x1"),
                    "-frames:v", "1",
                    sheet,
                ],
                cancellationToken).ConfigureAwait(false);

            if (!ran || !File.Exists(sheet))
            {
                return (null, Last(said) ?? "No frames could be taken from that.");
            }

            return (await File.ReadAllBytesAsync(sheet, cancellationToken).ConfigureAwait(false), null);
        }
        finally
        {
            try
            {
                Directory.Delete(into, recursive: true);
            }
            catch (IOException)
            {
                // Swept with the temp folder regardless.
            }
        }
    }

    /// <summary>
    /// Whether there is anything to hear.
    ///
    /// The specific question this answers is one that actually comes up: a track
    /// that downloaded wrong, a shot recorded with the microphone muted, an export
    /// that came out quiet. All of them look completely normal in a file listing
    /// and in the streams above.
    ///
    /// Measured with the encoder's own loudness reading rather than by decoding
    /// samples here. -91dB is the floor it reports for true digital silence; -60
    /// is the threshold used, which is quiet enough that anything above it is
    /// audible and anything below is not.
    /// </summary>
    public async Task<(bool Silent, double Loudness, string? Problem)> SilentAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return (false, 0, $"There is no file at {path}.");
        }

        var (_, said) = await RunAsync(
            ["-hide_banner", "-i", path, "-af", "volumedetect", "-f", "null", "-"],
            cancellationToken).ConfigureAwait(false);

        foreach (var line in said.Split('\n'))
        {
            var at = line.IndexOf("mean_volume:", StringComparison.Ordinal);

            if (at < 0)
            {
                continue;
            }

            var rest = line[(at + "mean_volume:".Length)..].Replace("dB", string.Empty, StringComparison.Ordinal);

            if (double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mean))
            {
                return (mean <= -60, mean, null);
            }
        }

        return (false, 0, "That file has no sound in it to measure.");
    }

    /// <summary>
    /// Runs the encoder and hands back everything it said.
    ///
    /// Its report goes to standard error even when nothing is wrong, which is why
    /// this keeps both rather than treating one as the failure channel.
    /// </summary>
    private async Task<(bool Ok, string Said)> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);

            if (process is null)
            {
                return (false, "The encoder would not start.");
            }

            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode == 0,
                await errors.ConfigureAwait(false) + await output.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "There is no encoder on this machine.");
        }
    }

    /// <summary>How long it runs, from the encoder's own line.</summary>
    private static double DurationIn(string said)
    {
        var at = said.IndexOf("Duration:", StringComparison.Ordinal);

        if (at < 0)
        {
            return 0;
        }

        var stamp = said[(at + "Duration:".Length)..].TrimStart();
        var end = stamp.IndexOf(',');

        if (end > 0)
        {
            stamp = stamp[..end];
        }

        return TimeSpan.TryParse(stamp.Trim(), CultureInfo.InvariantCulture, out var span)
            ? span.TotalSeconds
            : 0;
    }

    /// <summary>The last thing it said, which is the reason.</summary>
    private static string? Last(string said)
        => said.Split('\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0);
}
