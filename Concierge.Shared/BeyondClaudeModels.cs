namespace Concierge.Shared;

public sealed record BeyondCapability(
    string Id,
    string Name,
    string PlainEnglishName,
    string InspiredBy,
    string Summary,
    string WhyItMatters,
    string SafetyBoundary,
    HardeningStatus Status);

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
