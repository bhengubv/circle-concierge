using Bunit;
using Concierge.Shared.Butler;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ButlerAccountSettingsTests : BunitContext
{
    [Fact]
    public void Online_sign_in_returns_to_the_pending_action_only_after_verification()
    {
        var account = new TwoStepAccount();
        var signedIn = 0;
        Services.AddSingleton<IButlerAccountSession>(account);

        var panel = Render<Concierge.Shared.Components.Components.ButlerAccountSettings>(parameters => parameters
            .Add(component => component.IsHandheld, true)
            .Add(component => component.SignedIn, () => signedIn++));
        panel.WaitForAssertion(() => Assert.Contains("Email or mobile number", panel.Markup));
        Assert.Contains("Sign In: Online", panel.Markup);

        panel.Find("input[type=text]").Change("person@example.test");
        panel.Find("input[type=password]").Change("test-only-password");
        panel.Find("button.btn-primary").Click();

        panel.WaitForAssertion(() => Assert.Contains("Verification code", panel.Markup));
        Assert.Equal(0, signedIn);
        Assert.DoesNotContain("test-only-password", panel.Markup);

        panel.Find("input[inputmode=numeric]").Change("123456");
        panel.Find("button.btn-primary").Click();

        panel.WaitForAssertion(() => Assert.Equal(1, signedIn));
        Assert.Contains("Connected as", panel.Markup);
        Assert.DoesNotContain("123456", panel.Markup);
    }

    private sealed class TwoStepAccount : IButlerAccountSession
    {
        private bool _needsTwoFactor;
        private bool _connected;

        public Task<ButlerAccountStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ButlerAccountStatus(true, _connected,
                _connected ? "person@example.test" : null, _needsTwoFactor));

        public Task<ButlerSignInResult> SignInAsync(string emailOrMobile, string password,
            CancellationToken cancellationToken = default)
        {
            _needsTwoFactor = true;
            return Task.FromResult(new ButlerSignInResult(ButlerSignInState.NeedsTwoFactor,
                "Enter the verification code."));
        }

        public Task<ButlerSignInResult> CompleteTwoFactorAsync(string code,
            CancellationToken cancellationToken = default)
        {
            _needsTwoFactor = false;
            _connected = true;
            return Task.FromResult(new ButlerSignInResult(ButlerSignInState.Connected, "Connected."));
        }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<OnlineAccountAuthorization?> GetAuthorizationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<OnlineAccountAuthorization?>(null);

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            return Task.CompletedTask;
        }
    }
}
