using Concierge.Shared.Tools;

namespace Concierge.Tests;

public sealed class ConciergeCodeReadyRouteTests
{
    [Fact]
    public void Native_editor_launch_uses_a_fragment_not_a_request_url_token()
    {
        var uri = ConciergeCodeReadyRoute.BuildUri(53911, "/", new string('a', 64));

        Assert.Equal("http", uri.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(53911, uri.Port);
        Assert.Equal("/", uri.PathAndQuery);
        Assert.Equal("#tkn=" + new string('a', 64), uri.Fragment);
    }

    [Fact]
    public void Studio_launch_keeps_the_workspace_kind_and_the_token_out_of_requests()
    {
        var uri = ConciergeCodeReadyRoute.BuildStudioUri(53911, "/?kind=audio", new string('b', 64), "audio");

        Assert.Equal("/?kind=audio", uri.PathAndQuery);
        Assert.Equal("#tkn=" + new string('b', 64), uri.Fragment);
    }

    [Theory]
    [InlineData("/?kind=audio&tkn=secret", "audio")]
    [InlineData("/?kind=video", "audio")]
    [InlineData("/?kind=other", "other")]
    public void Studio_route_rejects_unexpected_or_credential_bearing_routes(string route, string kind)
        => Assert.Throws<InvalidOperationException>(() =>
            ConciergeCodeReadyRoute.BuildStudioUri(53911, route, new string('a', 64), kind));

    [Theory]
    [InlineData(null)]
    [InlineData("/?folder=")]
    [InlineData("/?folder=%2Fmnt%2Fc")]
    [InlineData("/other?folder=%2Fmnt%2Fc")]
    [InlineData("/?folder=https%3A%2F%2Fexample.com")]
    [InlineData("/?folder=%2F%2Fexample.com")]
    [InlineData("/?folder=%2Fmnt%2Fc&redirect=elsewhere")]
    [InlineData("/?folder=%2Fmnt%2Fc#fragment")]
    [InlineData("/?folder=%252Fmnt%252Fc")]
    public void Ready_route_rejects_missing_or_unexpected_destinations(string? route)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ConciergeCodeReadyRoute.BuildUri(53911, route, new string('a', 64)));
    }
}
