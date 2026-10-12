using Concierge.Shared.Tools;

namespace Concierge.Shared.Planning;

/// <summary>Project and Start-Up ownership attached to the current assistant tool execution.</summary>
public sealed record ProjectToolRun(
    Guid? ProjectId,
    Guid? WorkspaceId,
    Guid ConversationId,
    Guid RunId,
    Guid? StartupJourneyId = null,
    ToolPermissionMode PermissionMode = ToolPermissionMode.AskFirst);

/// <summary>
/// Makes the current turn's Project/Start-Up scope available to work tools without allowing
/// model supplied IDs to select or mutate unrelated work.
/// </summary>
public static class ProjectToolRunContext
{
    private static readonly AsyncLocal<ProjectToolRun?> CurrentValue = new();

    public static ProjectToolRun? Current => CurrentValue.Value;

    public static IDisposable Enter(ProjectToolRun? run)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = run;
        return new Scope(previous);
    }

    private sealed class Scope(ProjectToolRun? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            CurrentValue.Value = previous;
            _disposed = true;
        }
    }
}
