using Fluxor;

namespace Concierge.Shared.Components.State;

// A UI projection of BackgroundRuns. The runner remains the source of truth;
// this state lets Desktop and Mobile render the same immutable snapshot.
[FeatureState]
public sealed record RunningConversationsState
{
    public IReadOnlyList<Guid> ConversationIds { get; init; } = [];
}

public sealed record RunningConversationsRefreshed(IReadOnlyList<Guid> ConversationIds);

public static class RunningConversationsReducers
{
    [ReducerMethod]
    public static RunningConversationsState Reduce(
        RunningConversationsState state, RunningConversationsRefreshed action)
        => state with { ConversationIds = action.ConversationIds.Distinct().ToArray() };
}
