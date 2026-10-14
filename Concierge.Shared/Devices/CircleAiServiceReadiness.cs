namespace Concierge.Shared.Devices;

/// <summary>Reads the service's per-ability report; never infers readiness from files or hardware.</summary>
public static class CircleAiServiceReadiness
{
    // The 3.8.1 LinkSetupRows.Ability shape has a display title, not a stable capability ID.
    // Match the SDK's current Answering title exactly and fail closed on unknown/localized rows.
    // AbilityState.On explicitly means downloaded, wired and actually working, unlike Available.
    public static bool TextAnsweringIsOn(IReadOnlyList<IReadOnlyList<string>> rows)
        => rows.Any(row => row.Count >= 5 && row[0] == "Answering" && row[2] == "On");
}
