namespace Concierge.Shared.Devices;

public static class CircleAiReadCachePolicy
{
    // Only parameter-free reports may use these lifetimes. Queries, replies and writes never do.
    public static TimeSpan LifetimeFor(string verb) => TimeSpan.FromSeconds(verb switch
    {
        "Hello" => 5,
        "Capabilities" => 30,
        "Abilities" or "SetupPlan" or "SetupCensus" or "Footprint" => 5,
        "Permissions" => 2,
        "Status" or "SetupReadiness" or "SetupProgress" or "ResidentStatus" => 1,
        _ => 0
    });

    // An uncached read is not a mutation. In particular, polling AskStatus and
    // checking Hello must not evict the readiness/catalogue snapshots each time.
    // Unknown verbs remain conservative so new writes cannot leave stale settings.
    public static bool InvalidatesReads(string verb) => verb is not
        ("Hello" or "Capabilities" or "Abilities" or "SetupPlan" or "SetupCensus"
        or "Footprint" or "Permissions" or "Status" or "SetupReadiness"
        or "SetupProgress" or "ResidentStatus" or "AskStatus");
}
