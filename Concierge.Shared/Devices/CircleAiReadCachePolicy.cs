namespace Concierge.Shared.Devices;

public static class CircleAiReadCachePolicy
{
    // Only parameter-free reports may use these lifetimes. Queries, replies and writes never do.
    public static TimeSpan LifetimeFor(string verb) => TimeSpan.FromSeconds(verb switch
    {
        "Capabilities" => 30,
        "Abilities" or "SetupPlan" or "SetupCensus" or "Footprint" => 5,
        "Permissions" => 2,
        "SetupReadiness" or "SetupProgress" or "ResidentStatus" => 1,
        _ => 0
    });
}
