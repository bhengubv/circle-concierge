namespace Concierge.Shared.Chat;

/// <summary>A runtime that can retain a question locally or in its service's queue
/// while preparation proceeds. Acceptance does not mean inference is ready.</summary>
public interface IChatRuntimePreparation
{
    bool CanWaitForPreparation { get; }
}
