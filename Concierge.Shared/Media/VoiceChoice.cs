namespace Concierge.Shared.Media;

/// <summary>
/// Which registered voice does which half, when there is more than one.
///
/// Two rules, both stated once here rather than in each caller. A runtime is only considered
/// for what it says it can do — listening and speaking are separate, and a runtime can offer
/// one and not the other. And where two can do the same thing, **the one on this device
/// wins**: a recording is about as personal as a file gets, and somebody's own voice being
/// sent to a company they have never heard of should not be what happens when they say
/// nothing.
///
/// Whether a runtime is local is read off its id rather than kept in a list here. A second
/// list of who is local is a second thing to keep in step with the first, which is how the
/// two come to disagree.
/// </summary>
public static class VoiceChoice
{
    private const string Local = "circleai";

    /// <summary>Whichever can write out what is said, on this device first.</summary>
    public static IVoiceRuntime? Ears(IEnumerable<IVoiceRuntime> voices)
        => Best(voices, voice => voice.SupportsTranscription);

    /// <summary>Whichever can speak, on this device first.</summary>
    public static IVoiceRuntime? Mouth(IEnumerable<IVoiceRuntime> voices)
        => Best(voices, voice => voice.SupportsSynthesis);

    private static IVoiceRuntime? Best(IEnumerable<IVoiceRuntime> voices, Func<IVoiceRuntime, bool> can)
    {
        ArgumentNullException.ThrowIfNull(voices);

        return voices
            .Where(can)
            .OrderByDescending(voice => voice.Id.Contains(Local, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }
}
