namespace Concierge.Web.Hosting;

/// <summary>Prevents a published Web host from serving local personal data anonymously.</summary>
public static class WebHostAuthPolicy
{
    public static void RequireProtectedHost(bool isDevelopment, string? apiKey)
    {
        if (!isDevelopment && string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Concierge Web cannot start outside Development without authentication. " +
                "Configure CONCIERGE_API_KEY until phone-approved browser sign-in is available.");
    }
}
