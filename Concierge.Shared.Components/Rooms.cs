using Concierge.Shared;

namespace Concierge.Shared.Components;

/// <summary>
/// The two things every room needs to say about a HardeningStatus, in one
/// place because five rooms render it and five copies of a colour mapping is
/// how the colours drift apart.
///
/// Status is a dot and a word. Never a filled card, never a coloured badge,
/// never a coloured card edge — that rule is why this returns a dot class and
/// a plain English word and nothing else.
/// </summary>
public static class Rooms
{
    public static string Dot(HardeningStatus status) => status switch
    {
        HardeningStatus.Ready => "dot-done",
        HardeningStatus.NeedsOwner => "dot-waiting",
        _ => "dot-danger"
    };

    /// <summary>
    /// "NeedsOwner" is the enum's word, not a person's. What it means to
    /// somebody reading the room is that the work exists and nobody has picked
    /// it up.
    /// </summary>
    public static string Word(HardeningStatus status) => status switch
    {
        HardeningStatus.Ready => "Ready",
        HardeningStatus.NeedsOwner => "Needs an owner",
        _ => "Blocked"
    };
}
