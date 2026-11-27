namespace Concierge.Web.Hosting;

/// <summary>Browser protections that do not depend on a chosen authentication method.</summary>
public sealed class WebSecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        return next(context);
    }
}

public static class WebSecurityHeadersExtensions
{
    public static IApplicationBuilder UseConciergeSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<WebSecurityHeadersMiddleware>();
}
