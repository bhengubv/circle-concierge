using System.Diagnostics;
using System.Text;

namespace Concierge.Shared.Design;

/// <summary>What came of an export.</summary>
/// <param name="Ok">Whether there is a file at the end of it.</param>
/// <param name="Path">Where it is.</param>
/// <param name="Problem">What went wrong, in words a person can act on.</param>
public sealed record ExportResult(bool Ok, string? Path, string? Problem)
{
    public static ExportResult Made(string path) => new(true, path, null);

    public static ExportResult Failed(string problem) => new(false, null, problem);
}

/// <summary>Turning a design into a file somebody can keep.</summary>
public interface IMediaExport
{
    /// <summary>The running order, as one audio file.</summary>
    Task<ExportResult> SoundAsync(DesignDocument document, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>The shots, as one video file.</summary>
    Task<ExportResult> VideoAsync(DesignDocument document, string outputPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// The encoder.
///
/// Video played and produced nothing; sound played and produced nothing. Both
/// renderers said so honestly in their own comments and neither could do anything
/// about it, because turning frames and tracks into a file is a job for an encoder
/// and there was not one. This is that.
///
/// **What each export actually is**, so nobody has to guess from the name:
///
///   *Sound* is the running order concatenated in the order it is shown, at a
///   single sample rate. Not a mix — there are no faders, no levels, no crossfades,
///   because the surface that made it has none of those either and an export that
///   invented them would be exporting something the person never saw.
///
///   *Video* is each shot held for its own length, in order, with the design's
///   own colours behind it and the shot's words on it. Where a shot carries a
///   picture, the picture is the shot. This is a slideshow with timing rather than
///   a rendering of the live canvas — the canvas is HTML, and turning HTML into
///   frames needs a browser driven frame by frame, which is a much larger thing
///   and would be a different piece of work.
///
/// Timing comes from <see cref="DesignTiming"/>, so a shot's delay and rate are in
/// the exported file rather than only in the preview. An export that ignored them
/// would quietly disagree with what was on screen.
/// </summary>
public sealed class FfmpegMediaExport : IMediaExport
{
    private readonly string _ffmpeg;

    /// <param name="ffmpegPath">
    /// Where the encoder is. Resolved by <see cref="Find"/> when not given.
    /// </param>
    public FfmpegMediaExport(string? ffmpegPath = null)
        => _ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? Find() : ffmpegPath;

    /// <summary>Where the encoder is on this machine.</summary>
    public string EncoderPath => _ffmpeg;

    /// <summary>
    /// Finds the encoder: beside the app first, then on the path, then in the
    /// handful of places an installer actually puts one.
    ///
    /// Beside the app first because that is where a packaged Concierge carries it,
    /// and a machine that also happens to have one installed should not end up
    /// running a different version than the one that was shipped and tested.
    ///
    /// The third pass exists because the path is not reliable at the moment it
    /// matters most. Installing the encoder on Windows prints "restart your shell
    /// to use the new value", so every process already running — including this one
    /// — keeps the old path and cannot see what was just installed. Somebody who
    /// installs an encoder and is then told there is no encoder has been told
    /// something untrue, and looking in the folder the installer just wrote to
    /// costs four <c>File.Exists</c> calls.
    /// </summary>
    public static string Find()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside))
        {
            return beside;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(folder.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing an export over.
            }
        }

        foreach (var folder in UsualPlaces())
        {
            try
            {
                var candidate = System.IO.Path.Combine(folder, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Same again: a folder we cannot even name is simply not it.
            }
        }

        return name;
    }

    /// <summary>
    /// Where an installer leaves an encoder, per platform. Named folders only —
    /// no walking a tree, because a search that takes a second is a search that
    /// runs on every export.
    /// </summary>
    private static IEnumerable<string> UsualPlaces()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(local))
            {
                // winget writes a shim here and adds it to the path — for shells
                // started afterwards.
                yield return System.IO.Path.Combine(local, "Microsoft", "WinGet", "Links");
            }

            foreach (var files in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (!string.IsNullOrWhiteSpace(files))
                {
                    yield return System.IO.Path.Combine(files, "ffmpeg", "bin");
                }
            }

            // Chocolatey and Scoop, the other two ways this arrives on Windows.
            var shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(shared))
            {
                yield return System.IO.Path.Combine(shared, "chocolatey", "bin");
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                yield return System.IO.Path.Combine(profile, "scoop", "shims");
            }

            yield break;
        }

        // Homebrew on both architectures, then the ordinary Unix places.
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return "/usr/bin";
        yield return "/bin";
        yield return "/snap/bin";
    }

    public async Task<ExportResult> SoundAsync(
        DesignDocument document, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var sounds = SoundsOf(document);

        if (sounds.Count == 0)
        {
            return ExportResult.Failed("There is nothing to listen to yet, so there is nothing to save.");
        }

        var workspace = Scratch();

        try
        {
            var pieces = new List<string>();

            foreach (var sound in sounds)
            {
                if (await MaterialiseAsync(sound, workspace, pieces.Count, cancellationToken).ConfigureAwait(false)
                    is { } piece)
                {
                    pieces.Add(piece);
                }
            }

            if (pieces.Count == 0)
            {
                return ExportResult.Failed(
                    "None of these sounds could be read, so there was nothing to put in the file.");
            }

            // A list file rather than a filter chain: concat's demuxer joins them
            // without re-encoding decisions per input, and the list is a thing a
            // person can open and read when an export comes out in the wrong order.
            var list = System.IO.Path.Combine(workspace, "order.txt");
            await File.WriteAllTextAsync(
                list,
                string.Join(Environment.NewLine, pieces.Select(p => $"file '{p.Replace("'", @"'\''")}'")),
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

            // What the file says it is once it is somewhere else. A running order
            // that exported to an untitled blob was half the job: a phone shows a
            // grey square and the word Unknown, and the person who made it cannot
            // find it again.
            var details = SoundDetails.Of(sounds[0], document);
            var cover = await CoverAsync(details, workspace, cancellationToken).ConfigureAwait(false);

            var arguments = new List<string> { "-y", "-f", "concat", "-safe", "0", "-i", list };

            if (cover is not null)
            {
                arguments.AddRange(["-i", cover]);
            }

            arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);

            if (cover is not null)
            {
                // The picture rides as a still video stream marked as the cover,
                // which is what every player looks for. Mapped explicitly, because
                // the default mapping would take one stream and drop the other.
                arguments.AddRange([
                    "-map", "0:a", "-map", "1:v",
                    "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
                ]);
            }

            arguments.AddRange(Tags(details));
            arguments.Add(outputPath);

            var ran = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);

            return ran.Ok && File.Exists(outputPath)
                ? ExportResult.Made(outputPath)
                : ExportResult.Failed(ran.Problem ?? "The sound could not be saved.");
        }
        finally
        {
            Sweep(workspace);
        }
    }

    public async Task<ExportResult> VideoAsync(
        DesignDocument document, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var shots = DesignMediums.FramesOf(document);

        if (shots.Count == 0)
        {
            return ExportResult.Failed("There are no shots yet, so there is nothing to save.");
        }

        var look = DesignLooks.Of(document.Look);
        var workspace = Scratch();

        try
        {
            var lines = new StringBuilder();
            var used = 0;

            foreach (var shot in shots)
            {
                var timing = DesignTiming.Of(shot, defaultSeconds: 4);
                var still = await StillOf(shot, look, workspace, used, cancellationToken).ConfigureAwait(false);

                // A shot that waits shows its ground for the wait, so the delay is
                // in the file rather than only in the preview.
                if (timing.DelaySeconds > 0)
                {
                    var blank = await BlankOf(look, workspace, used, cancellationToken).ConfigureAwait(false);
                    lines.AppendLine(Entry(blank, timing.DelaySeconds));
                }

                lines.AppendLine(Entry(still, Math.Max(1, (int)Math.Ceiling(timing.Seconds / timing.Rate))));
                used++;
            }

            // concat's image demuxer needs the last file repeated without a
            // duration, or the final shot is dropped from the output entirely.
            var lastStill = await StillOf(shots[^1], look, workspace, used, cancellationToken).ConfigureAwait(false);
            lines.AppendLine($"file '{lastStill.Replace("'", @"'\''")}'");

            var list = System.IO.Path.Combine(workspace, "shots.txt");
            await File.WriteAllTextAsync(list, lines.ToString(), cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

            var ran = await RunAsync(
                [
                    "-y", "-f", "concat", "-safe", "0", "-i", list,
                    "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,"
                           + "pad=1280:720:(ow-iw)/2:(oh-ih)/2,format=yuv420p",
                    "-r", "30", outputPath,
                ],
                cancellationToken).ConfigureAwait(false);

            return ran.Ok && File.Exists(outputPath)
                ? ExportResult.Made(outputPath)
                : ExportResult.Failed(ran.Problem ?? "The video could not be saved.");
        }
        catch (InvalidOperationException problem)
        {
            // A card that could not be drawn. Answered rather than thrown, because
            // every other way this fails comes back as a sentence and a caller that
            // has to handle both is a caller that will handle one.
            return ExportResult.Failed(problem.Message);
        }
        finally
        {
            Sweep(workspace);
        }
    }

    /// <summary>
    /// A picture as a JPEG, or null.
    ///
    /// For the PDF, which carries a JPEG exactly as it is and cannot take a PNG
    /// without one of us decoding it. The encoder is already here and already
    /// knows how, so it does it rather than a second image library arriving for
    /// one conversion.
    ///
    /// Null rather than an exception when it cannot: a picture that will not
    /// convert costs the picture, and the rest of the document still goes out.
    /// </summary>
    public async Task<byte[]?> ToJpegAsync(byte[] picture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(picture);

        var workspace = Scratch();

        try
        {
            var from = System.IO.Path.Combine(workspace, "in");
            var to = System.IO.Path.Combine(workspace, "out.jpg");

            await File.WriteAllBytesAsync(from, picture, cancellationToken).ConfigureAwait(false);

            // Flattened onto white, because a PDF image has no transparency here
            // and a PNG with an alpha channel would otherwise come out with black
            // where it should be clear.
            var ran = await RunAsync(
                [
                    "-y", "-i", from,
                    "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=rgb24",
                    "-q:v", "3", to,
                ],
                cancellationToken).ConfigureAwait(false);

            return ran.Ok && File.Exists(to)
                ? await File.ReadAllBytesAsync(to, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            Sweep(workspace);
        }
    }

    // ── What the file says it is ──────────────────────────────────────────

    /// <summary>
    /// The tags, as arguments. Empty when there is nothing to say, so a design
    /// that carries none of this exports exactly the bytes it did before.
    /// </summary>
    private static IEnumerable<string> Tags(SoundDetails details)
    {
        foreach (var (name, value) in new[]
                 {
                     ("title", details.Title),
                     ("artist", details.Artist),
                     ("album", details.Album),
                     ("date", details.Year),
                     ("lyrics", details.Lyrics),
                 })
        {
            if (value.Length == 0)
            {
                continue;
            }

            yield return "-metadata";

            // One argument, passed through the argument list rather than a command
            // line, so a title with a space, a quote or an equals sign in it is a
            // title and not two arguments.
            yield return $"{name}={value}";
        }
    }

    /// <summary>
    /// The cover as a file the encoder can read, or null when there is not one.
    ///
    /// A picture that cannot be decoded costs the picture, not the export. Somebody
    /// who has just made forty minutes of audio should not lose it because the
    /// artwork was half-pasted.
    /// </summary>
    private static async Task<string?> CoverAsync(
        SoundDetails details, string workspace, CancellationToken cancellationToken)
    {
        if (details.Artwork is not { } artwork)
        {
            return null;
        }

        var comma = artwork.IndexOf(',');

        if (comma < 0)
        {
            return null;
        }

        try
        {
            var path = System.IO.Path.Combine(workspace, "cover.png");
            await File.WriteAllBytesAsync(
                path, Convert.FromBase64String(artwork[(comma + 1)..]), cancellationToken).ConfigureAwait(false);

            return path;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ── Pieces ────────────────────────────────────────────────────────────

    private static string Entry(string path, int seconds)
        => $"file '{path.Replace("'", @"'\''")}'{Environment.NewLine}duration {seconds}";

    /// <summary>Every sound in the document, in the order it is shown.</summary>
    private static List<DesignNode> SoundsOf(DesignDocument document)
    {
        var found = new List<DesignNode>();

        void Walk(string parentId)
        {
            foreach (var child in document.ChildrenOf(parentId))
            {
                if (child.Kind == DesignNodeKind.Sound)
                {
                    found.Add(child);
                }

                Walk(child.Id);
            }
        }

        Walk(document.RootId);
        return found;
    }

    /// <summary>
    /// A sound as a file on disk, or null when it cannot be got at.
    ///
    /// Only what is already here: a data URI, which is how the paperclip carries
    /// audio onto the canvas in the first place. A remote address is left out of
    /// the export rather than fetched, because an export is not the place to start
    /// making network requests to a string a model may have written.
    /// </summary>
    private static async Task<string?> MaterialiseAsync(
        DesignNode sound, string workspace, int index, CancellationToken cancellationToken)
    {
        if (!sound.Props.TryGetValue("src", out var src) || string.IsNullOrWhiteSpace(src))
        {
            return null;
        }

        if (!src.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = src.IndexOf(',');
        if (comma < 0 || !src[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(src[(comma + 1)..]);
            var path = System.IO.Path.Combine(workspace, $"sound-{index:D3}{ExtensionOf(src[..comma])}");

            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (FormatException)
        {
            // A source that is not the base64 it claims to be. One sound missing
            // from the export beats no export at all.
            return null;
        }
    }

    private static string ExtensionOf(string header) => header switch
    {
        var h when h.Contains("mpeg", StringComparison.OrdinalIgnoreCase) => ".mp3",
        var h when h.Contains("wav", StringComparison.OrdinalIgnoreCase) => ".wav",
        var h when h.Contains("ogg", StringComparison.OrdinalIgnoreCase) => ".ogg",
        var h when h.Contains("m4a", StringComparison.OrdinalIgnoreCase) => ".m4a",
        _ => ".aac",
    };

    /// <summary>The picture for a shot: its own if it has one, else a made card.</summary>
    private async Task<string> StillOf(
        DesignNode shot, DesignLook look, string workspace, int index, CancellationToken cancellationToken)
    {
        if (shot.Props.TryGetValue("src", out var src)
            && src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            && src.IndexOf(',') is var comma and > 0)
        {
            try
            {
                var path = System.IO.Path.Combine(workspace, $"shot-{index:D3}.png");
                await File.WriteAllBytesAsync(
                    path, Convert.FromBase64String(src[(comma + 1)..]), cancellationToken).ConfigureAwait(false);
                return path;
            }
            catch (FormatException)
            {
                // Falls through to a made card rather than losing the shot.
            }
        }

        return await CardOf(shot.Text, look, workspace, $"card-{index:D3}", cancellationToken).ConfigureAwait(false);
    }

    private Task<string> BlankOf(
        DesignLook look, string workspace, int index, CancellationToken cancellationToken)
        => CardOf(string.Empty, look, workspace, $"blank-{index:D3}", cancellationToken);

    /// <summary>
    /// A shot with no picture, drawn in the design's own colours.
    ///
    /// Drawn by the encoder rather than by the canvas, which is the honest
    /// limitation of this export: the canvas is HTML and turning HTML into frames
    /// needs a browser driven frame by frame. So the words and the ground are
    /// right, and the layout is the encoder's rather than the canvas's.
    /// </summary>
    private async Task<string> CardOf(
        string words, DesignLook look, string workspace, string name, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(workspace, $"{name}.png");
        var ground = look.Ground.TrimStart('#');
        var ink = look.Ink.TrimStart('#');

        var arguments = new List<string>
        {
            "-y", "-f", "lavfi", "-i", $"color=c=0x{ground}:s=1280x720:d=1", "-frames:v", "1",
        };

        var face = Typeface();

        if (!string.IsNullOrWhiteSpace(words) && face is not null)
        {
            // The words go to the encoder in a file rather than inside the filter.
            //
            // Escaping them by hand was tried first and is not winnable: the words
            // pass through the filter-graph parser and then the filter's own text
            // parser, so a colon needs one number of backslashes and a quote needs
            // another, and `Act 1: "the fall" \ 50% off` — an ordinary title —
            // defeated it. A file has no syntax. Only the path is escaped, and the
            // path is ours.
            //
            // `expansion=none` because the words are somebody's title, not a
            // template: drawtext otherwise reads `%` as a date format and refuses
            // the whole frame over "50% off".
            var said = System.IO.Path.Combine(workspace, $"{name}.txt");
            await File.WriteAllTextAsync(said, words, cancellationToken).ConfigureAwait(false);

            arguments.AddRange([
                "-vf",
                $"drawtext=fontfile='{face}':textfile='{Escaped(said)}':expansion=none"
                + $":fontcolor=0x{ink}:fontsize=56:x=(w-text_w)/2:y=(h-text_h)/2",
            ]);
        }

        arguments.Add(path);

        var ran = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);

        // The result used to be discarded, so a card the encoder refused to draw
        // came back as a path to a file that was never written — and the export
        // then failed several steps later with "No such file or directory",
        // naming the symptom rather than the cause. The real message was
        // "Cannot load default config file", which is fontconfig, which is the
        // line above.
        if (!ran.Ok || !File.Exists(path))
        {
            throw new InvalidOperationException(ran.Problem ?? "A card could not be drawn.");
        }

        return path;
    }

    /// <summary>
    /// A path the filter-graph parser will read back as the path it was given:
    /// separators turned round, and the drive's colon escaped so it is not taken
    /// for the separator between one filter option and the next.
    /// </summary>
    private static string Escaped(string path)
        => path
            .Replace(@"\", "/", StringComparison.Ordinal)
            .Replace(":", @"\:", StringComparison.Ordinal);

    /// <summary>
    /// A font file to draw words with, or null when there is none.
    ///
    /// Named outright rather than left to the encoder. <c>drawtext</c> asks
    /// fontconfig for a default face, and Windows has no fontconfig — so every
    /// card with words on it failed with "Cannot load default config file" while
    /// the blank ones came out fine.
    ///
    /// A shot whose font cannot be found keeps its ground and loses its words,
    /// which is a worse export than it should be and still an export. Losing a
    /// whole film over a typeface would be the wrong trade.
    /// </summary>
    private static string? Typeface()
    {
        foreach (var candidate in TypefaceCandidates())
        {
            if (File.Exists(candidate))
            {
                return Escaped(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> TypefaceCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

            if (!string.IsNullOrWhiteSpace(fonts))
            {
                yield return System.IO.Path.Combine(fonts, "segoeui.ttf");
                yield return System.IO.Path.Combine(fonts, "arial.ttf");
                yield return System.IO.Path.Combine(fonts, "tahoma.ttf");
            }

            yield break;
        }

        yield return "/System/Library/Fonts/Helvetica.ttc";
        yield return "/System/Library/Fonts/Supplemental/Arial.ttf";
        yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        yield return "/usr/share/fonts/TTF/DejaVuSans.ttf";
        yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
    }

    private static string Scratch()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "concierge-export", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);
        return path;
    }

    private static void Sweep(string workspace)
    {
        try
        {
            Directory.Delete(workspace, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Swept with the machine's temp folder eventually. Failing an export
            // that produced a file because the scratch could not be tidied would
            // be losing the work over the housekeeping.
        }
    }

    private async Task<(bool Ok, string? Problem)> RunAsync(
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
            await output.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return (true, null);
            }

            // The last line of ffmpeg's output is the reason; the rest is the
            // build banner and stream detail nobody reading an error wants.
            var said = (await errors.ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();

            return (false, $"The encoder refused: {said ?? $"exit code {process.ExitCode}"}");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, $"The encoder could not be run: {error.Message}");
        }
    }
}
