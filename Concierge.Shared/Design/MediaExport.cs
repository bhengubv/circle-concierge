using System.Diagnostics;
using System.Text;

namespace Concierge.Shared.Design;

/// <summary>What came of an export.</summary>
/// <param name="Ok">Whether there is a file at the end of it.</param>
/// <param name="Path">Where it is.</param>
/// <param name="Problem">What went wrong, in words a person can act on.</param>
/// <param name="Left">
/// What did not make it into the file, when something did not.
///
/// **Tracks that could not be read were dropped in silence.** Six in a running order, three
/// readable, and the answer was "Saved to …\design.m4a" — a file containing half the work,
/// reported as a success. Somebody publishes that and finds out from whoever downloads it.
///
/// Null when everything went in.
/// </param>
public sealed record ExportResult(bool Ok, string? Path, string? Problem, string? Left = null)
{
    public static ExportResult Made(string path, string? left = null) => new(true, path, null, left);

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
    private readonly IEncoderRunner _encoder;

    /// <summary>
    /// Which codec this encoder turned out to have, once it has been asked. Held for
    /// the life of the export rather than re-derived per shot.
    /// </summary>
    private IReadOnlyList<string>? _videoCodec;

    /// <param name="ffmpegPath">
    /// Where the encoder is. Resolved by <see cref="Find"/> when not given.
    /// </param>
    public FfmpegMediaExport(string? ffmpegPath = null)
    {
        _encoder = string.IsNullOrWhiteSpace(ffmpegPath)
            ? Encoders.Runner
            : new ProcessEncoder(ffmpegPath);
        _ffmpeg = _encoder.What;
    }

    /// <summary>Where the encoder is on this machine, or what it is where it has no path.</summary>
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

            // Counted, not just collected. A track that cannot be read is a track missing
            // from the file, and the person holding that file has to be told — see the note
            // on ExportResult.Left.
            var missed = 0;

            foreach (var sound in sounds)
            {
                var piece = await MaterialiseAsync(sound, workspace, pieces.Count, cancellationToken)
                    .ConfigureAwait(false);

                // Written out is not the same as readable, and that distinction is the whole
                // defect. A track carried as a data URI always materialises — the bytes are
                // right there — and can still be something no decoder will take. The concat
                // step then drops it without a word, so six tracks became a file holding
                // three and the answer was "Saved to …" with nothing else.
                if (piece is not null && await DecodesAsync(piece, cancellationToken).ConfigureAwait(false))
                {
                    pieces.Add(piece);
                }
                else
                {
                    missed++;
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
                ? ExportResult.Made(
                    outputPath,
                    missed == 0
                        ? null
                        : $"{missed} of {sounds.Count} could not be read and are not in it.")
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
            // Every shot becomes one finished piece at the same size, frame rate
            // and codecs, and the pieces are then joined without re-encoding.
            //
            // The older version built a list of stills and let the concat demuxer
            // hold each for a duration, which cannot work once a shot is real
            // footage: a clip has its own length, its own frame rate and its own
            // audio. Normalising first is the standard answer and the only one
            // that lets a drawn card and a filmed clip sit next to each other.
            var pieces = new List<Piece>();

            for (var at = 0; at < shots.Count; at++)
            {
                var shot = shots[at];
                var timing = DesignTiming.Of(shot, defaultSeconds: 4);

                // A shot that waits shows its ground for the wait, so the delay is
                // in the file rather than only in the preview.
                if (timing.DelaySeconds > 0)
                {
                    var blank = await BlankOf(look, workspace, at, cancellationToken).ConfigureAwait(false);

                    pieces.Add(new Piece(
                        await PieceFromCardAsync(
                            blank, timing.DelaySeconds, Shape, workspace, $"wait{at:D3}", cancellationToken)
                            .ConfigureAwait(false),
                        timing.DelaySeconds,
                        0));
                }

                var held = Math.Max(0.1, timing.Seconds / timing.Rate);

                if (FootageIn(shot) is { } footage)
                {
                    pieces.Add(new Piece(
                        await PieceFromFootageAsync(
                            footage, timing, held, PictureFilter(shot, timing.Rate, held, still: false),
                            workspace, $"shot{at:D3}", cancellationToken)
                            .ConfigureAwait(false),
                        held,
                        pieces.Count == 0 ? 0 : BlendOf(shot)));

                    continue;
                }

                var card = await StillOf(shot, look, workspace, at, cancellationToken).ConfigureAwait(false);

                pieces.Add(new Piece(
                    await PieceFromCardAsync(
                        card, held, PictureFilter(shot, timing.Rate, held, still: true), workspace, $"shot{at:D3}",
                        cancellationToken).ConfigureAwait(false),
                    held,
                    pieces.Count == 0 ? 0 : BlendOf(shot)));
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

            var ran = await JoinAsync(pieces, outputPath, workspace, cancellationToken)
                .ConfigureAwait(false);

            return ran.Ok && File.Exists(outputPath)
                ? ExportResult.Made(outputPath)
                : ExportResult.Failed(ran.Problem ?? "The video could not be saved.");
        }
        catch (InvalidOperationException problem)
        {
            // A card that could not be drawn, or footage that could not be cut.
            // Answered rather than thrown, because every other way this fails comes
            // back as a sentence and a caller that has to handle both is a caller
            // that will handle one.
            return ExportResult.Failed(problem.Message);
        }
        finally
        {
            Sweep(workspace);
        }
    }

    /// <summary>
    /// How every piece is shaped, so they can be joined without re-encoding.
    /// </summary>
    private const string Shape =
        "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,format=yuv420p";

    /// <summary>
    /// Puts the finished pieces together.
    ///
    /// **Two ways, and which one is used depends on whether anything fades.** With
    /// straight cuts every piece already matches, so joining is a copy: no
    /// re-encoding, no quality lost, and it takes as long as writing the file. The
    /// moment one shot fades into another that stops being possible — a dissolve
    /// has to be computed from both pictures at once — so the whole film goes
    /// through a filter chain instead.
    ///
    /// The fast path is kept rather than always using the slow one because most
    /// films are all cuts, and re-encoding a finished film to achieve nothing is
    /// the sort of cost nobody sees and everybody pays.
    /// </summary>
    private async Task<(bool Ok, string? Problem)> JoinAsync(
        IReadOnlyList<Piece> pieces, string outputPath, string workspace, CancellationToken cancellationToken)
    {
        if (pieces.Count == 0)
        {
            return (false, "There was nothing to join.");
        }

        if (pieces.Count == 1 || pieces.All(piece => piece.Blend <= 0))
        {
            var list = System.IO.Path.Combine(workspace, "shots.txt");

            await File.WriteAllTextAsync(
                list,
                string.Join(Environment.NewLine, pieces.Select(piece => Quoted(piece.Path))),
                cancellationToken).ConfigureAwait(false);

            return await RunAsync(
                ["-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", outputPath],
                cancellationToken).ConfigureAwait(false);
        }

        var arguments = new List<string> { "-y" };

        foreach (var piece in pieces)
        {
            arguments.AddRange(["-i", piece.Path]);
        }

        var chain = new StringBuilder();
        var video = "0:v";
        var audio = "0:a";

        // Where each fade begins: everything played so far, less every fade so
        // far, because a fade overlaps the two shots rather than sitting between
        // them. Get this wrong and the film drifts a little further out of step at
        // every join.
        var played = pieces[0].Seconds;
        var overlapped = 0.0;

        for (var at = 1; at < pieces.Count; at++)
        {
            var blend = pieces[at].Blend > 0 ? pieces[at].Blend : 0.001;
            overlapped += blend;

            var offset = Math.Max(0, played - overlapped);

            chain.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"[{video}][{at}:v]xfade=transition=fade:duration={Number(blend)}:offset={Number(offset)}[v{at}];");

            chain.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"[{audio}][{at}:a]acrossfade=d={Number(blend)}[a{at}];");

            video = $"v{at}";
            audio = $"a{at}";
            played += pieces[at].Seconds;
        }

        arguments.AddRange([
            "-filter_complex", chain.ToString().TrimEnd(';'),
            "-map", $"[{video}]", "-map", $"[{audio}]",
            .. await VideoCodecAsync(cancellationToken).ConfigureAwait(false),
            "-c:a", "aac", "-ar", "44100", "-ac", "2",
            outputPath,
        ]);

        return await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One finished shot: where it is, how long it runs, and how long the shot
    /// before it takes to fade into it.
    /// </summary>
    private sealed record Piece(string Path, double Seconds, double Blend);

    /// <summary>
    /// How long the shot before this one takes to fade into it, or nought for a cut.
    ///
    /// A cut is the default because a cut is what film is made of, and a dissolve on
    /// every join is what a first attempt looks like.
    /// </summary>
    internal static double BlendOf(DesignNode shot)
        => shot.Props.TryGetValue("blend", out var said)
            && double.TryParse(said, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
                ? Math.Clamp(seconds, 0.1, 1.5)
                : 0;

    /// <summary>
    /// How a shot is graded, as a filter, or nothing.
    ///
    /// **Plain words, not a filter graph.** Diffusion Studio exposes colour
    /// correction the way an editor does; this exposes the six or seven things
    /// somebody actually asks for — warmer, cooler, brighter, darker, black and
    /// white, faded, vivid — and maps each to what the encoder needs. A person who
    /// wants `eq=saturation=1.4:contrast=1.1` is not the person this is for, and a
    /// person who wants "make it warmer" should not have to become them.
    ///
    /// Unknown words grade nothing rather than failing. A model inventing "cinematic"
    /// should cost a shot its grade, not the whole film.
    /// </summary>
    internal static string GradeOf(DesignNode shot)
    {
        if (!shot.Props.TryGetValue("colour", out var said) || string.IsNullOrWhiteSpace(said))
        {
            return string.Empty;
        }

        return said.Trim().ToLowerInvariant() switch
        {
            "warm" or "warmer" => "colorbalance=rs=0.12:gs=0.04:bs=-0.12",
            "cool" or "cooler" or "cold" => "colorbalance=rs=-0.12:gs=-0.02:bs=0.12",
            "bright" or "brighter" => "eq=brightness=0.08:contrast=1.05",
            "dark" or "darker" or "moody" => "eq=brightness=-0.09:contrast=1.1",
            "grey" or "gray" or "mono" or "black and white" => "hue=s=0",
            "faded" or "soft" or "washed" => "eq=saturation=0.6:contrast=0.92",
            "vivid" or "punchy" or "bold" => "eq=saturation=1.45:contrast=1.12",
            _ => string.Empty,
        };
    }

    /// <summary>The words a grade can be asked for by, for the tool and the tests.</summary>
    internal static IReadOnlyList<string> Grades =>
        ["warm", "cool", "bright", "dark", "grey", "faded", "vivid"];

    /// <summary>
    /// How a shot moves while it is on screen, as a filter, or nothing.
    ///
    /// **A still picture held for four seconds looks like a fault.** That is the whole of
    /// what a motion-graphics engine is wanted for here, and it does not need one: a slow
    /// push in or a slow drift across turns a card or a photograph into a shot, and both are
    /// ordinary filters the encoder already has.
    ///
    /// Three words, not a keyframe editor — still, fade, grow, drift. Somebody who wants to
    /// author a curve is not who this is for, and somebody who wants the picture to stop
    /// looking dead should not have to become them.
    ///
    /// **Grow and drift only apply to a still.** On real footage they fight the picture that
    /// is already moving, and the encoder does it by resampling frame by frame, so it costs
    /// time and looks worse. A fade works on either. A word that cannot be honoured here
    /// costs the movement, not the film.
    /// </summary>
    /// <param name="shot">The shot.</param>
    /// <param name="seconds">How long it is held, which is what the movement is spread over.</param>
    /// <param name="still">Whether this is a drawn card or a photograph rather than footage.</param>
    internal static string MoveOf(DesignNode shot, double seconds, bool still)
    {
        if (!shot.Props.TryGetValue("move", out var said) || string.IsNullOrWhiteSpace(said))
        {
            return string.Empty;
        }

        // Frames rather than seconds, because that is what zoompan counts in — and at the
        // thirty the rest of the export is normalised to.
        var frames = Math.Max(1, (int)Math.Round(Math.Max(seconds, 0.1) * 30));

        return said.Trim().ToLowerInvariant() switch
        {
            "fade" or "fade in" or "in" => "fade=t=in:st=0:d=0.5",

            "grow" or "push" or "zoom" or "closer" when still =>
                $"zoompan=z='min(zoom+0.0006,1.10)':d={frames}"
                + ":x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s=1280x720:fps=30",

            "drift" or "pan" or "across" when still =>
                $"zoompan=z=1.10:d={frames}"
                + $":x='(iw-iw/zoom)*(on/{frames})':y='ih/2-(ih/zoom/2)':s=1280x720:fps=30",

            _ => string.Empty,
        };
    }

    /// <summary>The words a movement can be asked for by, for the tool and the tests.</summary>
    internal static IReadOnlyList<string> Moves => ["still", "fade", "grow", "drift"];

    /// <summary>
    /// Everything done to one shot's picture, in the order it has to happen.
    ///
    /// Grading goes before the shaping rather than after. Padding a clip to 16:9
    /// adds bars, and a grade applied afterwards grades the bars too — a "warmer"
    /// shot would come out with warm grey edges, which is the kind of thing nobody
    /// sees until it is in front of an audience.
    /// </summary>
    private static string PictureFilter(DesignNode shot, double rate, double seconds = 0, bool still = false)
    {
        var parts = new List<string>();

        if (rate != 1)
        {
            parts.Add($"setpts={Number(1 / rate)}*PTS");
        }

        if (GradeOf(shot) is { Length: > 0 } grade)
        {
            parts.Add(grade);
        }

        parts.Add(Shape);

        // Movement last, on the shaped picture. A push in applied before the shaping would
        // be undone by the scale that follows it, and a fade applied first would fade the
        // picture and then have bars painted over the result.
        if (MoveOf(shot, seconds, still) is { Length: > 0 } moving)
        {
            parts.Add(moving);
        }

        return string.Join(',', parts);
    }

    /// <summary>
    /// The footage a shot points at, or null.
    ///
    /// A path on disk rather than a data URI, and that is a deliberate difference
    /// from how a picture or a track is carried. A four-minute clip is hundreds of
    /// megabytes; as base64 inside `design.json` it would be rewritten every time
    /// anybody edited a heading.
    ///
    /// **So a design holding footage is not portable the way the others are** — it
    /// points at files on this machine. Saying that here is better than somebody
    /// discovering it when they send the design to someone else.
    /// </summary>
    private static string? FootageIn(DesignNode shot)
    {
        if (!shot.Props.TryGetValue("src", out var src) || string.IsNullOrWhiteSpace(src))
        {
            return null;
        }

        // A data URI on a shot is a picture, which the still path already handles.
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return File.Exists(src) ? src : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A cut out of real footage: seek to the in point, keep the length asked for.
    ///
    /// The seek goes **before** the input rather than after it, which is what makes
    /// it jump straight to that point instead of decoding everything up to it. On a
    /// long clip that is the difference between instant and a minute.
    /// </summary>
    private async Task<string> PieceFromFootageAsync(
        string footage, DesignTiming timing, double held, string picture, string workspace, string name,
        CancellationToken cancellationToken)
    {
        var piece = System.IO.Path.Combine(workspace, $"{name}.mp4");

        var arguments = new List<string> { "-y" };

        if (timing.TrimSeconds > 0)
        {
            arguments.AddRange(["-ss", Number(timing.TrimSeconds)]);
        }

        // Both inputs first, then everything about the output. ffmpeg reads its
        // arguments in that order and nothing else — putting the silent track
        // after `-vf` produced "Error opening input files: Invalid argument",
        // which names the inputs and says nothing about the ordering that caused
        // it.
        arguments.AddRange([
            "-i", footage,

            // Every piece carries sound, even when the footage has none, because
            // the join refuses a run of pieces that disagree about whether audio
            // exists at all.
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",

            "-map", "0:v:0", "-map", "1:a:0",
            "-t", Number(held),
            "-vf", picture,
            "-r", "30",
            .. await VideoCodecAsync(cancellationToken).ConfigureAwait(false),
            "-c:a", "aac", "-ar", "44100", "-ac", "2",
            piece,
        ]);

        var ran = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);

        if (!ran.Ok || !File.Exists(piece))
        {
            throw new InvalidOperationException(
                ran.Problem ?? $"The footage at {System.IO.Path.GetFileName(footage)} could not be cut.");
        }

        return piece;
    }

    /// <summary>A drawn card held for a length, made to match the footage pieces.</summary>
    private async Task<string> PieceFromCardAsync(
        string card, double held, string picture, string workspace, string name,
        CancellationToken cancellationToken)
    {
        var piece = System.IO.Path.Combine(workspace, $"{name}.mp4");

        var ran = await RunAsync(
            [
                "-y",
                "-loop", "1", "-i", card,
                "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
                "-t", Number(held),
                "-vf", picture,
                "-r", "30",
                .. await VideoCodecAsync(cancellationToken).ConfigureAwait(false),
                "-c:a", "aac", "-ar", "44100", "-ac", "2",
                "-shortest",
                piece,
            ],
            cancellationToken).ConfigureAwait(false);

        if (!ran.Ok || !File.Exists(piece))
        {
            throw new InvalidOperationException(ran.Problem ?? "A shot could not be made.");
        }

        return piece;
    }

    /// <summary>
    /// One line of the join list. Its own helper rather than reusing the still
    /// version, because a finished piece carries its own length — saying a
    /// duration here would override the clip and cut it twice.
    /// </summary>
    private static string Quoted(string path)
        => $"file '{path.Replace("'", @"'''")}'";

    private static string Number(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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

        // `-update 1` says "this one path is the whole output", which is what writing
        // a single still means and what was missing.
        //
        // **Every card came out empty on the phone, and the desktop had been warning
        // about this the whole time.** Without it ffmpeg treats the output path as a
        // numbered sequence with no pattern in it — "Use a pattern such as %03d for an
        // image sequence or use the -update option" — and whether it then writes the
        // frame anyway is a matter of how forgiving that particular build is. The
        // desktop's is; ffmpeg-kit's Android build, configured --enable-small, is not:
        // "Finishing stream without any data written to it", frame=0, and an export
        // that failed three steps later complaining about a missing file.
        //
        // So this is not an Android fix. It is a command that was wrong everywhere and
        // only ever punished on the strictest build, which is the useful kind of thing
        // a second platform finds.
        var arguments = new List<string>
        {
            "-y", "-f", "lavfi", "-i", $"color=c=0x{ground}:s=1280x720:d=1",
            "-frames:v", "1", "-update", "1",
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
    /// <summary>
    /// What to make the picture with, asked of the encoder rather than assumed.
    ///
    /// **`libx264` was written in three places as though every ffmpeg has it, and
    /// the phone's does not.** x264 is GPL, so an LGPL build cannot carry it — and
    /// an LGPL build is exactly what ships inside the app on Android, because this
    /// is the one head where the encoder is redistributed rather than found on
    /// somebody's machine. The failure was not a missing codec message either: it
    /// was *"Unrecognized option 'preset'"*, because `-preset` is an x264 option,
    /// so the export died on a flag rather than on the thing the flag belonged to.
    ///
    /// Asked once and remembered, because `-encoders` lists several hundred lines
    /// and an export runs this per shot.
    ///
    /// The order is quality first. `libopenh264` is Cisco's, LGPL-compatible, and
    /// makes a genuine H.264 file that plays everywhere — it simply wants a bitrate
    /// where x264 takes a preset. `mpeg4` is the floor: every build has it, it is
    /// nobody's first choice, and a film in an older codec is better than no film.
    /// </summary>
    private async Task<IReadOnlyList<string>> VideoCodecAsync(CancellationToken cancellationToken)
    {
        if (_videoCodec is { } already)
        {
            return already;
        }

        var run = await _encoder
            .RunAsync(["-hide_banner", "-encoders"], cancellationToken)
            .ConfigureAwait(false);

        var said = run.Said;

        // Matched with a leading space so "libx264" does not match "libx264rgb" and
        // so a codec named inside somebody's build string is not read as a codec.
        IReadOnlyList<string> chosen =
            said.Contains(" libx264", StringComparison.Ordinal)
                ? ["-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p"]
                : said.Contains(" libopenh264", StringComparison.Ordinal)
                    ? ["-c:v", "libopenh264", "-b:v", "2M", "-pix_fmt", "yuv420p"]
                    : ["-c:v", "mpeg4", "-q:v", "3", "-pix_fmt", "yuv420p"];

        _videoCodec = chosen;

        return chosen;
    }

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

    internal static IEnumerable<string> TypefaceCandidates()
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
        }

        // Everything else follows whatever platform this is, unconditionally.
        //
        // It used to stop after the Windows names. Nothing was wrong with that —
        // `File.Exists` settles which one applies anyway — but it meant the list a
        // phone would use could not be read on the machine the tests run on, and a
        // list nobody can check is how the Android names came to be missing in the
        // first place. Eight File.Exists calls on a miss is not a cost worth an
        // untestable branch.

        // Android, and it has to be named like everything else here.
        //
        // A shot whose font cannot be found keeps its ground and loses its words, so
        // without this every card on the phone came out a plain coloured rectangle —
        // a video that exports and says nothing, which is worse than one that fails.
        // Android ships no fontconfig and no DejaVu; Roboto is on every device, and
        // the two names cover the split where it was repackaged as a variable font.
        yield return "/system/fonts/Roboto-Regular.ttf";
        yield return "/system/fonts/RobotoStatic-Regular.ttf";
        yield return "/system/fonts/DroidSans.ttf";

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

    /// <summary>
    /// Whether the encoder can actually read this file.
    ///
    /// Decoding it to nowhere is the cheapest honest test — a file that claims to be an mp3
    /// and holds two kilobytes of zeros passes every check made of its name, its extension and
    /// its size, and fails this one. It costs one encoder run per track, which against an
    /// export that re-encodes everything is nothing.
    /// </summary>
    private async Task<bool> DecodesAsync(string path, CancellationToken cancellationToken)
    {
        var (ok, _) = await RunAsync(
            ["-v", "error", "-i", path, "-f", "null", "-"], cancellationToken).ConfigureAwait(false);

        return ok;
    }

    /// <summary>
    /// Ask the encoder, however this head asks it.
    ///
    /// This was a `Process.Start` of its own, beside an identical one in `MediaLook`.
    /// Both go through the one seam now — Android has no executable to start, and two
    /// copies of that assumption would have needed fixing twice.
    /// </summary>
    private async Task<(bool Ok, string? Problem)> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var run = await _encoder.RunAsync(arguments, cancellationToken).ConfigureAwait(false);

        if (run.Ok)
        {
            return (true, null);
        }

        if (!run.Started)
        {
            return (false, $"The encoder could not be run: {run.Said}");
        }

        // The last line of ffmpeg's output is the reason; the rest is the
        // build banner and stream detail nobody reading an error wants.
        var said = run.Said
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return (false, $"The encoder refused: {said ?? "it said nothing"}");
    }
}
