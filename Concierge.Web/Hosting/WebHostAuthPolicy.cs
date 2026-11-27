namespace Concierge.Web.Hosting;

/// <summary>Prevents a published Web host from serving local personal data anonymously.</summary>
public static class WebHostAuthPolicy
{
    public static void RequireProtectedHost(bool isDevelopment, string? apiKey, bool allowSharedKeyPilot = false)
    {
        if (isDevelopment) return;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Concierge Web cannot start outside Development without authentication. " +
                "Configure CONCIERGE_API_KEY for a private pilot.");
        if (!allowSharedKeyPilot)
            throw new InvalidOperationException(
                "Concierge Web cannot serve a shared personal store to remote browsers yet. " +
                "Phone-approved, person-bound sign-in is required for a public host. " +
                "For a private, single-owner pilot only, explicitly set Auth:AllowSharedKeyPilot=true.");
    }
}
