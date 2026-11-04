namespace Concierge.Shared.Chat;

/// <summary>A runtime that can retain a question locally or in its service's queue
/// while preparation proceeds. Acceptance does not mean inference is ready.</summary>
public interface IChatRuntimePreparation
{
    bool CanWaitForPreparation { get; }

    /// <summary>True only while loading or downloading is actually reported. A queued
    /// request or an unknown readiness report alone is not preparation progress.</summary>
    bool IsActivelyPreparing => CanWaitForPreparation;
}
