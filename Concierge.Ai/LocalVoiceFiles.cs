namespace Concierge.Ai;

/// <summary>
/// What speech files are on this machine, and where they were looked for.
///
/// `CircleAI.Voice` ships the code for both directions — `WhisperTranscriber` writes out
/// what is said, `OnnxTtsEngine` speaks — and neither can do anything without a model file
/// beside it. Those files are large, they are not ours to redistribute, and nobody has put
/// them here yet.
///
/// So this is a look on disk rather than a configuration setting. A capability that is
/// present when its files are and absent otherwise is the same rule every device capability
/// follows: a head that cannot do a thing does not advertise it, rather than offering it and
/// failing when it is used.
///
/// **Where it looks is said out loud** — `Why` names the folder and what to put in it, so
/// somebody who wants speech has an answer rather than a runtime that is quietly not ready.
/// </summary>
/// <param name="Listener">The whisper model file, or null when there is not one.</param>
/// <param name="Speaker">The voice model file, or null when there is not one.</param>
/// <param name="Folder">Where both were looked for.</param>
/// <param name="Why">What is missing and what to do about it, in a sentence.</param>
public sealed record LocalVoiceFiles(string? Listener, string? Speaker, string Folder, string Why)
{
    /// <summary>Whether anything at all was found.</summary>
    public bool Anything => Listener is not null || Speaker is not null;

    /// <summary>The folder speech files go in, under the models folder the chat runtime uses.</summary>
    public static string FolderUnder(string modelsDirectory)
        => Path.Combine(modelsDirectory, "voice");

    /// <summary>
    /// Look. Never throws — a folder that cannot be read is the same answer as an empty one,
    /// because a missing capability must not be able to stop the app starting.
    /// </summary>
    public static LocalVoiceFiles Look(string modelsDirectory)
    {
        var folder = FolderUnder(modelsDirectory);

        var listener = Newest(folder, "*.bin");
        var speaker = Newest(folder, "*.onnx");

        return new LocalVoiceFiles(listener, speaker, folder, Sentence(listener, speaker, folder));
    }

    private static string? Newest(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Sentence(string? listener, string? speaker, string folder) => (listener, speaker) switch
    {
        (not null, not null) =>
            $"Listening with {Path.GetFileName(listener)} and speaking with {Path.GetFileName(speaker)}.",

        (not null, null) =>
            $"Listening with {Path.GetFileName(listener)}. Nothing here can speak — put a voice "
            + $"model (.onnx) in {folder}.",

        (null, not null) =>
            $"Speaking with {Path.GetFileName(speaker)}. Nothing here can listen — put a whisper "
            + $"model (.bin) in {folder}.",

        _ =>
            $"No speech files. Put a whisper model (.bin) and a voice model (.onnx) in {folder}, "
            + "and this works on the device with no key and nothing to configure.",
    };
}
