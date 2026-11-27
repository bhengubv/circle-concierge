namespace Concierge.Shared.Tools;

/// <summary>Validates Concierge Code's local launch route before adding its one-time fragment.</summary>
public static class ConciergeCodeReadyRoute
{
    public static Uri BuildUri(int port, string? route, string token)
    {
        if (port is < 1 or > 65535 || route != "/")
            throw new InvalidOperationException("Concierge Code returned an invalid project route.");
        return BuildFragmentUri(port, route, token);
    }

    public static Uri BuildStudioUri(int port, string? route, string token, string kind)
    {
        if (port is < 1 or > 65535 || route != $"/?kind={kind}"
            || kind is not ("games" or "video" or "audio" or "images"))
            throw new InvalidOperationException("Concierge Code returned an invalid studio route.");
        return BuildFragmentUri(port, route, token);
    }

    private static Uri BuildFragmentUri(int port, string route, string token)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Concierge Code returned an invalid launch token.");
        return new Uri($"http://127.0.0.1:{port}{route}#tkn={token}");
    }
}
