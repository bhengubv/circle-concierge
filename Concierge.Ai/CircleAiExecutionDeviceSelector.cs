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

        if (route.Organ != Organ.Specialist)
        {
            if (localReady)
                return Local(required, "Ordinary work stays on this device while its Circle AI runtime is ready.");
            if (desktopReady)
                return Desktop(required, "This device is not ready for conversation; the connected Desktop is ready.");
            return Local(required, "No ready Circle AI destination was advertised; keep the request on this device for the normal readiness path.");
        }

        if (desktopReady && !localReady)
            return Desktop(required, $"Circle AI selected the Desktop because only it currently advertises {required}.");
        if (localReady && !desktopReady)
            return Local(required, $"Circle AI selected this device because only it currently advertises {required}.");
        if (!localReady && !desktopReady)
        {
            // Keep the one existing best-effort path when neither catalogue can meet the
            // specialist requirement. Runtime readiness still decides whether it can answer.
            if (!localCanAnswer && desktopCanAnswer && IsFresh(desktop, now) && Supports(desktop, "conversation"))
                return Desktop(required, $"Neither head advertises {required}; Desktop is the only ready conversation runtime.");
            return Local(required, $"Neither head advertises {required}; use the local runtime's honest capability limits.");
        }

        var resourceWinner = CompareResources(local?.Resources, desktop?.Resources);
        return resourceWinner > 0
            ? Desktop(required, $"Both heads advertise {required}; Desktop has greater measured resource headroom.")
            : Local(required, $"Both heads advertise {required}; this device has equal or greater measured resource headroom.");
    }

    private static int CompareResources(
        ChatRuntimeResourceSnapshot? local,
        ChatRuntimeResourceSnapshot? desktop)
    {
        var localMemory = local?.UsableMemoryGb;
        var desktopMemory = desktop?.UsableMemoryGb;
        if (localMemory is > 0 && desktopMemory is > 0)
        {
            var memory = desktopMemory.Value.CompareTo(localMemory.Value);
            if (memory != 0)
                return memory;
        }

        var localCores = local?.CpuCores;
        var desktopCores = desktop?.CpuCores;
        if (localCores is > 0 && desktopCores is > 0)
            return desktopCores.Value.CompareTo(localCores.Value);

        return 0;
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
