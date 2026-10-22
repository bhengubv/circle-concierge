namespace Concierge.Shared.Chat;

/// <summary>A runtime that can hold a question locally while its service prepares, without starting inference.</summary>
public interface IChatRuntimePreparation
{
    bool CanWaitForPreparation { get; }
}
