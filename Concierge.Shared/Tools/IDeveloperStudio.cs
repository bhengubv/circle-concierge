namespace Concierge.Shared.Tools;

/// <summary>An optional local editor for a Project's source files.</summary>
public interface IDeveloperStudio
{
    event EventHandler? Closed;
    Task<Uri> OpenAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default);
    Task CloseAsync();
}
