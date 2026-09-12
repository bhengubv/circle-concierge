using CircleAI.Core;
using CircleAI.Inference;
using Concierge.Shared.Chat;

namespace Concierge.Ai;

/// <summary>
/// The speech models, and getting them.
///
/// **`LocalVoiceFiles` waits for somebody to put files in a folder by hand, and nothing ever
/// puts them there.** Its own comment says the files "are not ours to redistribute", and that
/// is out of date: the voices in CircleAI's registry live in `thegeekco/circleai-voices` and
/// `bhengubv/circleai-voices`. They are ours. There are eighty-eight models catalogued and
/// the chat runtime has fetched its own since the day it was written, through exactly the
/// loader used here.
///
/// So voice was not blocked on a licence or on a missing file. It was blocked on nobody
/// having wired the same three lines the chat model already uses — the ninth thing in this
/// repository found written, reachable, and never called.
/// </summary>
/// <remarks>
/// **Which two, and why.**
///
/// `Whisper-tiny-ggml` is 78 MB and is what the Butler tier matrix specifies for Lite and
/// Mini devices — the phones most people actually own. Listening has to work on those or it
/// does not matter that it works here.
///
/// `Vits-11ZA-int8` is 32 MB and speaks eleven South African languages from one file. The
/// full-precision version is 122 MB for the same eleven. On a 3 GB phone over mobile data,
/// four times the download for a quality difference nobody asked about is the wrong trade,
/// and the bigger one can be chosen later by name.
/// </remarks>
public sealed class VoiceModels(string modelsDirectory)
{
    /// <summary>What listens. Small on purpose — see the note on the class.</summary>
    public const string Listener = "Whisper-tiny-ggml";

    /// <summary>What speaks: eleven South African languages in one 32 MB file.</summary>
    public const string Speaker = "Vits-11ZA-int8";

    private readonly string _models = modelsDirectory
        ?? throw new ArgumentNullException(nameof(modelsDirectory));

    /// <summary>
    /// What is here and what is not, without hashing anything.
    ///
    /// Presence, not integrity — hashing both models to answer "can I speak" would cost a
    /// hundred megabytes of reading on a screen that asks every time it opens. The load path
    /// hashes; this one asks.
    /// </summary>
    public VoiceModelState Look()
    {
        try
        {
            using var loader = new BundleModelLoader(_models);

            var listening = loader.ModelPresent(Listener);
            var speaking = loader.ModelPresent(Speaker);

            return new VoiceModelState(
                listening ? Path.GetFullPath(loader.GetModelPath(Listener)) : null,
                speaking ? Path.GetFullPath(loader.GetModelPath(Speaker)) : null,
                _models);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or FileNotFoundException or InvalidOperationException)
        {
            // A models folder that cannot be read is the same answer as an empty one. A
            // missing capability must never be able to stop the app starting.
            return new VoiceModelState(null, null, _models);
        }
    }

    /// <summary>
    /// Fetches whichever of the two is missing.
    /// </summary>
    /// <remarks>
    /// Roughly 110 MB over somebody's connection, which on a South African mobile plan is
    /// real money — so this is called once a person has said yes, never on start-up, and the
    /// size is quoted before the question.
    ///
    /// Returns what it managed rather than throwing. Half of speech is a real outcome: with
    /// only the listener, Concierge can hear and not answer aloud, and that is worth having
    /// and worth saying.
    /// </remarks>
    public async Task<VoiceModelState> FetchAsync(
        IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_models);

        using var loader = new BundleModelLoader(_models);

        var trouble = new List<string>();

        foreach (var model in new[] { Listener, Speaker })
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (loader.ModelPresent(model))
            {
                continue;
            }

            // The rich overload, the same one the chat runtime uses: the IProgress<float> one
            // drops the byte counts and the ETA a call before the screen, and takes no
            // cancellation token — so a stop button wired to it would be a lie.
            var relayed = progress is null
                ? null
                : new Progress<DownloadProgress>(report =>
                    progress.Report(new ModelDownloadProgress(
                        report.Ratio, $"{Named(model)} — {report.Describe()}")));

            try
            {
                await loader.DownloadModelAsync(model, relayed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // Kept and carried out, not swallowed. The other model may still arrive, so
                // this cannot throw — but the first version discarded the reason entirely and
                // the caller was left with "this can hear but not speak" and no idea why.
                // That is the defect this whole repository argues against, committed while
                // building the fix for it.
                trouble.Add($"{Named(model)}: {failure.Message}");
            }
        }

        return Look() with { Trouble = trouble.Count == 0 ? null : string.Join(" ", trouble) };
    }

    /// <summary>
    /// What to call a model while it is downloading.
    ///
    /// "Vits-11ZA-int8 — 14.2 MB of 32.4 MB" tells somebody nothing about what they are
    /// waiting for. They are waiting for a voice.
    /// </summary>
    private static string Named(string model) => model == Listener ? "Listening" : "Voice";
}

/// <summary>Which speech models are on this machine.</summary>
/// <param name="Listener">The whisper model, or null.</param>
/// <param name="Speaker">The voice model, or null.</param>
/// <param name="Folder">Where they live.</param>
public sealed record VoiceModelState(string? Listener, string? Speaker, string Folder)
{
    /// <summary>
    /// What went wrong while fetching, when something did.
    ///
    /// Separate from <see cref="Why"/> because they answer different questions: Why says what
    /// this device can do, Trouble says why it cannot do more. A person who just waited for a
    /// download that half worked needs the second one.
    /// </summary>
    public string? Trouble { get; init; }

    /// <summary>Whether anything at all is here.</summary>
    public bool Anything => Listener is not null || Speaker is not null;

    /// <summary>Whether both halves are here, so speech works in both directions.</summary>
    public bool Both => Listener is not null && Speaker is not null;

    /// <summary>
    /// What is here and what is not, in a sentence somebody can act on.
    ///
    /// Half is named as half. "Speech is not set up" for a machine that can hear perfectly
    /// well is the kind of almost-true that sends somebody looking for a fault that is not
    /// there.
    /// </summary>
    public string Why => (Listener, Speaker) switch
    {
        (not null, not null) => "Speech works both ways on this device.",
        (not null, null) => "This can hear but not speak — the voice is not downloaded yet.",
        (null, not null) => "This can speak but not hear — the listener is not downloaded yet.",
        _ => "Speech is not set up. About 110 MB, and then it works on the device with no key "
             + "and no network.",
    };

    /// <summary>Why plus whatever went wrong, which is what a screen should show.</summary>
    public string Everything => Trouble is null ? Why : $"{Why} {Trouble}";
}
