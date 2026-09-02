namespace Concierge.Shared.Tools;

/// <summary>
/// The named permission levels a session can run at. One choice decides both which tools the
/// session may reach and whether it must ask before acting, so a restricted session is a
/// smaller composition rather than a normal one with rules bolted on.
/// </summary>
public enum ConciergePermissionPreset
{
    /// <summary>Reading only. No tool that can change anything is offered at all.</summary>
    ReadOnly = 0,

    /// <summary>Reading and writing inside the workspace, each write approved. No commands.</summary>
    WorkspaceWrite = 1,

    /// <summary>Everything, unattended. For an operator who has accepted the consequences.</summary>
    FullAccess = 2,
}

/// <summary>
/// Applies a <see cref="ConciergePermissionPreset"/> to a session: it narrows the tool
/// catalogue and decides how approval behaves.
/// </summary>
/// <remarks>
/// <para>
/// Two guarantees hold together. <see cref="SelectTools"/> means a forbidden tool is never
/// offered to the model, and <see cref="Wrap"/> means it could not act even if something
/// called it anyway — a read-only session refuses a write regardless of what an approver says.
/// Composition is the primary control; the wrapper is the backstop.
/// </para>
/// <para>
/// This is the seam Kid Mode should use. A child's session does not need a rule telling it
/// not to run commands if the shell was never in its catalogue.
/// </para>
/// </remarks>
public sealed class ConciergePermissionPolicy
{
    private ConciergePermissionPolicy(ConciergePermissionPreset preset) => Preset = preset;

    /// <summary>The preset this policy applies.</summary>
    public ConciergePermissionPreset Preset { get; }

    /// <summary>
    /// The level a host gets when it expresses no opinion: writing is possible with approval,
    /// commands are not offered. The safe middle, not the powerful one.
    /// </summary>
    public static ConciergePermissionPolicy Default { get; } = For(ConciergePermissionPreset.WorkspaceWrite);

    /// <summary>Build the policy for one preset.</summary>
    public static ConciergePermissionPolicy For(ConciergePermissionPreset preset) => new(preset);

    /// <summary>Whether this level permits a tool to be offered at all.</summary>
    public bool Permits(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Preset switch
        {
            ConciergePermissionPreset.ReadOnly => tool.IsReadOnly,
            // Writing is permitted; running arbitrary commands is not. `run_command` is
            // matched by name because "not read-only" alone cannot separate the two.
            ConciergePermissionPreset.WorkspaceWrite =>
                tool.IsReadOnly || !string.Equals(tool.Name, "run_command", StringComparison.Ordinal),
            ConciergePermissionPreset.FullAccess => true,
            _ => tool.IsReadOnly,
        };
    }

    /// <summary>Narrow a catalogue to the tools this level allows, preserving order.</summary>
    public IReadOnlyList<IAgentTool> SelectTools(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Where(Permits).ToList();
    }

    /// <summary>
    /// Apply this level's approval behaviour over a host's approver: read-only refuses
    /// everything, full access allows everything without asking, and workspace-write defers
    /// to the person.
    /// </summary>
    public IToolApprovalService Wrap(IToolApprovalService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return Preset switch
        {
            ConciergePermissionPreset.ReadOnly => FixedApprovalService.Denied,
            ConciergePermissionPreset.FullAccess => FixedApprovalService.Allowed,
            _ => inner,
        };
    }
}

/// <summary>
/// Answers every request the same way, without consulting anyone. Used by the presets that
/// have already made the decision — never as a host's default.
/// </summary>
internal sealed class FixedApprovalService : IToolApprovalService
{
    private readonly ToolApprovalDecision _decision;

    private FixedApprovalService(ToolApprovalDecision decision) => _decision = decision;

    /// <summary>Refuses everything — the read-only backstop.</summary>
    public static FixedApprovalService Denied { get; } = new(ToolApprovalDecision.Denied);

    /// <summary>Allows everything without asking — full access.</summary>
    public static FixedApprovalService Allowed { get; } = new(ToolApprovalDecision.Allowed);

    /// <inheritdoc />
    public ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_decision);
}
