using CircleAI.Hosting.Neuron;
using Concierge.Shared.Chat;

namespace Concierge.Ai;

/// <summary>
/// Uses Circle AI's turn router to choose a capability tier, then compares the live
/// capability/resource snapshots of this device and its connected Desktop. Ordinary
/// turns stay on the current device; specialist work uses the most capable ready head.
/// </summary>
public sealed class CircleAiExecutionDeviceSelector : IChatExecutionDeviceSelector
{
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(30);
    private readonly INeuronRouter _turnRouter;

    public CircleAiExecutionDeviceSelector(INeuronRouter? turnRouter = null)
        => _turnRouter = turnRouter ?? new HeuristicNeuronRouter();

    public ChatExecutionDecision Select(
        string userInput,
        bool hasImage,
        bool localCanAnswer,
        ChatRuntimeCapabilityCatalog? local,
        bool desktopCanAnswer,
        ChatRuntimeCapabilityCatalog? desktop,
        DateTimeOffset now)
    {
        var route = _turnRouter.Route(new RouteContext(userInput ?? string.Empty, hasImage));
        var required = route.Organ == Organ.Specialist
            ? CapabilityId(route.Capability)
            : "conversation";

        var localReady = localCanAnswer && Supports(local, required);
        var desktopReady = desktopCanAnswer && IsFresh(desktop, now) && Supports(desktop, required);
        var localConversation = local?.Capabilities.FirstOrDefault(c => c.Id == "conversation");
        var requiredFact = local?.Capabilities.FirstOrDefault(c => c.Id == required);

        if (localReady)
            return Local(required, "This device can handle the query. A larger Desktop alone is not a reason to transfer it.");
        if (requiredFact?.State is ChatRuntimeCapabilityState.Preparing or ChatRuntimeCapabilityState.ActionRequired
            || !localCanAnswer && localConversation?.State is ChatRuntimeCapabilityState.Preparing or ChatRuntimeCapabilityState.ActionRequired)
            return Local(required, "Circle AI on this device needs preparation or approval. Do not silently transfer while it is getting ready.");
        var confirmedLimit = requiredFact?.State == ChatRuntimeCapabilityState.Unavailable;
        var confirmedFailure = !localCanAnswer && localConversation?.State == ChatRuntimeCapabilityState.Unavailable;
        if (desktopReady && (confirmedLimit || confirmedFailure))
            return Desktop(required, "The current device reports a failure or cannot handle this capability; the connected Desktop can.");
        if (localCanAnswer && requiredFact is null)
            return Local(required, "The service has not reported a limit for this query. Try the current device; do not infer a limit from Desktop's greater resources.");
        return Local(required, "No justified automatic transfer was reported. The user can still choose Desktop manually.");
    }

    private static bool IsFresh(ChatRuntimeCapabilityCatalog? catalog, DateTimeOffset now)
    {
        if (catalog is null)
            return false;
        var age = now - catalog.ObservedAt;
        return age >= TimeSpan.Zero && age <= MaximumSnapshotAge;
    }

    private static bool Supports(ChatRuntimeCapabilityCatalog? catalog, string capability)
        => catalog?.Capabilities.Any(item =>
            string.Equals(item.Id, capability, StringComparison.OrdinalIgnoreCase)
            && item.State == ChatRuntimeCapabilityState.Available) == true;

    private static string CapabilityId(CircleAI.Inference.ChatCapability capability)
    {
        if ((capability & CircleAI.Inference.ChatCapability.Vision) != 0)
            return "visual-input";
        if ((capability & CircleAI.Inference.ChatCapability.LongContext) != 0)
            return "long-context";
        if ((capability & CircleAI.Inference.ChatCapability.Reasoning) != 0)
            return "reasoning";
        return "conversation";
    }

    private static ChatExecutionDecision Local(string required, string reason)
        => new(ChatExecutionTarget.LocalDevice, required, reason);

    private static ChatExecutionDecision Desktop(string required, string reason)
        => new(ChatExecutionTarget.ConnectedDesktop, required, reason);
}
