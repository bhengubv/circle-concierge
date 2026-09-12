using Concierge.Shared.Design;
using Ffmpegkit.Droid;

namespace Concierge.Platforms.Android;

/// <summary>
/// The encoder on a phone, where there is no program to start.
///
/// **This is what the whole handheld gap came down to.** Measured on the running
/// Android head against the running desktop head: thirty-five tools against
/// forty-three, and every single one of the eight absences was ffmpeg —
/// `media_facts`, `media_frames`, `media_is_silent`, `music_identify`,
/// `music_quality`, `music_duplicates`, `music_file`, `music_filing` — plus
/// `design_save` once a canvas is open. Not eight decisions. One.
///
/// The cause is a property of the platform rather than an oversight: an APK carries
/// no executables beside the app, and Android has refused to run a binary out of the
/// app's own data directory since API 29. So `File.Exists(Find())` was false, which
/// is the honest answer to the wrong question — the encoder is here, it is simply
/// not a file anybody can start.
///
/// It is the same ffmpeg, told the same arguments, in the same order. Only the
/// manner of asking differs, which is exactly the width of the seam.
/// </summary>
/// <remarks>
/// **Arguments are handed over pre-split and never joined into a command line.**
/// `ExecuteAsync` takes a string the way a person would type it, which means the
/// quoting rules are back — and this repository has already lost that fight once,
/// in the export's drawtext escaping, against `Act 1: "the fall" \ 50% off`. A file
/// path on Android contains no spaces most of the time and that is not a guarantee
/// anybody should build on.
/// </remarks>
public sealed class FFmpegKitEncoder : IEncoderRunner
{
    /// <summary>
    /// How long to wait for the last of the encoder's output to arrive, in milliseconds.
    ///
    /// ffmpeg-kit collects log lines on its own thread, so a session that has finished
    /// can still have a line or two in flight. Asking for the logs without waiting
    /// would truncate exactly the last line — which is the one the export quotes back
    /// as the reason a save failed, and the one `MediaLook` reads a duration out of.
    ///
    /// Five seconds is ffmpeg-kit's own default, and it is a ceiling rather than a
    /// delay: the call returns as soon as the output is complete.
    /// </summary>
    private const int LogsSettle = 5000;

    /// <summary>
    /// Always, which is the point: the library is inside the APK, so there is nothing
    /// to install, nothing to find, and no machine where this is present and broken.
    ///
    /// The desktop's answer has to be a file check because ffmpeg there is somebody
    /// else's install and may genuinely be missing. Here it shipped with the app.
    /// </summary>
    public bool Here => true;

    /// <inheritdoc />
    public string What => "ffmpeg-kit 8.1.7, inside the app";

    /// <inheritdoc />
    public async Task<EncoderRun> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await FFmpegKit
                .ExecuteWithArgumentsAsync([.. arguments], cancellationToken)
                .ConfigureAwait(false);

            if (session is null)
            {
                return new EncoderRun(false, "The encoder did not answer.", Started: false);
            }

            // Everything it printed, which is what both callers actually read: the
            // export wants the last line of a failure, and `MediaLook` reads the
            // report ffmpeg writes before it does anything. One string serves both,
            // the same as on a desktop where stderr and stdout are concatenated.
            var said = session.GetAllLogsAsString(LogsSettle) ?? string.Empty;

            return new EncoderRun(session.Succeeded(), said);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            // A JNI call can throw where a process cannot — the library missing for
            // this ABI, mostly. Reported as "did not start" rather than "refused",
            // because those two send somebody to entirely different places and the
            // export writes a different sentence for each.
            return new EncoderRun(false, failure.Message, Started: false);
        }
    }
}
