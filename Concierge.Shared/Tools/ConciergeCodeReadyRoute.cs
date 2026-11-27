namespace Concierge.Shared.Tools;

/// <summary>Validates the route returned by Concierge Code's version 1 ready event.</summary>
public static class ConciergeCodeReadyRoute
{
    public static Uri BuildUri(int port, string? route, string token)
    {
        if (port is < 1 or > 65535 || route != "/")
            throw new InvalidOperationException("Concierge Code returned an invalid project route.");
        return new Uri($"http://127.0.0.1:{port}/?tkn={Uri.EscapeDataString(token)}");
    }
}
