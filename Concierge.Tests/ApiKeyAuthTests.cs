using System.Text;
using Concierge.Web.Hosting;
using Microsoft.AspNetCore.Http;

namespace Concierge.Tests;

public sealed class ApiKeyAuthTests
{
    [Fact]
    public async Task Middleware_is_a_passthrough_when_no_key_is_configured()
    {
        var context = NewContext("/chat");
        var middleware = new ApiKeyAuthMiddleware(_ => Task.CompletedTask, new ApiKeyAuthOptions { ConfiguredKey = null });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Missing_header_returns_401_when_key_is_configured()
    {
        var context = NewContext("/chat");
        var middleware = new ApiKeyAuthMiddleware(_ => Task.CompletedTask, new ApiKeyAuthOptions { ConfiguredKey = "secret-key" });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(context.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [Fact]
    public async Task Wrong_header_returns_401_even_when_length_matches()
    {
        var context = NewContext("/chat");
        context.Request.Headers[ApiKeyAuthMiddleware.HeaderName] = "wrong-keyx"; // same length as "secret-key"
        var middleware = new ApiKeyAuthMiddleware(_ => Task.CompletedTask, new ApiKeyAuthOptions { ConfiguredKey = "secret-key" });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Correct_header_passes_through_to_the_next_middleware()
    {
        var context = NewContext("/chat");
        context.Request.Headers[ApiKeyAuthMiddleware.HeaderName] = "secret-key";
        var invoked = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { invoked = true; return Task.CompletedTask; }, new ApiKeyAuthOptions { ConfiguredKey = "secret-key" });

        await middleware.InvokeAsync(context);

        Assert.True(invoked);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/metrics")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/Concierge.Shared.Components/concierge-mermaid.js")]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css")]
    [InlineData("/app.css")]
    [InlineData("/favicon.png")]
    public async Task Health_metrics_and_static_paths_bypass_the_gate_so_monitoring_stays_alive(string path)
    {
        var context = NewContext(path);
        var invoked = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { invoked = true; return Task.CompletedTask; }, new ApiKeyAuthOptions { ConfiguredKey = "secret-key" });

        await middleware.InvokeAsync(context);

        Assert.True(invoked);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static DefaultHttpContext NewContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
