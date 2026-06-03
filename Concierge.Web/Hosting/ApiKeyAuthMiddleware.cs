using System.Security.Cryptography;
using System.Text;

namespace Concierge.Web.Hosting;

/// <summary>
/// Optional header-based auth. When <see cref="ApiKeyAuthOptions.ConfiguredKey"/> is set
/// (typically via the <c>CONCIERGE_API_KEY</c> environment variable or an
/// <c>Auth:ApiKey</c> configuration entry), every request must carry an
/// <c>X-Concierge-Key</c> header that constant-time matches the configured value. Missing
/// or wrong key returns 401 with a plain-text body.
/// </summary>
/// <remarks>
/// When no key is configured the middleware is a passthrough — local dev keeps working
/// without setting anything. Health (<c>/healthz</c>) and metrics (<c>/metrics</c>) and
/// static asset paths (<c>/_framework/</c>, <c>/_content/</c>, <c>/lib/</c>, <c>app.css</c>,
/// favicon, the Blazor reconnect script) are always allowed so monitoring + the bootstrap
/// shell still work when auth is enabled.
/// </remarks>
public sealed class ApiKeyAuthMiddleware
{
    /// <summary>HTTP header callers send the key on.</summary>
    public const string HeaderName = "X-Concierge-Key";

    private static readonly string[] AlwaysAllowedPrefixes =
    [
        "/healthz",
        "/metrics",
        "/_framework/",
        "/_content/",
        "/lib/",
        "/app.css",
        "/favicon.png",
    ];

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
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var prefix in AlwaysAllowedPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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
    /// API key the middleware enforces. Empty / null means the middleware is a passthrough.
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
