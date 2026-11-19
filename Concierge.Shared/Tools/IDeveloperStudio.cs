namespace Concierge.Shared.Tools;

/// <summary>An optional local editor for a Project's source files.</summary>
public interface IDeveloperStudio
{
    event EventHandler? Closed;
    event EventHandler? AskCircleAiRequested;
    Task<Uri> OpenAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetInstalledExtensionsAsync(CancellationToken cancellationToken = default);
    Task InstallSetupAsync(string setupId, CancellationToken cancellationToken = default);
    Task CloseAsync();
}
