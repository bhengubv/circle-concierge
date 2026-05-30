namespace Concierge.Shared;

public enum ConciergeToolRisk
{
    Low,
    Medium,
    High
}

public enum ConciergeToolOutcome
{
    Succeeded,
    ApprovalRequired,
    Denied,
    Failed
}

public sealed record ConciergeToolDefinition(
    string Name,
    string UserFacingName,
    string Description,
    bool IsReadOnly,
    bool IsDestructive,
    bool IsConcurrencySafe,
    ConciergeToolRisk Risk);

public sealed record ConciergeToolResult(
    string ToolName,
    ConciergeToolOutcome Outcome,
    string Summary,
    string Output,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int? ExitCode = null);

public sealed record ConciergeRunLog(
    string Id,
    string Goal,
    IReadOnlyList<ConciergeToolResult> Results,
    DateTimeOffset CreatedAt);

public sealed record FileWritePreview(
    string RelativePath,
    string ProposedContent,
    string DiffPreview,
    bool RequiresApproval);
