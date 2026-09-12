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

/// <summary>What a file says about itself. Anything missing is empty rather than guessed.</summary>
/// <param name="Ok">Whether it said anything at all.</param>
/// <param name="Title">What the track is called.</param>
/// <param name="Artist">Who made it.</param>
/// <param name="Album">What it is on.</param>
/// <param name="Genre">What kind of thing it is.</param>
/// <param name="Year">When it came out, or nought.</param>
public sealed record MediaTags(
    bool Ok, string Title, string Artist, string Album, string Genre, int Year);

/// <summary>How loud a file is, how finely it was recorded, and whether it was squashed.</summary>
/// <param name="Ok">Whether anything could be measured.</param>
/// <param name="Seconds">How long it runs.</param>
/// <param name="Loudness">Average loudness in decibels, which is a negative number.</param>
/// <param name="Peak">The loudest moment, in decibels. Nought is the ceiling.</param>
/// <param name="SampleRate">How often it was sampled, in hertz.</param>
/// <param name="Bits">How finely each sample was recorded, when the encoder says.</param>
/// <param name="BetterThanCd">Sampled more often or more finely than a CD.</param>
/// <param name="Clipped">Its peak is at the ceiling, so it has been squashed flat.</param>
/// <param name="Problem">Why nothing could be measured, when nothing could.</param>
public sealed record AudioQuality(
    bool Ok, double Seconds, double Loudness, double Peak,
    int SampleRate, int Bits, bool BetterThanCd, bool Clipped, string? Problem);

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
    private readonly IEncoderRunner _encoder;

    /// <param name="ffmpegPath">Where the encoder is. Found when not given.</param>
    public MediaLook(string? ffmpegPath = null)
        => _encoder = string.IsNullOrWhiteSpace(ffmpegPath)
            ? Encoders.Runner
            : new ProcessEncoder(ffmpegPath);

    /// <summary>
    /// Whether this machine can look inside anything.
    ///
    /// Asked of the encoder rather than of the filesystem. It was a file check, which
    /// is the same question on every head that runs a program and the wrong one on a
    /// phone, where the encoder is a library inside the APK and there is no path to
    /// test — so the answer was always no and eight tools were absent.
    /// </summary>
    public static bool Possible => Encoders.Here;

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
    /// What a music file says about itself: who made it, what it is on, what it is called.
    ///
    /// Read from the same report `FactsAsync` reads, for the same reason — ffmpeg prints the
    /// file's metadata before it does anything, and a second parser of ours would be one more
    /// thing to disagree with the encoder about.
    ///
    /// Everything is optional and a missing tag is empty rather than guessed. A file with no
    /// artist is a real and common thing, and inventing one from the filename is how a library
    /// ends up with an artist called "01 Track".
    /// </summary>
    public async Task<MediaTags> TagsAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new MediaTags(false, string.Empty, string.Empty, string.Empty, string.Empty, 0);
        }

        var (_, said) = await RunAsync(["-hide_banner", "-i", path], cancellationToken).ConfigureAwait(false);

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in said.Split('\n'))
        {
            // "    title           : Wild Is The Wind". Only lines that are indented and
            // carry a colon, so "Stream #0:0" and "Duration: 00:03:12" are not mistaken for
            // tags — both of which are in the same block and both of which would otherwise
            // land in the library as an artist.
            if (line.Length == 0 || !char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var at = line.IndexOf(':');

            if (at <= 0)
            {
                continue;
            }

            var key = line[..at].Trim();
            var value = line[(at + 1)..].Trim();

            if (key.Length == 0 || value.Length == 0 || key.Contains(' ', StringComparison.Ordinal))
            {
                continue;
            }

            tags.TryAdd(key, value);
        }

        var year = tags.TryGetValue("date", out var when) ? YearIn(when) : 0;

        return new MediaTags(
            tags.Count > 0,
            Tag(tags, "title"),
            Tag(tags, "artist", "album_artist"),
            Tag(tags, "album"),
            Tag(tags, "genre"),
            year);
    }

    private static string Tag(IReadOnlyDictionary<string, string> tags, params string[] names)
    {
        foreach (var name in names)
        {
            if (tags.TryGetValue(name, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// A year out of whatever was written. A date tag is "1965", "1965-03-07" or occasionally
    /// a whole timestamp, and the year is the only part anybody files by.
    /// </summary>
    private static int YearIn(string written)
    {
        var digits = new string(written.TakeWhile(char.IsDigit).ToArray());

        return digits.Length == 4 && int.TryParse(digits, out var year) ? year : 0;
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
    /// How loud it is, how good it is, and whether it is clipped.
    ///
    /// Antra ships an audio analyser and prefers high-quality files. The preferring half is
    /// about choosing between downloads from services nobody here has an account for; the
    /// noticing half is a property of a file on the disk and needs nothing but the encoder.
    ///
    /// **Clipping is the one worth having.** A track whose peak sits at 0dB has been squashed
    /// flat somewhere in its history, and it is the commonest thing wrong with a file that
    /// otherwise looks perfect — the right length, the right tags, the right size.
    /// </summary>
    public async Task<AudioQuality> QualityAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new AudioQuality(false, 0, 0, 0, 0, 0, false, false, $"There is no file at {path}.");
        }

        var (_, said) = await RunAsync(
            ["-hide_banner", "-i", path, "-af", "volumedetect", "-f", "null", "-"],
            cancellationToken).ConfigureAwait(false);

        var mean = After(said, "mean_volume:");
        var peak = After(said, "max_volume:");

        var rate = 0;
        var bits = 0;
        var channels = 0;

        foreach (var line in said.Split('\n'))
        {
            if (!line.Contains("Audio:", StringComparison.Ordinal))
            {
                continue;
            }

            // "Stream #0:0: Audio: flac, 96000 Hz, stereo, s32 (24 bit)". Read from the
            // encoder's own line rather than from the file extension: a .flac can hold
            // anything, and the extension is what somebody renamed it to.
            foreach (var part in line.Split(','))
            {
                var bit = part.Trim();

                if (bit.EndsWith(" Hz", StringComparison.Ordinal)
                    && int.TryParse(bit[..^3].Trim(), out var hz))
                {
                    rate = hz;
                }

                channels = bit switch
                {
                    "mono" => 1,
                    "stereo" => 2,
                    _ => channels,
                };

                var deep = bit.IndexOf(" bit)", StringComparison.Ordinal);

                if (deep > 0)
                {
                    var open = bit.LastIndexOf('(', deep);

                    if (open >= 0 && int.TryParse(bit[(open + 1)..deep].Trim(), out var depth))
                    {
                        bits = depth;
                    }
                }
            }

            break;
        }

        if (rate == 0 && mean == 0 && peak == 0)
        {
            return new AudioQuality(false, 0, 0, 0, 0, 0, false, false, "Nothing in that file could be measured.");
        }

        var facts = await FactsAsync(path, cancellationToken).ConfigureAwait(false);

        // "Hi-res" is anybody's definition, and this is the usual one: better than a CD in
        // either how often it was sampled or how finely. Stated rather than implied so nobody
        // has to guess what the word meant here.
        var better = rate > 44_100 || bits > 16;

        // Within a tenth of a decibel of the ceiling. Not "at 0" exactly: a peak of -0.04 is
        // the same event, and asking for exactly zero finds almost none of them.
        var clipped = peak >= -0.1 && peak != 0;

        return new AudioQuality(true, facts.Seconds, mean, peak, rate, bits, better, clipped, null);
    }

    private static double After(string said, string label)
    {
        foreach (var line in said.Split('\n'))
        {
            var at = line.IndexOf(label, StringComparison.Ordinal);

            if (at < 0)
            {
                continue;
            }

            var rest = line[(at + label.Length)..].Replace("dB", string.Empty, StringComparison.Ordinal);

            if (double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>
    /// Runs the encoder and hands back everything it said.
    ///
    /// Its report goes to standard error even when nothing is wrong, which is why
    /// this keeps both rather than treating one as the failure channel.
    /// </summary>
    /// <summary>
    /// Ask the encoder, however this head asks it.
    ///
    /// This was a `Process.Start` of its own, beside an identical one in the export.
    /// Both go through the one seam now, so a head where the encoder is not a program
    /// needed one implementation rather than two.
    /// </summary>
    private async Task<(bool Ok, string Said)> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var run = await _encoder.RunAsync(arguments, cancellationToken).ConfigureAwait(false);

        return run.Started
            ? (run.Ok, run.Said)
            : (false, "There is no encoder on this machine.");
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
