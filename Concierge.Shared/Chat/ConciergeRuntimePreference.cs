namespace Concierge.Shared.Chat;

/// <summary>Chooses the same client connection for chat and background work. Circle AI owns model selection.</summary>
public static class ConciergeRuntimePreference
{
    public static IChatRuntime? Choose(IEnumerable<IChatRuntime> runtimes)
    {
        var available = runtimes.ToArray();
        // Keep the service selected while it prepares. Do not silently send work elsewhere.
        return available.FirstOrDefault(r => string.Equals(r.Id, "circleai", StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(r => r.IsReady) ?? available.FirstOrDefault();
    }
}
