namespace Concierge.Shared;

public enum HardeningStatus
{
    Ready,
    NeedsOwner,
    Blocked
}

public enum ProviderAccessKind
{
    DirectOfficialApi,
    OfficialPartnerApi
}

public sealed record ProviderInfo(
    string Id,
    string Name,
    string OfficialApi,
    string LatestFamily,
    ProviderAccessKind AccessKind = ProviderAccessKind.DirectOfficialApi);

/// <param name="Source">
/// Where it came from — "bundled" for the ones that ship inside the assembly,
/// or the name of the folder it was found in. Defaulted so every existing
/// construction still compiles, and surfaced because "why is it answering like
/// that?" is answered by which skills are on and where they came from.
/// </param>
public sealed record SkillInfo(
    string Id, string Name, string Area, string Summary, string Source = "bundled");

public sealed record ProductionTask(int Priority, string Title, HardeningStatus Status, string Summary);

public sealed record ProductionGate(string Id, string Name, HardeningStatus Status, string Summary, string Evidence);

public sealed record ResourceControlSettings(
    int MaxConcurrentRuns,
    int MaxBackgroundRuns,
    int RunTimeoutMinutes,
    string PriorityProfile,
    bool CleanupOwnedProcesses,
    bool EmergencyStopEnabled);

public sealed record WorkloadInfo(string Id, string Title, string State, string CleanupState, DateTimeOffset UpdatedAt);

public sealed record ApprovalRequest(string Id, string Title, string Risk, string Summary, DateTimeOffset CreatedAt);

public sealed record DiagnosticCheck(string Id, string Name, HardeningStatus Status, string Evidence);

public sealed record ProductionReadinessItem(
    int Priority,
    string Id,
    string Name,
    HardeningStatus Status,
    string CodeCoverage,
    string Evidence);

public sealed record ConciergeSnapshot(
    IReadOnlyList<ProviderInfo> Providers,
    IReadOnlyList<SkillInfo> Skills,
    IReadOnlyList<ProductionTask> ProductionTasks,
    IReadOnlyList<ProductionGate> ProductionGates,
    IReadOnlyList<ApprovalRequest> Approvals,
    IReadOnlyList<WorkloadInfo> Workloads,
    ResourceControlSettings ResourceControl,
    IReadOnlyList<DiagnosticCheck> Diagnostics);
