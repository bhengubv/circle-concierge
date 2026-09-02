namespace Concierge.Shared.Tools;

/// <summary>
/// Outcome of asking a person whether one tool call may run. A grant covers the call it was
/// asked for and nothing else — there is no "allow always".
/// </summary>
public enum ToolApprovalDecision
{
    /// <summary>Nobody could be asked: no approver is wired, or the channel is gone.</summary>
    Unavailable = 0,

    /// <summary>The person refused this call.</summary>
    Denied = 1,

    /// <summary>The person allowed this call, once.</summary>
    Allowed = 2,
}

/// <summary>
/// One request for permission, carrying enough for a person to decide without reading the
/// code that produced it.
/// </summary>
/// <param name="ToolName">The tool being called, e.g. <c>write_file</c>.</param>
/// <param name="Summary">One plain line naming what will happen if allowed.</param>
/// <param name="Risk">The tool's declared risk, so a surface can style the prompt.</param>
/// <param name="Detail">
/// The evidence the decision rests on — a diff for a write, the command line for a run.
/// Null when there is nothing further to show.
/// </param>
public sealed record ToolApprovalRequest(
    string ToolName,
    string Summary,
    ConciergeToolRisk Risk,
    string? Detail = null);

/// <summary>
/// Channel-neutral approval seam. A host answers through whatever surface it has — a MAUI
/// dialog, a web prompt, a console question — and the tool layer never learns which.
/// </summary>
/// <remarks>
/// Anything other than <see cref="ToolApprovalDecision.Allowed"/> is a refusal, so a missing
/// or broken approver fails closed instead of quietly permitting the call.
/// </remarks>
public interface IToolApprovalService
{
    /// <summary>Ask whether this call may proceed.</summary>
    ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The default when a host wires no approver: every request answers
/// <see cref="ToolApprovalDecision.Unavailable"/>, so acting tools refuse rather than run
/// unattended.
/// </summary>
public sealed class UnavailableToolApprovalService : IToolApprovalService
{
    /// <summary>The shared stateless instance.</summary>
    public static UnavailableToolApprovalService Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ToolApprovalDecision.Unavailable);
}
