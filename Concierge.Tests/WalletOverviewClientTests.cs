using System.Net;
using System.Text;
using Concierge.Shared.Butler;
using Concierge.Shared.Wallet;

namespace Concierge.Tests;

public sealed class WalletOverviewClientTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WalletId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Signed_out_person_does_not_send_a_wallet_request()
    {
        var requests = 0;
        var client = CreateClient(new StubAccount(null), _ => { requests++; return Json(HttpStatusCode.OK, "[]"); });

        var result = await client.ReadAsync();

        Assert.Equal(WalletOverviewState.SignInRequired, result.State);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Temporary_account_verification_failure_does_not_claim_sign_out()
    {
        var requests = 0;
        var client = CreateClient(new StubAccount(null, storedAccount: true), _ => { requests++; return Json(HttpStatusCode.OK, "[]"); });

        var result = await client.ReadAsync();

        Assert.Equal(WalletOverviewState.Unavailable, result.State);
        Assert.Contains("could not be verified", result.Message);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Reads_only_the_signed_in_persons_wallets_with_their_bearer()
    {
        var authorization = new OnlineAccountAuthorization(UserId.ToString(), "private-access-token");
        var client = CreateClient(new StubAccount(authorization), request =>
        {
            Assert.Equal($"/api/wallets/user/{UserId}", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("private-access-token", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK,
                $"[{{\"walletId\":\"{WalletId}\",\"userId\":\"{UserId}\",\"currencyCode\":\"ZAR\",\"balanceAvailable\":12.50,\"balanceCurrent\":14.00,\"status\":\"active\",\"isDefault\":true}}]");
        });

        var result = await client.ReadAsync();

        Assert.Equal(WalletOverviewState.Ready, result.State);
        var wallet = Assert.Single(result.Wallets);
        Assert.Equal(WalletId, wallet.WalletId);
        Assert.Equal(12.50m, wallet.BalanceAvailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rejected_account_never_shows_a_cached_balance(HttpStatusCode status)
    {
        var client = CreateClient(new StubAccount(new(UserId.ToString(), "expired")), _ => Json(status, "{}"));

        var result = await client.ReadAsync();

        Assert.Equal(WalletOverviewState.SignInRequired, result.State);
        Assert.Empty(result.Wallets);
    }

    [Fact]
    public async Task Missing_wallet_is_distinct_from_a_service_error()
    {
        var account = new StubAccount(new(UserId.ToString(), "access"));
        Assert.Equal(WalletOverviewState.NoWallet,
            (await CreateClient(account, _ => Json(HttpStatusCode.OK, "[]")).ReadAsync()).State);
        Assert.Equal(WalletOverviewState.Unavailable,
            (await CreateClient(account, _ => Json(HttpStatusCode.InternalServerError, "{}")).ReadAsync()).State);
    }

    [Fact]
    public async Task Mismatched_owner_is_never_displayed()
    {
        var client = CreateClient(new StubAccount(new(UserId.ToString(), "access")), _ => Json(HttpStatusCode.OK,
            $"[{{\"walletId\":\"{WalletId}\",\"userId\":\"33333333-3333-3333-3333-333333333333\",\"currencyCode\":\"ZAR\",\"balanceAvailable\":500.00,\"balanceCurrent\":500.00,\"status\":\"active\",\"isDefault\":true}}]"));

        var result = await client.ReadAsync();

        Assert.Equal(WalletOverviewState.Unavailable, result.State);
        Assert.Empty(result.Wallets);
    }

    private static WalletOverviewClient CreateClient(IButlerAccountSession account,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new CallbackHandler(respond)) { BaseAddress = new Uri("https://wallet.example.test/") }, account);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class StubAccount(OnlineAccountAuthorization? authorization, bool storedAccount = false) : IButlerAccountSession
    {
        public Task<OnlineAccountAuthorization?> GetAuthorizationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(authorization);
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(authorization?.AccessToken);
        public Task<ButlerAccountStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ButlerAccountStatus(true, storedAccount || authorization is not null, null, false));
        public Task<ButlerSignInResult> SignInAsync(string emailOrMobile, string password, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ButlerSignInResult> CompleteTwoFactorAsync(string code, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
