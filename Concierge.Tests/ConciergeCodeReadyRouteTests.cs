using Concierge.Shared.Tools;

namespace Concierge.Tests;

public sealed class ConciergeCodeReadyRouteTests
{
    [Fact]
    public void Ready_route_keeps_the_project_folder_and_adds_only_the_local_token()
    {
        var uri = ConciergeCodeReadyRoute.BuildUri(53911,
            "/?folder=%2Fmnt%2Fc%2FTest%20Project", new string('a', 64));

        Assert.Equal("http", uri.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(53911, uri.Port);
        Assert.Equal("/?folder=%2Fmnt%2Fc%2FTest%20Project&tkn=" + new string('a', 64),
            uri.PathAndQuery);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/")]
    [InlineData("/?folder=")]
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
