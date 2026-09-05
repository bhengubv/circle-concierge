namespace Concierge.Shared.Skills;

/// <summary>
/// A skill discoverable in the catalog. Extends the lightweight <c>SkillInfo</c>
/// record (which the Skills page binds to) with everything the runtime needs to
/// actually ACTIVATE the skill in a conversation — the path to the markdown
/// body, the source it came from, any declared tools or model preferences.
///
/// Why a separate type from SkillInfo: SkillInfo is a card-display contract; it
/// must stay narrow because the Skills page renders thousands of these. Loading
/// the full SKILL.md body for every card on render would make scrolling janky.
/// SkillDescriptor is the runtime-side model that picks up where SkillInfo
/// stops — we resolve a descriptor lazily when the user actually picks a skill.
/// </summary>
public sealed record SkillDescriptor(
    string Id,
    string Name,
    string Area,
    string Description,
    /// <summary>Where to fetch the skill body. May be a file path, an
    /// embedded-resource name, or a remote URL — the loader picks the strategy
    /// from this string's prefix (<c>file://</c>, <c>embed://</c>,
    /// <c>https://</c>).</summary>
    string BodyUri,
    /// <summary>Source identifier — "bundled" | "curated" | "downloaded:&lt;name&gt;" |
    /// "user". Lets the UI badge a skill so users know what they're activating.</summary>
    string Source,
    /// <summary>Tool ids the skill expects to use, if any. Concierge's tool
    /// registry will check these are available at activation time.</summary>
    IReadOnlyList<string> Tools,
    /// <summary>Preferred model family — e.g. "claude" | "openai" | "gemini".
    /// Null when the skill has no preference.</summary>
    string? PreferredProvider,
    /// <summary>Free-form tags (red-team, finance, code-review, ...).</summary>
    IReadOnlyList<string> Tags)
{
    public SkillInfo ToInfo() => new(Id, Name, Area, Description, Source);
}

/// <summary>
/// The resolved-and-loaded form of a skill — body text + parsed metadata,
/// ready to inject into a chat system prompt.
/// </summary>
public sealed record SkillActivation(
    SkillDescriptor Descriptor,
    /// <summary>The markdown body BELOW the YAML front-matter — what gets
    /// prepended to the system prompt.</summary>
    string Body,
    /// <summary>Sanitised system-prompt text. Caller can prepend this directly.</summary>
    string SystemPromptAddendum,
    /// <summary>Estimated token count of the body (rough 4 chars/token).</summary>
    int EstimatedTokens);
