using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Concierge.Shared.Identity;

/// <summary>
/// Verifies a phone's existing TGN account bearer with AuthAPI. A local device
/// unlock, a QR scan, or an unverified JWT payload cannot establish this identity.
/// This uses the shared IAuthApiClient current-user HTTP contract: GET /api/auth/me.
/// </summary>
public sealed class TgnCurrentUserVerifier(HttpClient authApi)
{
    public async Task<Guid?> VerifyAsync(string? bearerToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        try
        {
            using var response = await authApi.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
            if (body is null || !body.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("userId", out var userId)
                || !userId.TryGetGuid(out var ownerId) || ownerId == Guid.Empty)
                return null;
            return ownerId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
