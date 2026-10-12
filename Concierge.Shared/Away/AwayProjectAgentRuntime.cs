using System.Runtime.CompilerServices;
using Concierge.Shared.Chat;
using Concierge.Shared.Planning;

namespace Concierge.Away;

/// <summary>Routes a scoped, read-only Project analysis through a connected Desktop.</summary>
public sealed class AwayProjectAgentRuntime(AwayClient client) : IProjectAgentChatRuntime
{
    public string Id => "circleai";
    public string EngineLabel => "Circle AI on Desktop";
    public bool IsReady => true;
    public string StatusMessage => "The connected Desktop will run this Project analysis.";
    public bool LeavesDevice => true;

    public async IAsyncEnumerable<string> StreamProjectAgentAsync(ProjectAgentDispatch dispatch,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var answer = await client.SayProjectAgentAsync(dispatch.Task,
            new Situation(DateTimeOffset.UtcNow), dispatch.RunId, dispatch.ProjectId,
            dispatch.WorkspaceId, dispatch.ConversationId, dispatch.SystemInstructions,
            dispatch.ReferenceContext, cancellationToken).ConfigureAwait(false);
        if (!answer.Understood || string.IsNullOrWhiteSpace(answer.Reply))
            throw new InvalidOperationException(answer.Reply ?? "The Desktop did not return an analysis.");

        yield return answer.Reply;
    }

    public IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Desktop routing requires the Project-agent dispatch contract.");
}

/// <summary>Selects Desktop for Project analysis when its measured usable memory exceeds Mobile's.</summary>
public sealed class AwayProjectAgentRuntimeSelector(AwayClient client) : IProjectAgentRuntimeSelector
{
    public async Task<IChatRuntime?> SelectAsync(IChatRuntime local, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(local.Id, "circleai", StringComparison.OrdinalIgnoreCase))
            return local;

        var remote = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (remote is not { CanAnswer: true, CircleAi: { } catalog })
            return local.IsReady ? local : null;

        var required = catalog.Capabilities.Where(capability => capability.Id is "conversation" or "reasoning")
            .ToArray();
        if (required.Length < 2 || required.Any(capability => capability.State != ChatRuntimeCapabilityState.Available))
            return local.IsReady ? local : null;

        var localMemory = (local as IChatRuntimeCapabilitySource)?.CapabilityCatalog?.Resources?.UsableMemoryGb;
        var remoteMemory = catalog.Resources?.UsableMemoryGb;
        return remoteMemory is > 0 && (localMemory is null || remoteMemory > localMemory)
            ? new AwayProjectAgentRuntime(client)
            : local.IsReady ? local : null;
    }
}
