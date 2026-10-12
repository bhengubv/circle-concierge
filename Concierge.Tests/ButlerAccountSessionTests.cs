using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Shared.Butler;

namespace Concierge.Tests;

public sealed class ButlerAccountSessionTests
{
    [Fact]
    public async Task Sign_in_stores_the_TGN_identity_and_tokens_without_exposing_them_in_status()
    {
        var store = new MemoryTokenStore();
        var session = CreateSession(store, _ => Json(HttpStatusCode.OK, LoginResponse("access", DateTimeOffset.UtcNow.AddMinutes(10))));

        var result = await session.SignInAsync("person@example.com", "password");
        var status = await session.GetStatusAsync();

        Assert.Equal(ButlerSignInState.Connected, result.State);
        Assert.Equal("Butler account connected.", result.Message);
        Assert.True(status.IsConnected);
        Assert.Equal("person@example.com", status.Email);
        Assert.False(status.NeedsTwoFactor);
        Assert.Equal("access", (await store.LoadAsync())?.AccessToken);
    }

    [Fact]
    public async Task Two_factor_login_does_not_save_tokens_before_the_person_verifies()
    {
        var store = new MemoryTokenStore();
        var requests = new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """{"twoFactorRequired":true,"data":{"twoFactorToken":"challenge"}}"""),
            Json(HttpStatusCode.OK, LoginResponse("verified-access", DateTimeOffset.UtcNow.AddMinutes(10))),
        ]);
        var session = CreateSession(store, _ => requests.Dequeue());

        var first = await session.SignInAsync("person@example.com", "password");
        Assert.Equal(ButlerSignInState.NeedsTwoFactor, first.State);
        Assert.Null(await store.LoadAsync());
        Assert.True((await session.GetStatusAsync()).NeedsTwoFactor);

        var completed = await session.CompleteTwoFactorAsync("123456");

        Assert.Equal(ButlerSignInState.Connected, completed.State);
        Assert.Equal("verified-access", (await store.LoadAsync())?.AccessToken);
        Assert.False((await session.GetStatusAsync()).NeedsTwoFactor);
    }

    [Fact]
    public async Task Expired_access_token_rotates_refresh_credentials_before_returning_access()
    {
        var store = new MemoryTokenStore
        {
            Tokens = new ButlerSessionTokens("user-1", "person@example.com", MakeJwt(DateTimeOffset.UtcNow.AddMinutes(10)),
                DateTimeOffset.UtcNow.AddSeconds(-5), "old-refresh", Guid.Parse("11111111-1111-1111-1111-111111111111")),
        };
        var session = CreateSession(store, _ => Json(HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                data = new
                {
                    accessToken = MakeJwt(DateTimeOffset.UtcNow.AddMinutes(10)),
                    refreshToken = "new-refresh",
                    refreshTokenId = "22222222-2222-2222-2222-222222222222",
                },
            })));

        var accessToken = await session.GetAccessTokenAsync();
        var refreshed = await store.LoadAsync();

        Assert.NotNull(accessToken);
        Assert.Equal("new-refresh", refreshed?.RefreshToken);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), refreshed?.RefreshTokenId);
        Assert.True(refreshed?.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(8));
    }

    [Fact]
    public async Task Rejected_refresh_clears_the_expired_session()
    {
        var store = new MemoryTokenStore
        {
            Tokens = new ButlerSessionTokens("user-1", "person@example.com", "expired", DateTimeOffset.UtcNow.AddSeconds(-5),
                "refresh", Guid.NewGuid()),
        };
        var session = CreateSession(store, _ => Json(HttpStatusCode.Unauthorized, "{}"));

        Assert.Null(await session.GetAccessTokenAsync());
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Sign_out_revokes_refresh_token_and_clears_local_vault()
    {
        var store = new MemoryTokenStore
        {
            Tokens = new ButlerSessionTokens("user-1", "person@example.com", "access", DateTimeOffset.UtcNow.AddMinutes(10),
                "refresh", Guid.Parse("33333333-3333-3333-3333-333333333333")),
        };
        HttpRequestMessage? request = null;
        var session = CreateSession(store, current =>
        {
            request = current;
            return Json(HttpStatusCode.OK, "{}");
        });

        await session.SignOutAsync();

        Assert.Null(await store.LoadAsync());
        Assert.Equal("api/auth/revoke", request?.RequestUri?.PathAndQuery.TrimStart('/'));
        Assert.Contains("33333333-3333-3333-3333-333333333333", await request!.Content!.ReadAsStringAsync());
    }

    private static ButlerAccountSession CreateSession(MemoryTokenStore store, Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new CallbackHandler(respond)) { BaseAddress = new Uri("https://auth.example/") }, store);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string LoginResponse(string access, DateTimeOffset expires)
        => JsonSerializer.Serialize(new
        {
            data = new
            {
                userId = "user-1",
                email = "person@example.com",
                accessToken = access,
                refreshToken = "refresh",
                refreshTokenId = "44444444-4444-4444-4444-444444444444",
                expiresAt = expires,
            },
        });

    private static string MakeJwt(DateTimeOffset expiry)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { exp = expiry.ToUnixTimeSeconds() })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"header.{payload}.signature";
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class MemoryTokenStore : IButlerSessionTokenStore
    {
        public ButlerSessionTokens? Tokens { get; set; }
        public Task<ButlerSessionTokens?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Tokens);
        public Task SaveAsync(ButlerSessionTokens tokens, CancellationToken cancellationToken = default)
        {
            Tokens = tokens;
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Tokens = null;
            return Task.CompletedTask;
        }
    }
}
