namespace Concierge.Shared.Tools;

/// <summary>Conversation and run identity attached to approvals requested by the active turn.</summary>
public sealed record ToolApprovalRun(Guid ConversationId, Guid RunId);

/// <summary>Flows the active turn identity to approval tools without changing every tool API.</summary>
public static class ToolApprovalRunContext
{
    private static readonly AsyncLocal<ToolApprovalRun?> CurrentRun = new();

    /// <summary>The run whose tool is currently requesting approval, if any.</summary>
    public static ToolApprovalRun? Current => CurrentRun.Value;

    /// <summary>Attaches an identity for the current async execution flow.</summary>
    public static IDisposable Enter(Guid conversationId, Guid runId)
    {
        var previous = CurrentRun.Value;
        CurrentRun.Value = new ToolApprovalRun(conversationId, runId);
        return new Scope(() => CurrentRun.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
