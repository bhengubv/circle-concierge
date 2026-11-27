using Concierge.Web.Hosting;
using Microsoft.AspNetCore.Http;

namespace Concierge.Tests;

public sealed class WebSecurityHeadersTests
{
    [Fact]
    public async Task Dynamic_responses_cannot_be_framed_or_sniffed_and_do_not_send_referrers()
    {
        var context = new DefaultHttpContext();
        var reachedNext = false;
        var middleware = new WebSecurityHeadersMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(reachedNext);
        Assert.Equal("frame-ancestors 'none'", context.Response.Headers["Content-Security-Policy"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"]);
    }
}
