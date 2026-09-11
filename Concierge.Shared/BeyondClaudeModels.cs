namespace Concierge.Shared;

/// <summary>
/// Where one of the eleven things in Beyond Code actually stands.
///
/// Its own three words rather than `HardeningStatus`, because the fact that room needs to
/// report is not how hardened something is — it is **whether the product does this at all**,
/// and `HardeningStatus` has no word for "nobody has built this". Every one of the eleven was
/// therefore `Ready`, under a heading reading "What it can do", beside six that did not exist
/// in any form: no memory store, no helper teams, no simulations, no server access, no
/// small-machine profiles, no security checks.
/// </summary>
public enum BeyondState
{
    /// <summary>Built, and reachable from this app today.</summary>
    InTheApp,

    /// <summary>Built, and waiting on a key or an account somebody has to supply.</summary>
    NeedsAKey,

    /// <summary>A direction. Nothing behind it yet, and the room says so rather than badging it.</summary>
    NotBuilt
}

public sealed record BeyondCapability(
    string Id,
    string Name,
    string PlainEnglishName,
    string InspiredBy,
    string Summary,
    string WhyItMatters,
    string SafetyBoundary,
    BeyondState State,
    /// <summary>
    /// What in the app delivers this, named so somebody can go and check. Empty for anything
    /// not built — a claim with nowhere to go and look is the thing this whole change is about.
    /// </summary>
    string Where = "");

public sealed record MemoryRoom(
    string Id,
    string Name,
    string Purpose,
    bool StoresRawLocalEvidence,
    bool PortableAcrossDevices);

public sealed record WorkforceAgent(
    string Name,
    string Role,
    string DefaultBoundary);

public sealed record WorkforceSwarm(
    string Id,
    string Name,
    string Purpose,
    IReadOnlyList<WorkforceAgent> Agents);

public sealed record SimulationScenario(
    string Id,
    string Name,
    string PlainEnglishQuestion,
    string EvidenceNeeded,
    string Output);

public sealed record InfrastructureLane(
    string Id,
    string Name,
    string Purpose,
    string SafetyBoundary);

public sealed record DiagramEvidenceTemplate(
    string Id,
    string Name,
    string UseWhen,
    string Output);

public sealed record SafeResumePolicy(
    bool RequiresUserApproval,
    bool KeepsOriginalSandbox,
    bool BlocksPermissionBypass,
    bool StopsWhenContextChanges,
    string Summary);

public sealed record BeyondClaudeSnapshot(
    IReadOnlyList<BeyondCapability> Capabilities,
    IReadOnlyList<MemoryRoom> MemoryRooms,
    IReadOnlyList<WorkforceSwarm> WorkforceSwarms,
    IReadOnlyList<SimulationScenario> Simulations,
    IReadOnlyList<InfrastructureLane> InfrastructureLanes,
    IReadOnlyList<DiagramEvidenceTemplate> DiagramTemplates,
    SafeResumePolicy SafeResume);
