namespace Concierge.Shared.Devices;

/// <summary>Projection of the published LinkSetupRows wire shapes. No client readiness guesses.</summary>
public static class CircleAiSettingsRows
{
    public static IReadOnlyList<CircleAiSettingFact> Project(string verb, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) return [new("Service report", "No details reported")];
        return rows.SelectMany(row => ProjectRow(verb, row)).ToArray();
    }

    private static IEnumerable<CircleAiSettingFact> ProjectRow(string verb, IReadOnlyList<string> row)
    {
        switch (verb)
        {
            case "ResidentStatus" when row.Count >= 4:
                return [new("Background listening", row[0], row[1]), new("Listening guidance", row[2])];
            case "SetupReadiness" when row.Count >= 4:
                return [new(row[1], row[0], row[2]), new("Overall setup readiness", Flag(row[3], "Ready", "Not ready"))];
            case "Abilities" when row.Count >= 5:
                return [new(row[0], row[2] == "TooBig" ? "Blocked" : row[2], row[2] == "TooBig"
                    ? row[1] + " Circle AI reports that its current resource plan does not fit. Model selection belongs to the service; there is no model choice to make here."
                    : row[1])];
            case "Permissions" when row.Count >= 4:
                return [new(row[0], Flag(row[2], "Allowed", "Not allowed"), row[1])];
            case "Footprint" when row.Count >= 3:
                // Storage's size field is service-formatted text, not a byte counter.
                return [new(string.IsNullOrWhiteSpace(row[0]) ? "Storage summary" : row[0],
                    string.IsNullOrWhiteSpace(row[1]) ? "Not reported" : row[1],
                    Flag(row[2], "Reusable resource; removing it may require rebuilding or downloading.", "Service-owned storage."))];
            case "SetupPlan" when row.Count >= 2:
                return [new(row[0], row[1] + " bytes planned", "Planned by Circle AI. This check does not start downloads.")];
            case "SetupCensus" when row.Count >= 4:
                return [new(row[0], Flag(row[1], "Present", "Missing"), row[3])];
            default:
                return [new("Service report", "Unrecognized response", "No readiness or setting value was inferred.")];
        }
    }
    private static string Flag(string value, string yes, string no) => value switch { "1" => yes, "0" => no, _ => "Not reported" };
}
