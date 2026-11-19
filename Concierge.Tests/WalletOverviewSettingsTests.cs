using Bunit;
using Concierge.Shared.Wallet;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class WalletOverviewSettingsTests : BunitContext
{
    [Fact]
    public async Task Balance_is_hidden_until_the_person_asks_to_view_it()
    {
        Services.AddSingleton<IWalletOverviewClient>(new StubWallet(new(WalletOverviewState.Ready, "Wallet details from TheGeekNetwork.",
            [new(Guid.NewGuid(), Guid.NewGuid(), "ZAR", 12.50m, 14m, "active", true)])));

        var panel = Render<Concierge.Shared.Components.Components.WalletOverviewSettings>();
        Assert.DoesNotContain("12.50", panel.Markup);

        panel.Find("button").Click();

        panel.WaitForAssertion(() => Assert.Contains("12.50", panel.Markup));
        Assert.Contains("This does not approve a payment", panel.Markup);

        await panel.Instance.ClearAsync();
        Assert.DoesNotContain("12.50", panel.Markup);
    }

    [Fact]
    public void Signed_out_state_explains_the_online_account_step()
    {
        Services.AddSingleton<IWalletOverviewClient>(new StubWallet(new(WalletOverviewState.SignInRequired,
            "Sign in to your TheGeekNetwork online account to view your wallet.", [])));

        var panel = Render<Concierge.Shared.Components.Components.WalletOverviewSettings>();
        panel.Find("button").Click();

        panel.WaitForAssertion(() => Assert.Contains("Sign in to your TheGeekNetwork online account", panel.Markup));
        Assert.Empty(panel.FindAll(".row strong"));
    }

    [Fact]
    public void Wallet_request_is_remembered_for_online_sign_in()
    {
        var signInRequests = 0;
        Services.AddSingleton<IWalletOverviewClient>(new StubWallet(new(WalletOverviewState.SignInRequired,
            "Sign in to view your wallet.", [])));

        var panel = Render<Concierge.Shared.Components.Components.WalletOverviewSettings>(parameters =>
            parameters.Add(component => component.SignInRequired, () => signInRequests++));
        panel.Find("button").Click();

        panel.WaitForAssertion(() => Assert.Equal(1, signInRequests));
        Assert.Empty(panel.FindAll(".row strong"));
    }

    [Fact]
    public void Web_without_a_secure_account_session_does_not_offer_a_dead_wallet_button()
    {
        Services.AddSingleton<IWalletOverviewClient>(new WalletOverviewUnavailable());

        var panel = Render<Concierge.Shared.Components.Components.WalletOverviewSettings>();

        Assert.Empty(panel.FindAll("button"));
        Assert.Contains("needs secure online sign-in", panel.Markup);
    }

    private sealed class StubWallet(WalletOverview overview) : IWalletOverviewClient
    {
        public Task<WalletOverview> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(overview);
    }
}
