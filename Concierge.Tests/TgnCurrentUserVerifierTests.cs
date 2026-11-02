using System.Net;
using System.Text.Json;
using Concierge.Shared.Identity;

namespace Concierge.Tests;

public sealed class TgnCurrentUserVerifierTests
{
    [Fact]
    public async Task OnlyAuthApiConfirmedAccountCanApprovePairing()
    {
        var ownerId = Guid.NewGuid();
        var handler = new StubHandler(request =>
        {
            Assert.Equal("/api/auth/me", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("phone-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data = new { userId = ownerId } }))
            };
        });
        var verifier = new TgnCurrentUserVerifier(new HttpClient(handler)
            { BaseAddress = new Uri("https://authapi.example.test") });

        Assert.Null(await verifier.VerifyAsync(null));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(ownerId, await verifier.VerifyAsync("phone-token"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.OK, "{\"data\":{\"userId\":\"00000000-0000-0000-0000-000000000000\"}}")]
    [InlineData(HttpStatusCode.OK, "{\"data\":{}}")]
    public async Task MissingOrRejectedIdentityCannotApprovePairing(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        var verifier = new TgnCurrentUserVerifier(new HttpClient(handler)
            { BaseAddress = new Uri("https://authapi.example.test") });

        Assert.Null(await verifier.VerifyAsync("phone-token"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
