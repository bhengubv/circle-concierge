using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Concierge.Shared.Butler;

namespace Concierge.Shared.Wallet;

public enum WalletOverviewState { SignInRequired, Ready, NoWallet, Unavailable }

public sealed record WalletSummary(Guid WalletId, Guid UserId, string CurrencyCode, decimal BalanceAvailable,
    decimal BalanceCurrent, string Status, bool IsDefault);

public sealed record WalletOverview(WalletOverviewState State, string Message,
    IReadOnlyList<WalletSummary> Wallets);

public interface IWalletOverviewClient
{
    bool IsSupported => true;
    Task<WalletOverview> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads the signed-in person's wallets. It cannot initiate a transfer or payment.</summary>
public sealed class WalletOverviewClient(HttpClient walletApi, IButlerAccountSession account) : IWalletOverviewClient
{
    public async Task<WalletOverview> ReadAsync(CancellationToken cancellationToken = default)
    {
        var authorization = await account.GetAuthorizationAsync(cancellationToken).ConfigureAwait(false);
        if (authorization is null)
        {
            var status = await account.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return status.IsConnected
                ? new(WalletOverviewState.Unavailable, "Your online account could not be verified right now. Check your connection and try again.", [])
                : new(WalletOverviewState.SignInRequired, "Sign in to your TheGeekNetwork online account to view your wallet.", []);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/wallets/user/{authorization.UserId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization.AccessToken);
        try
        {
            using var response = await walletApi.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(WalletOverviewState.SignInRequired, "Your online account needs to be verified again before wallet details can be shown.", []);
            if (!response.IsSuccessStatusCode)
                return new(WalletOverviewState.Unavailable, "Wallet details are unavailable right now. Try again later.", []);

            var wallets = await response.Content.ReadFromJsonAsync<List<WalletSummary>>(cancellationToken).ConfigureAwait(false);
            if (wallets is null)
                return new(WalletOverviewState.Unavailable, "Wallet details could not be read. Try again later.", []);
            if (!Guid.TryParse(authorization.UserId, out var ownerId)
                || wallets.Any(wallet => wallet.WalletId == Guid.Empty || wallet.UserId != ownerId))
                return new(WalletOverviewState.Unavailable, "Wallet ownership could not be verified. No balances were shown.", []);
            return wallets.Count == 0
                ? new(WalletOverviewState.NoWallet, "No wallet is linked to this online account yet.", [])
                : new(WalletOverviewState.Ready, "Wallet details from TheGeekNetwork.", wallets);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new(WalletOverviewState.Unavailable, "Wallet details could not be reached. Check your connection and try again.", []);
        }
    }
}

public sealed class WalletOverviewUnavailable : IWalletOverviewClient
{
    public bool IsSupported => false;
    public Task<WalletOverview> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WalletOverview(WalletOverviewState.Unavailable,
            "Wallet access is not configured for this Concierge head.", []));
}
