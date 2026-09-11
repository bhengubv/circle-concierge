using System.Diagnostics;
using CircleAI.Voice;
using Concierge.Shared.Design;
using Concierge.Shared.Media;

// Both namespaces name a TranscriptionResult. This one is ours — the one the seam
// returns — and the package's is what whisper hands back inside.
using Written = Concierge.Shared.Media.TranscriptionResult;

namespace Concierge.Ai;

/// <summary>How the on-device voice runtime is set up.</summary>
public sealed class LocalVoiceOptions
{
    /// <summary>Where the models live — the same folder the chat runtime downloads into.</summary>
    public string ModelsDirectory { get; init; } = new CircleAiChatOptions().ModelsDirectory;

    /// <summary>How many threads whisper may use. Zero lets the package decide.</summary>
    public int Threads { get; init; }

    /// <summary>What the voice model was trained at. Piper's usual is 22,050.</summary>
    public int SampleRate { get; init; } = 22050;

    /// <summary>Where the encoder is, for turning a file into samples whisper can read.</summary>
    public string? FfmpegPath { get; init; }
}

/// <summary>
/// Speech, both ways, on the device.
///
/// CircleAI.Voice has been in this solution's package cache the whole time and nothing
/// referenced it: WhisperTranscriber writes out what is said, OnnxTtsEngine speaks, and
/// neither had a caller. This is that wiring, behind the IVoiceRuntime seam the cloud
/// runtime already sits behind — so whatever uses voice does not care which answered.
///
/// **Present when the model files are, absent otherwise**, which is the rule every device
/// capability follows. There is no key, no account and nothing to configure: either the
/// files are in the folder or they are not, and StatusMessage says which and names the
/// folder.
///
/// **An audio file is not samples.** Whisper wants 16kHz mono PCM and a person has an .m4a,
/// so the file goes through the encoder first — the same encoder the export and inspection
/// work already find and use. No encoder means no transcription, said rather than failed
/// obscurely, because a runtime that reports ready and then cannot read any real file is the
/// screen-asserting-something-untrue defect this repository keeps hitting.
/// </summary>
public sealed class CircleAiVoiceRuntime : IVoiceRuntime, IAsyncDisposable
{
    private readonly LocalVoiceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WhisperTranscriber? _listener;
    private OnnxTtsEngine? _speaker;
    private bool _disposed;

    public CircleAiVoiceRuntime(LocalVoiceOptions? options = null)
    {
        _options = options ?? new LocalVoiceOptions();
        Files = LocalVoiceFiles.Look(_options.ModelsDirectory);
    }

    /// <summary>What was found on disk, and where it was looked for.</summary>
    public LocalVoiceFiles Files { get; }

    public string Id => "circleai-voice";

    public string EngineLabel => "On this device · whisper + onnx";

    public bool IsReady => SupportsTranscription || SupportsSynthesis;

    public string StatusMessage => Files.Listener is not null && Encoder is null
        ? Files.Why + " Transcription also needs the encoder (ffmpeg), which is not on this "
          + "machine, so only speaking works."
        : Files.Why;

    /// <summary>
    /// Listening needs both halves: the model, and something that can turn a file into the
    /// samples it reads. One without the other is a capability that fails when used.
    /// </summary>
    public bool SupportsTranscription => Files.Listener is not null && Encoder is not null;

    public bool SupportsSynthesis => Files.Speaker is not null;

    /// <summary>
    /// Everything except somebody asking to stop. A cancelled call is not a broken model and
    /// must come back as a cancellation rather than as silence.
    /// </summary>
    private static bool NotCancelled(CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested;

    private string? Encoder
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(_options.FfmpegPath)
                ? FfmpegMediaExport.Find()
                : _options.FfmpegPath;

            return File.Exists(path) ? path : null;
        }
    }

    public async Task<Written> TranscribeAsync(
        Stream audio, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        if (!SupportsTranscription)
        {
            return new Written(Id, string.Empty, null, null);
        }

        var samples = await SamplesFromAsync(audio, fileName, cancellationToken).ConfigureAwait(false);

        if (samples.Length == 0)
        {
            return new Written(Id, string.Empty, null, null);
        }

        var listener = await ListenerAsync(cancellationToken).ConfigureAwait(false);

        if (listener is null)
        {
            return new Written(Id, string.Empty, null, null);
        }

        CircleAI.Voice.TranscriptionResult heard;

        try
        {
            heard = await listener.TranscribeAsync(samples, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (NotCancelled(cancellationToken))
        {
            // Both packages open their model on first use rather than in the constructor, so
            // a file that is not a model fails here rather than where it was opened. A test
            // with four bytes in a .onnx found this: the exception came straight back out
            // through the seam and would have taken a turn down with it.
            return new Written(Id, string.Empty, null, null);
        }

        // 16kHz, mono, two bytes a sample — so the length is the duration.
        var seconds = samples.Length / (double)(16_000 * 2);

        return new Written(
            Id,
            heard.Text?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(heard.LanguageCode) ? null : heard.LanguageCode,
            TimeSpan.FromSeconds(seconds));
    }

    public async Task<SpeechResult> SynthesizeAsync(
        string text, string voice = "alloy", CancellationToken cancellationToken = default)
    {
        if (!SupportsSynthesis || string.IsNullOrWhiteSpace(text))
        {
            return new SpeechResult(Id, "audio/wav", []);
        }

        var speaker = await SpeakerAsync(cancellationToken).ConfigureAwait(false);

        if (speaker is null)
        {
            return new SpeechResult(Id, "audio/wav", []);
        }

        TtsSynthesisResult said;

        try
        {
            said = await speaker.SynthesiseAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (NotCancelled(cancellationToken))
        {
            return new SpeechResult(Id, "audio/wav", []);
        }

        // A voice model has one voice. The argument is part of the seam because a cloud
        // provider offers several; ignoring it here is better than pretending to honour it.
        var wav = WavAudio.Wrap(
            said.AudioData.Span,
            said.SampleRate > 0 ? said.SampleRate : _options.SampleRate,
            said.Channels > 0 ? said.Channels : 1,
            said.BitsPerSample > 0 ? said.BitsPerSample : 16);

        return new SpeechResult(Id, "audio/wav", wav);
    }

    /// <summary>
    /// A file, as the samples whisper reads: 16kHz, one channel, sixteen bits.
    ///
    /// Through a temporary file rather than a pipe, because ffmpeg writing to standard
    /// output while we also read standard error is two pipes and a deadlock waiting for a
    /// long recording.
    /// </summary>
    private async Task<byte[]> SamplesFromAsync(
        Stream audio, string fileName, CancellationToken cancellationToken)
    {
        var encoder = Encoder;

        if (encoder is null)
        {
            return [];
        }

        var folder = Path.Combine(Path.GetTempPath(), "concierge-voice", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);

        var ending = Path.GetExtension(fileName);
        var source = Path.Combine(folder, "in" + (string.IsNullOrWhiteSpace(ending) ? ".bin" : ending));
        var samples = Path.Combine(folder, "out.pcm");

        try
        {
            await using (var writing = File.Create(source))
            {
                await audio.CopyToAsync(writing, cancellationToken).ConfigureAwait(false);
            }

            var run = new ProcessStartInfo(encoder)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y",
                         "-i", source,
                         "-ac", "1", "-ar", "16000", "-f", "s16le",
                         samples,
                     })
            {
                run.ArgumentList.Add(argument);
            }

            using var process = Process.Start(run);

            if (process is null)
            {
                return [];
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return File.Exists(samples)
                ? await File.ReadAllBytesAsync(samples, cancellationToken).ConfigureAwait(false)
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A file still held open costs a temporary folder, not the transcript.
            }
        }
    }

    private async Task<WhisperTranscriber?> ListenerAsync(CancellationToken cancellationToken)
    {
        if (Files.Listener is not { } path)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Opened once and kept: a whisper model takes seconds to open and every
            // sentence after the first would pay it again.
            return _listener ??= new WhisperTranscriber(path, _options.Threads);
        }
        catch (Exception)
        {
            // A file that is not a model, or a native library that will not load. The call
            // comes back with nothing rather than taking the app down with it.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OnnxTtsEngine?> SpeakerAsync(CancellationToken cancellationToken)
    {
        if (Files.Speaker is not { } path)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return _speaker ??= new OnnxTtsEngine(path, _options.SampleRate);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        _speaker?.Dispose();
        _gate.Dispose();
    }
}
