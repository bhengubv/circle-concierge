using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace Concierge.Web.Hosting;

/// <summary>
/// Header-based auth. When <see cref="ApiKeyAuthOptions.ConfiguredKey"/> is set
/// (typically via the <c>CONCIERGE_API_KEY</c> environment variable or an
/// <c>Auth:ApiKey</c> configuration entry), every request must carry an
/// <c>X-Concierge-Key</c> header that constant-time matches the configured value. Missing
/// or wrong key returns 401 with a plain-text body.
/// </summary>
/// <remarks>
/// When no key is configured, only same-origin requests from a loopback peer to a
/// loopback host are allowed. Health (<c>/healthz</c>) and
/// static asset paths (<c>/_framework/</c>, <c>/_content/</c>, <c>/lib/</c>, <c>app.css</c>,
/// favicon, the Blazor reconnect script) are always allowed so monitoring + the bootstrap
/// shell still work when auth is enabled.
/// </remarks>
public sealed class ApiKeyAuthMiddleware
{
    /// <summary>HTTP header callers send the key on.</summary>
    public const string HeaderName = "X-Concierge-Key";

    private static readonly string[] AlwaysAllowedPaths =
    [
        "/healthz",
        "/app.css",
        "/favicon.png",
    ];

    private static readonly string[] StaticAssetPaths = ["/_framework", "/_content", "/lib"];

    private readonly RequestDelegate _next;
    private readonly ApiKeyAuthOptions _options;

    public ApiKeyAuthMiddleware(RequestDelegate next, ApiKeyAuthOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrEmpty(_options.ConfiguredKey))
        {
            if (!IsLocalDevelopmentRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Concierge Web needs authentication outside this device.").ConfigureAwait(false);
                return;
            }
            await _next(context).ConfigureAwait(false);
            return;
        }

        // A shared key sent to a remote HTTP endpoint can be copied by anyone on
        // that network. Local reverse proxies may still terminate TLS themselves.
        if (!context.Request.IsHttps && !IsLoopbackPeer(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Use HTTPS for remote Concierge access.").ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path;
        foreach (var allowedPath in AlwaysAllowedPaths)
        {
            if (path.Equals(new PathString(allowedPath), StringComparison.OrdinalIgnoreCase))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }
        }

        foreach (var assetPath in StaticAssetPaths)
        {
            if (path.StartsWithSegments(new PathString(assetPath), StringComparison.OrdinalIgnoreCase))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var presented) || presented.Count == 0)
        {
            await Reject(context).ConfigureAwait(false);
            return;
        }

        var expected = Encoding.UTF8.GetBytes(_options.ConfiguredKey);
        var actual = Encoding.UTF8.GetBytes(presented.ToString());
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            await Reject(context).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsLocalDevelopmentRequest(HttpContext context)
    {
        if (!IsLoopbackPeer(context.Connection.RemoteIpAddress))
            return false;

        var host = context.Request.Host.Host;
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && (!IPAddress.TryParse(host, out var hostIp)
                || !IPAddress.IsLoopback(hostIp.IsIPv4MappedToIPv6 ? hostIp.MapToIPv4() : hostIp)))
            return false;

        // Browsers attach Origin to cross-site writes and WebSocket handshakes.
        // A foreign site must not drive the unauthenticated local Blazor circuit.
        if (context.Request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin))
                return false;
            var requestPort = context.Request.Host.Port
                ?? (context.Request.IsHttps ? 443 : 80);
            if (!origin.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                || origin.Port != requestPort
                || !origin.Scheme.Equals(context.Request.Scheme, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool IsLoopbackPeer(IPAddress? remote)
        => remote is not null && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);

    private static async Task Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers["WWW-Authenticate"] = $"ApiKey realm=\"Concierge\", header=\"{HeaderName}\"";
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Concierge requires an API key. Send the X-Concierge-Key header.").ConfigureAwait(false);
    }
}

public sealed class ApiKeyAuthOptions
{
    /// <summary>
    /// API key the middleware enforces. Empty / null allows local same-origin development only.
    /// Wired in <c>Program.cs</c> to read from <c>CONCIERGE_API_KEY</c> environment variable
    /// first, then <c>Auth:ApiKey</c> from configuration as a fallback.
    /// </summary>
    public string? ConfiguredKey { get; init; }
}

public static class ApiKeyAuthExtensions
{
    public static IApplicationBuilder UseConciergeApiKeyAuth(this IApplicationBuilder app, ApiKeyAuthOptions options)
        => app.UseMiddleware<ApiKeyAuthMiddleware>(options);
}
