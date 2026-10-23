namespace Concierge.Shared.Devices;

public sealed record CircleAiServiceChatStatus(bool Ready, string Stage, string Explanation, string Version,
    bool CanWaitForPreparation = false, double? PreparationFraction = null,
    bool AcceptsQueuedRequests = false);
public sealed record CircleAiServiceChatReply(bool Ok, string? Text, string? Error);

/// <summary>Published service transport. The service owns its conversation sessions and models.</summary>
public interface ICircleAiServiceChatLink
{
    Task<CircleAiServiceChatStatus> ReadChatStatusAsync(CancellationToken cancellationToken);
    Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, bool agentic,
        CancellationToken cancellationToken);
}

public interface ICircleAiServiceRequestProgress
{
    event Action<string>? RequestProgress;
}
