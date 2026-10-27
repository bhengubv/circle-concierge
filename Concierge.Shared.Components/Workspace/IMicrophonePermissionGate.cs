namespace Concierge.Shared.Components.Workspace;

/// <summary>
/// Optional host hook for asking the operating system for microphone access.
/// Browser hosts rely on their own permission prompt.
/// </summary>
public interface IMicrophonePermissionGate
{
    Task<bool> EnsureGrantedAsync();
}
