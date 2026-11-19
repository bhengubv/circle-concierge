namespace Concierge.Shared.Tools;

/// <summary>An optional local editor for a Project's source files.</summary>
public interface IDeveloperStudio
{
    event EventHandler? Closed;
    event EventHandler? AskCircleAiRequested;
    Task<Uri> OpenAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default);
    Task<Uri> OpenWorkspaceAsync(Guid projectId, string projectName, string workspaceKind, CancellationToken cancellationToken = default)
        => string.Equals(workspaceKind, "code", StringComparison.OrdinalIgnoreCase)
            ? OpenAsync(projectId, projectName, cancellationToken)
            : throw new NotSupportedException($"The {workspaceKind} workspace is not installed on this device.");
    Task<IReadOnlySet<string>> GetSupportedWorkspaceKindsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "code" });
    Task<IReadOnlySet<string>> GetInstalledExtensionsAsync(CancellationToken cancellationToken = default);
    Task InstallSetupAsync(string setupId, CancellationToken cancellationToken = default);
    Task CloseAsync();
}
