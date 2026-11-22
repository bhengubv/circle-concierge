namespace Concierge.Shared.Tools;

/// <summary>Validates the route returned by Concierge Code's version 1 ready event.</summary>
public static class ConciergeCodeReadyRoute
{
    public static Uri BuildUri(int port, string? route, string token)
    {
        if (port is < 1 or > 65535 || string.IsNullOrEmpty(route)
            || !route.StartsWith("/?folder=", StringComparison.Ordinal))
            throw new InvalidOperationException("Concierge Code returned an invalid project route.");

        var encodedFolder = route["/?folder=".Length..];
        if (encodedFolder.Length == 0 || encodedFolder.IndexOfAny(['&', '?', '#']) >= 0)
            throw new InvalidOperationException("Concierge Code returned an invalid project route.");

        var folder = Uri.UnescapeDataString(encodedFolder);
        if (!folder.StartsWith("/", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)
            || folder.Any(char.IsControl)
            || !string.Equals(Uri.EscapeDataString(folder), encodedFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Concierge Code returned an invalid project route.");

        return new Uri($"http://127.0.0.1:{port}{route}&tkn={Uri.EscapeDataString(token)}");
    }
}
