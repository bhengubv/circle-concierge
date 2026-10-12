using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Concierge.Shared.Butler;

public sealed record ButlerWorkCapability(string Id, string DisplayName, string Description, string Risk);
public sealed record ButlerWorkCapabilities(string Version, IReadOnlyList<ButlerWorkCapability> Capabilities);
public sealed record ButlerWorkRunRequest(
    Guid ConciergeRunId,
    string IdempotencyKey,
    Guid? WorkId,
    Guid? ProjectId,
    Guid? WorkspaceId,
    Guid? ConversationId,
    string OperationId,
    string ActionMode,
    IReadOnlyList<string> ApprovalReferences,
    JsonElement Arguments);
public sealed record ButlerWorkRunAccepted(Guid ButlerRunId, Guid ConciergeRunId, string Status, string StatusUrl, string EventsUrl);
public sealed record ButlerWorkRun(
    Guid Id,
    string UserId,
    Guid ConciergeRunId,
    string IdempotencyKey,
    Guid? WorkId,
    Guid? ProjectId,
    Guid? WorkspaceId,
    Guid? ConversationId,
    string OperationId,
    string ActionMode,
    string Status,
    JsonElement Request,
    JsonElement? Result,
    string? Problem,
    bool CancellationRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record ButlerWorkRunEvent(long Sequence, string Type, JsonElement Data, DateTimeOffset CreatedAt);
public sealed record ButlerWorkRunEnvelope(ButlerWorkRun Run, IReadOnlyList<ButlerWorkRunEvent> Events);

public interface IButlerWorkRunApi
{
    Task<ButlerWorkCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<ButlerWorkRunAccepted> SubmitAsync(ButlerWorkRunRequest request, CancellationToken cancellationToken = default);
    Task<ButlerWorkRunEnvelope?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<ButlerWorkRunEnvelope> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ButlerWorkRunEvent>> ReadEventsAsync(Guid runId, long afterSequence, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>Authenticated HTTP adapter for Butler's durable, owner-scoped work-run API.</summary>
public sealed class ButlerWorkRunApiClient(HttpClient http, IButlerAccountSession account) : IButlerWorkRunApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ButlerWorkCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, "api/butler/work-capabilities", cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ButlerWorkCapabilities>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Butler returned an empty capability catalogue.");
    }

    public async Task<ButlerWorkRunAccepted> SubmitAsync(ButlerWorkRunRequest payload, CancellationToken cancellationToken = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Post, "api/butler/work-runs", cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ButlerWorkRunAccepted>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Butler accepted a run without returning its identity.");
    }

    public async Task<ButlerWorkRunEnvelope?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get,
            $"api/butler/work-runs/by-idempotency?key={Uri.EscapeDataString(key)}", cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ButlerWorkRunEnvelope>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Butler returned an empty idempotency lookup.");
    }

    public async Task<ButlerWorkRunEnvelope> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, $"api/butler/work-runs/{runId:D}", cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ButlerWorkRunEnvelope>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Butler returned an empty run status.");
    }

    public async Task<IReadOnlyList<ButlerWorkRunEvent>> ReadEventsAsync(
        Guid runId, long afterSequence, CancellationToken cancellationToken = default)
    {
        if (afterSequence < -1)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        using var request = await AuthorizedAsync(HttpMethod.Get,
            $"api/butler/work-runs/{runId:D}/events?afterSequence={afterSequence}", cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ButlerWorkRunEvent>>(Json, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        using var request = await AuthorizedAsync(HttpMethod.Post, $"api/butler/work-runs/{runId:D}/cancel", cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var token = await account.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new ButlerAccountRequiredException();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? message = null;
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("message", out var detail)
                && detail.ValueKind == JsonValueKind.String)
                message = detail.GetString();
        }
        catch (JsonException) { }
        throw new ButlerWorkApiException(response.StatusCode, message ?? $"Butler returned HTTP {(int)response.StatusCode}.");
    }
}

public sealed class ButlerAccountRequiredException()
    : InvalidOperationException("Connect your Butler account in Settings before asking Concierge to run Butler work.");

public sealed class ButlerWorkApiException(System.Net.HttpStatusCode statusCode, string message) : HttpRequestException(message, null, statusCode)
{ }

/// <summary>Fails closed when a host has not configured Butler account and API services.</summary>
public sealed class ButlerWorkRunApiUnavailable : IButlerWorkRunApi
{
    private static ButlerAccountRequiredException Missing() => new();
    public Task<ButlerWorkCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromException<ButlerWorkCapabilities>(Missing());
    public Task<ButlerWorkRunAccepted> SubmitAsync(ButlerWorkRunRequest request, CancellationToken cancellationToken = default) => Task.FromException<ButlerWorkRunAccepted>(Missing());
    public Task<ButlerWorkRunEnvelope?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromException<ButlerWorkRunEnvelope?>(Missing());
    public Task<ButlerWorkRunEnvelope> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromException<ButlerWorkRunEnvelope>(Missing());
    public Task<IReadOnlyList<ButlerWorkRunEvent>> ReadEventsAsync(Guid runId, long afterSequence, CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<ButlerWorkRunEvent>>(Missing());
    public Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromException<bool>(Missing());
}
