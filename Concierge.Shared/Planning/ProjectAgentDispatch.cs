using Concierge.Shared.Chat;

namespace Concierge.Shared.Planning;

/// <summary>Identity and scoped reference data for one Project agent run.</summary>
public sealed record ProjectAgentDispatch(
    Guid RunId,
    Guid ProjectId,
    Guid? WorkspaceId,
    Guid? ConversationId,
    string Name,
    string Task,
    string SystemInstructions,
    string ReferenceContext);

/// <summary>A runtime able to carry a whole agent run to another execution device.</summary>
public interface IProjectAgentChatRuntime : IChatRuntime
{
    IAsyncEnumerable<string> StreamProjectAgentAsync(
        ProjectAgentDispatch dispatch,
        CancellationToken cancellationToken = default);
}

/// <summary>Chooses the strongest currently ready Circle AI head without exposing model choice.</summary>
public interface IProjectAgentRuntimeSelector
{
    Task<IChatRuntime?> SelectAsync(IChatRuntime local, CancellationToken cancellationToken = default);
}
