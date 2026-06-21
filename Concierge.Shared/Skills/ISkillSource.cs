namespace Concierge.Shared.Skills;

/// <summary>
/// A place skills come from — embedded resources in a DLL, a folder on disk, a
/// remote catalog index. The catalog service composes any number of sources to
/// produce the unified list users see in the Skills tab.
///
/// Each source returns lightweight descriptors (no body loading) so discovery
/// stays cheap. The body is fetched lazily by <see cref="ISkillRuntime"/> when
/// a skill is actually activated.
/// </summary>
public interface ISkillSource
{
    /// <summary>Stable identifier for this source — used as the descriptor's
    /// <c>Source</c> tag and in logs. e.g. "bundled" | "filesystem" |
    /// "downloaded:Claude-BugHunter".</summary>
    string Name { get; }

    /// <summary>Enumerate all skills this source knows about. Implementations
    /// should be idempotent and safe to call repeatedly — the catalog caches
    /// the result.</summary>
    IReadOnlyList<SkillDescriptor> Discover();

    /// <summary>Read the markdown body for a previously-discovered descriptor.
    /// Returns null when the body is unavailable (file moved, etc.).</summary>
    string? LoadBody(SkillDescriptor descriptor);
}
