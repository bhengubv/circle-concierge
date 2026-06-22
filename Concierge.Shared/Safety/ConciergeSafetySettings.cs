namespace Concierge.Shared.Safety;

/// <summary>
/// Family Mode configuration that the parental dashboard surfaces and the
/// content filter respects. Persisted alongside the other Concierge settings
/// in <c>%LocalAppData%/Concierge/safety/settings.json</c> so a hard restart
/// doesn't lose the parent's choices.
/// </summary>
/// <remarks>
/// The strictness level is a coarse dial — fine-grained category tuning lives
/// in the audit log review path, not here. Three steps match the way every
/// other consumer product spells this: Off (adult), Balanced (default — flag
/// but allow most), Strict (kid mode — refuse anything questionable).
/// </remarks>
public sealed class ConciergeSafetySettings
{
    /// <summary>
    /// When true, every IChatRuntime call routes through the content filter
    /// and the kid-mode system-prompt snippet (from
    /// CircleAI.SafetyChild.SafetyChildDomainContext) is injected at the top
    /// of the conversation. Defaults to false — adult-mode is the natural
    /// out-of-box state for a power-user assistant.
    /// </summary>
    public bool KidMode { get; set; }

    /// <summary>
    /// How aggressive the filter is. <see cref="SafetyStrictness.Strict"/>
    /// is the implicit setting when <see cref="KidMode"/> is on; advanced
    /// parents can override to <see cref="SafetyStrictness.Balanced"/> for
    /// older children. <see cref="SafetyStrictness.Off"/> disables the
    /// filter entirely; provided so the parental dashboard can show "we
    /// are not filtering" honestly instead of pretending Off doesn't exist.
    /// </summary>
    public SafetyStrictness Strictness { get; set; } = SafetyStrictness.Off;

    /// <summary>Optional display label shown in the chat UI when KidMode is on
    /// (e.g. "Lebo's account"). Does not affect filtering.</summary>
    public string? ProfileLabel { get; set; }
}

public enum SafetyStrictness
{
    /// <summary>No filtering — runtime calls pass through unchanged.</summary>
    Off = 0,

    /// <summary>Flag risky categories in the audit log but only refuse the
    /// most-confident hits. Suitable for older children and supervised
    /// shared accounts.</summary>
    Balanced = 1,

    /// <summary>Refuse anything the filter flags above the low-confidence
    /// threshold. Suitable for primary-school kid mode.</summary>
    Strict = 2,
}
