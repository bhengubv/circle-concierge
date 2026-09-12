using System.Diagnostics;

namespace Concierge.Shared.Design;

/// <summary>What running the encoder said, and whether it worked.</summary>
/// <param name="Ok">Whether it finished cleanly.</param>
/// <param name="Said">
/// Everything it printed. ffmpeg writes its report to standard error before it does
/// anything, so this is both the error when something went wrong and the description
/// of a file when nothing did — which is why one field carries both.
/// </param>
/// <param name="Started">
/// Whether it ran at all. A distinct thing from failing: "the encoder refused" and
/// "there is no encoder" send somebody to two different places, and collapsing them
/// is how a missing install comes to look like a corrupt file.
/// </param>
public sealed record EncoderRun(bool Ok, string Said, bool Started = true);

/// <summary>
/// Running the encoder, wherever it happens to live on this head.
///
/// **Every head but one runs a program.** ffmpeg is an executable, Concierge starts
/// it with an argument list and reads what it printed, and that is the whole of it
/// on Windows, macOS and Linux.
///
/// Android cannot. An APK carries no executables beside the app, and Android refuses
/// to run a binary out of the app's own data directory, so the encoder arrives there
/// as a library called through JNI instead. The arguments are identical — it is the
/// same ffmpeg, told the same thing — and only the manner of asking differs. So this
/// is the seam, and it is deliberately the smallest one that covers both: hand it a
/// list of arguments, get back what the encoder said.
/// </summary>
public interface IEncoderRunner
{
    /// <summary>
    /// Whether there is an encoder to run at all.
    ///
    /// Asked before anything is registered rather than discovered on first use: a
    /// machine with no encoder is not offered `design_save`, `media_facts` or the
    /// music tools, because a tool that is offered and always fails is worse than
    /// one that is honestly absent. That is the rule the device capabilities follow.
    /// </summary>
    bool Here { get; }

    /// <summary>
    /// What it is, in words, for the room whose job is saying what this device can
    /// do — a path on a desktop, a named library on a phone.
    /// </summary>
    string What { get; }

    Task<EncoderRun> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// Which encoder this process uses.
///
/// **Static, because the thing it replaces already was.** `MediaLook.Possible` has
/// been a static file check since the day it was written, and six call sites across
/// three files ask it before any container exists — `AddConciergeTools` decides
/// whether to register `design_save` at all. Threading an injected encoder through
/// that would mean answering the question before the answer can be resolved.
///
/// So this is the ambient assumption that was already there, written down and made
/// replaceable, rather than a new one. A head that runs a program changes nothing.
/// A head that cannot sets <see cref="Runner"/> once, before it registers anything.
/// </summary>
public static class Encoders
{
    private static IEncoderRunner _runner = new ProcessEncoder();

    /// <summary>
    /// The encoder this process will use. Set it before registering services — the
    /// tool catalogue is decided at registration and is not revisited.
    /// </summary>
    public static IEncoderRunner Runner
    {
        get => _runner;
        set => _runner = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Whether anything on this head can encode.</summary>
    public static bool Here => _runner.Here;
}

/// <summary>
/// The ordinary one: start ffmpeg and read what it printed.
///
/// True on every head that has a filesystem somebody can put a program on, which is
/// all of them except Android and iOS.
/// </summary>
public sealed class ProcessEncoder : IEncoderRunner
{
    private readonly string _ffmpeg;

    public ProcessEncoder(string? path = null)
        => _ffmpeg = string.IsNullOrWhiteSpace(path) ? FfmpegMediaExport.Find() : path;

    /// <inheritdoc />
    public bool Here => File.Exists(_ffmpeg);

    /// <inheritdoc />
    public string What => _ffmpeg;

    /// <inheritdoc />
    public async Task<EncoderRun> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
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
                return new EncoderRun(false, "The encoder would not start.", Started: false);
            }

            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return new EncoderRun(
                process.ExitCode == 0,
                await errors.ConfigureAwait(false) + await output.ConfigureAwait(false));
        }
        catch (Exception error)
            when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new EncoderRun(false, error.Message, Started: false);
        }
    }
}
