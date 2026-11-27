using System.Text;
using System.Net;
using Concierge.Web.Hosting;
using Microsoft.AspNetCore.Http;

namespace Concierge.Tests;

public sealed class ApiKeyAuthTests
{
    [Fact]
    public void Published_web_host_refuses_anonymous_startup()
    {
        Assert.Throws<InvalidOperationException>(() => WebHostAuthPolicy.RequireProtectedHost(false, null));
        Assert.Throws<InvalidOperationException>(() => WebHostAuthPolicy.RequireProtectedHost(false, "  "));
        WebHostAuthPolicy.RequireProtectedHost(false, "test-key");
        WebHostAuthPolicy.RequireProtectedHost(true, null);
    }

    [Fact]
    public async Task Local_same_origin_development_works_without_a_key()
    {
        var context = NewContext("/chat");
        var middleware = new ApiKeyAuthMiddleware(_ => Task.CompletedTask, new ApiKeyAuthOptions { ConfiguredKey = null });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("192.168.0.12", "localhost", null)]
    [InlineData("127.0.0.1", "attacker.example", null)]
    [InlineData("127.0.0.1", "localhost", "https://attacker.example")]
    [InlineData("127.0.0.1", "localhost", "http://localhost:9999")]
    public async Task Anonymous_development_rejects_remote_peers_and_foreign_origins(
        string peer, string host, string? origin)
    {
        var context = NewContext("/api/away/conversations");
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Host = new HostString(host, 5199);
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        var invoked = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { invoked = true; return Task.CompletedTask; },
            new ApiKeyAuthOptions());

        await middleware.InvokeAsync(context);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Same_origin_request_on_loopback_is_allowed_without_a_key()
    {
        var context = NewContext("/api/away/say");
        context.Request.Headers.Origin = "http://localhost:5199";
        var invoked = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { invoked = true; return Task.CompletedTask; },
            new ApiKeyAuthOptions());

        await middleware.InvokeAsync(context);

        Assert.True(invoked);
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
    [InlineData("http", StatusCodes.Status403Forbidden, false)]
    [InlineData("https", StatusCodes.Status200OK, true)]
    public async Task Keyed_remote_access_requires_https(string scheme, int expectedStatus, bool shouldPass)
    {
        var context = NewContext("/api/away/say");
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.0.12");
        context.Request.Scheme = scheme;
        context.Request.Headers[ApiKeyAuthMiddleware.HeaderName] = "secret-key";
        var invoked = false;
        var middleware = new ApiKeyAuthMiddleware(_ => { invoked = true; return Task.CompletedTask; },
            new ApiKeyAuthOptions { ConfiguredKey = "secret-key" });

        await middleware.InvokeAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(shouldPass, invoked);
    }

    [Theory]
    [InlineData("/healthz-private")]
    [InlineData("/metrics-admin")]
    [InlineData("/metrics")]
    [InlineData("/_framework-secret")]
    [InlineData("/app.css-private")]
    public async Task Similar_looking_private_paths_do_not_bypass_the_gate(string path)
    {
        var context = NewContext(path);
        var middleware = new ApiKeyAuthMiddleware(_ => Task.CompletedTask,
            new ApiKeyAuthOptions { ConfiguredKey = "secret-key" });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/Concierge.Shared.Components/concierge-mermaid.js")]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css")]
    [InlineData("/app.css")]
    [InlineData("/favicon.png")]
    public async Task Health_and_static_paths_bypass_the_gate_so_the_shell_stays_alive(string path)
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
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5199);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
