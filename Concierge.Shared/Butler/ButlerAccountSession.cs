using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Concierge.Shared.Butler;

/// <summary>Tokens received from AuthAPI. Implementations must store these in an OS-backed secure vault.</summary>
public sealed record ButlerSessionTokens(
    string UserId,
    string Email,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    Guid RefreshTokenId);

public interface IButlerSessionTokenStore
{
    Task<ButlerSessionTokens?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ButlerSessionTokens tokens, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record ButlerAccountStatus(bool IsSupported, bool IsConnected, string? Email, bool NeedsTwoFactor);

public enum ButlerSignInState
{
    Connected,
    NeedsTwoFactor,
    Failed,
}

public sealed record ButlerSignInResult(ButlerSignInState State, string Message);

public interface IButlerAccountSession
{
    Task<ButlerAccountStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ButlerSignInResult> SignInAsync(string emailOrMobile, string password, CancellationToken cancellationToken = default);
    Task<ButlerSignInResult> CompleteTwoFactorAsync(string code, CancellationToken cancellationToken = default);
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Authenticates a person with the existing TGN AuthAPI and keeps its rotating tokens in a
/// host-provided secure store. The access token is only exposed to the Butler API client.
/// </summary>
public sealed class ButlerAccountSession(HttpClient authApi, IButlerSessionTokenStore tokenStore) : IButlerAccountSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _twoFactorToken;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ButlerAccountStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new ButlerAccountStatus(true, tokens is not null, tokens?.Email, !string.IsNullOrWhiteSpace(_twoFactorToken));
    }

    public async Task<ButlerSignInResult> SignInAsync(
        string emailOrMobile, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailOrMobile) || string.IsNullOrWhiteSpace(password))
            return new ButlerSignInResult(ButlerSignInState.Failed, "Enter your email or mobile number and password.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _twoFactorToken = null;
            using var response = await authApi.PostAsJsonAsync("api/auth/login",
                new { emailOrMobile = emailOrMobile.Trim(), password }, JsonOptions, cancellationToken).ConfigureAwait(false);
            using var document = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && TryGet(document.RootElement, "twoFactorRequired", out var twoFactor)
                && twoFactor.ValueKind == JsonValueKind.True)
            {
                _twoFactorToken = GetString(GetProperty(GetProperty(document.RootElement, "data"), "twoFactorToken"));
                if (!string.IsNullOrWhiteSpace(_twoFactorToken))
                    return new ButlerSignInResult(ButlerSignInState.NeedsTwoFactor, "Enter the verification code from your authenticator.");
            }

            if (!response.IsSuccessStatusCode)
                return new ButlerSignInResult(ButlerSignInState.Failed, GetMessage(document.RootElement, "Sign in failed. Check your details and try again."));

            return await StoreLoginAsync(document.RootElement, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new ButlerSignInResult(ButlerSignInState.Failed, "AuthAPI could not be reached. Check your connection and try again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ButlerSignInResult> CompleteTwoFactorAsync(
        string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new ButlerSignInResult(ButlerSignInState.Failed, "Enter the verification code.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(_twoFactorToken))
                return new ButlerSignInResult(ButlerSignInState.Failed, "Your sign-in step expired. Start again.");

            using var response = await authApi.PostAsJsonAsync("api/2fa/verify",
                new { code = code.Trim(), twoFactorToken = _twoFactorToken, trustDevice = false }, JsonOptions, cancellationToken).ConfigureAwait(false);
            using var document = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ButlerSignInResult(ButlerSignInState.Failed, GetMessage(document.RootElement, "That code was not accepted. Try again."));

            _twoFactorToken = null;
            return await StoreLoginAsync(document.RootElement, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new ButlerSignInResult(ButlerSignInState.Failed, "AuthAPI could not be reached. Check your connection and try again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = await tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (tokens is null)
                return null;
            if (tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return tokens.AccessToken;

            using var response = await authApi.PostAsJsonAsync("api/auth/refresh",
                new { tokenId = tokens.RefreshTokenId, token = tokens.RefreshToken }, JsonOptions, cancellationToken).ConfigureAwait(false);
            using var document = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await tokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var data = GetProperty(document.RootElement, "data");
            var accessToken = GetString(GetProperty(data, "accessToken"));
            var refreshToken = GetString(GetProperty(data, "refreshToken"));
            var refreshTokenIdText = GetString(GetProperty(data, "refreshTokenId"));
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken)
                || !Guid.TryParse(refreshTokenIdText, out var refreshTokenId)
                || !TryReadJwtExpiry(accessToken, out var expiresAt))
            {
                await tokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var refreshed = tokens with
            {
                AccessToken = accessToken,
                AccessTokenExpiresAt = expiresAt,
                RefreshToken = refreshToken,
                RefreshTokenId = refreshTokenId,
            };
            await tokenStore.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        catch (HttpRequestException)
        {
            // Keep the refresh token for a later online retry; don't turn a brief outage into sign-out.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _twoFactorToken = null;
            var tokens = await tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (tokens is not null)
            {
                try
                {
                    using var response = await authApi.PostAsJsonAsync("api/auth/revoke",
                        new { tokenId = tokens.RefreshTokenId }, JsonOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) { }
            }
            await tokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ButlerSignInResult> StoreLoginAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var data = GetProperty(root, "data");
        var userId = GetString(GetProperty(data, "userId"));
        var email = GetString(GetProperty(data, "email"));
        var accessToken = GetString(GetProperty(data, "accessToken"));
        var refreshToken = GetString(GetProperty(data, "refreshToken"));
        var refreshTokenIdText = GetString(GetProperty(data, "refreshTokenId"));
        var expiresAtElement = GetProperty(data, "expiresAt");
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(accessToken)
            || string.IsNullOrWhiteSpace(refreshToken) || !Guid.TryParse(refreshTokenIdText, out var refreshTokenId)
            || expiresAtElement.ValueKind != JsonValueKind.String
            || !expiresAtElement.TryGetDateTimeOffset(out var expiresAt))
        {
            return new ButlerSignInResult(ButlerSignInState.Failed, "AuthAPI returned an incomplete sign-in response.");
        }

        await tokenStore.SaveAsync(new ButlerSessionTokens(userId, email ?? string.Empty, accessToken,
            expiresAt, refreshToken, refreshTokenId), cancellationToken).ConfigureAwait(false);
        return new ButlerSignInResult(ButlerSignInState.Connected, "Butler account connected.");
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static string GetMessage(JsonElement root, string fallback)
        => GetString(GetProperty(root, "message")) is { Length: > 0 } message ? message : fallback;

    private static JsonElement GetProperty(JsonElement value, string name)
        => TryGet(value, name, out var property) ? property : default;

    private static bool TryGet(JsonElement value, string name, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject())
            {
                if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    property = item.Value;
                    return true;
                }
            }
        }
        property = default;
        return false;
    }

    private static string? GetString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryReadJwtExpiry(string jwt, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return false;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return TryGet(document.RootElement, "exp", out var exp)
                && exp.TryGetInt64(out var unixSeconds)
                && TryFromUnixTime(unixSeconds, out expiresAt);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryFromUnixTime(long seconds, out DateTimeOffset result)
    {
        try
        {
            result = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }
}

/// <summary>Used where this host has no OS-backed credential vault or account-link surface.</summary>
public sealed class ButlerAccountUnavailable : IButlerAccountSession
{
    public Task<ButlerAccountStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ButlerAccountStatus(false, false, null, false));
    public Task<ButlerSignInResult> SignInAsync(string emailOrMobile, string password, CancellationToken cancellationToken = default)
        => Task.FromResult(new ButlerSignInResult(ButlerSignInState.Failed, "Butler account linking is not available on this host yet."));
    public Task<ButlerSignInResult> CompleteTwoFactorAsync(string code, CancellationToken cancellationToken = default)
        => Task.FromResult(new ButlerSignInResult(ButlerSignInState.Failed, "Butler account linking is not available on this host yet."));
    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
