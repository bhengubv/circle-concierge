namespace Concierge.Shared;

/// <summary>
/// What a risk level means to the person being asked.
///
/// This lives in Concierge.Shared rather than in a head because every head
/// asks the same question and must give the same answer. It was briefly
/// written twice — once in WorkspaceBase for the web recipes and again in the
/// Wear OS head — which is precisely the drift RecipeTests exists to prevent:
/// three form factors are allowed to look different and are not allowed to
/// disagree about what the product does.
/// </summary>
public static class ApprovalRisk
{
    /// <summary>
    /// What a risk level means in reach.
    ///
    /// Derived from the level rather than written per item, because the queue
    /// carries no scope field and a specific-sounding sentence that is not
    /// backed by data is worse than a general one that is true.
    /// </summary>
    public static string ReachOf(string? risk) => Normalise(risk) switch
    {
        "high" or "critical" => "It can reach files and tools outside this conversation",
        "medium" => "It can change things inside this workspace",
        _ => "It stays inside this conversation",
    };

    /// <summary>
    /// The severity, without naming a colour or a CSS class — those belong to
    /// whichever head is drawing it, and a watch has no stylesheet.
    /// </summary>
    public static ApprovalSeverity SeverityOf(string? risk) => Normalise(risk) switch
    {
        "high" or "critical" => ApprovalSeverity.Danger,
        "medium" => ApprovalSeverity.Caution,
        "low" => ApprovalSeverity.Settled,
        _ => ApprovalSeverity.Unknown,
    };

    private static string Normalise(string? risk) => (risk ?? string.Empty).ToLowerInvariant();
}

/// <summary>How loudly a risk should be shown. Status is a dot and a word in
/// every head, so this is the whole vocabulary.</summary>
public enum ApprovalSeverity
{
    Unknown,
    Settled,
    Caution,
    Danger
}
