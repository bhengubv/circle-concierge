using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Concierge.Shared.Chat;
using Concierge.Shared.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concierge.Ai;

/// <summary>The Windows-only, text conversation contract at 127.0.0.1:8088.</summary>
public sealed class CircleAiOmniLoopbackRuntime : IChatRuntime, IChatRuntimeCapabilitySource,
    IServiceOwnedAiRuntime,
    IChatRuntimeStatusEvents, ICircleAiSettingsGateway, IDisposable
{
    private static readonly Uri ServiceUri = new("http://127.0.0.1:8088/");
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    internal TimeSpan StartingRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    private string _status = "Checking Circle AI Service…";
    private string? _model;
    private ChatRuntimeCapabilityState _state = ChatRuntimeCapabilityState.Preparing;
    private DateTimeOffset _observed;

    public CircleAiOmniLoopbackRuntime() : this(new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
    })) { }
    internal CircleAiOmniLoopbackRuntime(HttpClient http)
    {
        _http = http;
        _http.BaseAddress = ServiceUri;
        _http.Timeout = TimeSpan.FromMinutes(12);
    }

    public string Id => "circleai";
    public string EngineLabel => "Circle AI Service";
    public bool LeavesDevice => false;
    public bool ServiceOwnsAi => true;
    public bool IsReady { get; private set; }
    public string StatusMessage => _status;
    public event EventHandler? StatusChanged;
    public ModelDownloadProgress? CurrentDownloadProgress => null;
    public ChatRuntimeCapabilityCatalog CapabilityCatalog => new(1, Id, "omni-loopback-v1", _observed,
        [new("conversation", "Conversation", _state, StatusMessage)], null);

    private void SetStatus(string status, ChatRuntimeCapabilityState state, bool ready, string? model = null)
    {
        var changed = _status != status || _state != state || IsReady != ready || _model != model;
        _status = status;
        _state = state;
        IsReady = ready;
        _model = model;
        _observed = DateTimeOffset.UtcNow;
        if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _requestGate.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("status", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
            var root = body?.RootElement ?? throw new IOException("Circle AI Service returned no status.");
            var engine = root.GetProperty("engine").GetString();
            var model = root.TryGetProperty("model", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (engine == "up") SetStatus("Circle AI Service is ready.", ChatRuntimeCapabilityState.Available, true, model);
            else SetStatus("Circle AI Service is starting…", ChatRuntimeCapabilityState.Preparing, false, model);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            SetStatus("Circle AI Service is starting…", ChatRuntimeCapabilityState.Preparing, false);
        }
        catch (HttpRequestException error) when (IsConnectionRefused(error))
        {
            SetStatus("Circle AI Service is not running", ChatRuntimeCapabilityState.Unavailable, false);
        }
        catch (HttpRequestException error)
        {
            SetStatus("Circle AI Service status failed: " + error.Message,
                ChatRuntimeCapabilityState.Unavailable, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            SetStatus("Circle AI Service status failed: " + error.Message,
                ChatRuntimeCapabilityState.Unavailable, false);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_state == ChatRuntimeCapabilityState.Unavailable)
                throw new ChatRuntimeUnavailableException(StatusMessage);
            var turns = messages.Where(message => message.Role is "user" or "assistant")
                .Select(message => new { role = message.Role, content = message.Content }).ToArray();
            if (turns.Length == 0 || turns[^1].role != "user")
                throw new ArgumentException("Circle AI needs a user question.", nameof(messages));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsReady)
                {
                    await Task.Delay(StartingRetryDelay, cancellationToken).ConfigureAwait(false);
                    await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                    if (_state == ChatRuntimeCapabilityState.Unavailable)
                        throw new ChatRuntimeUnavailableException(StatusMessage);
                    continue;
                }
                SetStatus("Circle AI is thinking…", ChatRuntimeCapabilityState.Available, true, _model);
                // The installed service currently reads text even when conversation is supplied.
                // Carry both so the history is retained and this build can answer the question.
                var json = JsonSerializer.Serialize(new { text = turns[^1].content, conversation = turns });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try { response = await _http.PostAsync("ask", content, cancellationToken).ConfigureAwait(false); }
                catch (HttpRequestException error) when (IsConnectionRefused(error))
                {
                    SetStatus("Circle AI Service is not running", ChatRuntimeCapabilityState.Unavailable, false);
                    throw new ChatRuntimeUnavailableException(StatusMessage);
                }
                catch (HttpRequestException error)
                {
                    SetStatus("Circle AI Service request failed: " + error.Message,
                        ChatRuntimeCapabilityState.Unavailable, false);
                    throw new IOException(StatusMessage, error);
                }
                using (response)
                {
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    SetStatus("Circle AI Service is starting…", ChatRuntimeCapabilityState.Preparing, false, _model);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                    SetStatus("Circle AI Service failed: " + error, ChatRuntimeCapabilityState.Unavailable, false);
                    throw new IOException(StatusMessage);
                }
                using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
                    .ConfigureAwait(false);
                if (body?.RootElement.TryGetProperty("answer", out var answer) != true
                    || answer.ValueKind != JsonValueKind.String)
                    throw new IOException("Circle AI Service returned no answer.");
                SetStatus("Circle AI Service is ready.", ChatRuntimeCapabilityState.Available, true, _model);
                yield return answer.GetString()!;
                yield break;
                }
            }
        }
        finally { _requestGate.Release(); }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
            if (body?.RootElement.TryGetProperty("error", out var error) == true)
                return error.GetString() ?? response.ReasonPhrase ?? "Unknown error";
        }
        catch (JsonException) { }
        return response.ReasonPhrase ?? "Unknown error";
    }

    private static bool IsConnectionRefused(HttpRequestException error)
        => error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
           || error.Message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase);

    public async Task<CircleAiSettingsReport> ReadAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var facts = new List<CircleAiSettingFact> { new("Engine", IsReady ? "Ready" : StatusMessage) };
        if (_model is not null) facts.Add(new("Model", Path.GetFileName(_model)));
        return new(StatusMessage,
            [new("readiness", "Circle AI Service", "The service controls its own model and personal data sources.", facts)],
            false, false);
    }

    public Task OpenCircleAiAsync() => Task.FromException(new NotSupportedException("Circle AI Service has no app-opening endpoint."));
    public Task OpenPermissionsAsync() => Task.FromException(new NotSupportedException("Circle AI Service has no permissions endpoint."));
    public void Dispose() { _http.Dispose(); _requestGate.Dispose(); }
}

public sealed class CircleAiOmniLoopbackLoader(CircleAiOmniLoopbackRuntime runtime) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _polling;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _polling = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await runtime.RefreshAsync(_stop.Token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        if (_polling is not null) await _polling.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose() => _stop.Dispose();
}

public static class CircleAiOmniLoopbackRegistration
{
    public static IServiceCollection AddConciergeAiOmniLoopback(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IChatRuntime)
            || descriptor.ServiceType == typeof(IModelDownloadCoordinator)
            || descriptor.ImplementationType?.Name == "CircleAiChatRuntimeLoader").ToArray())
            services.Remove(descriptor);
        services.AddSingleton<IModelDownloadCoordinator, ServiceOwnedModelDownloads>();
        services.AddSingleton<CircleAiOmniLoopbackRuntime>();
        services.AddSingleton<IChatRuntime>(provider => provider.GetRequiredService<CircleAiOmniLoopbackRuntime>());
        services.AddSingleton<ICircleAiSettingsGateway>(provider => provider.GetRequiredService<CircleAiOmniLoopbackRuntime>());
        services.AddHostedService<CircleAiOmniLoopbackLoader>();
        return services;
    }
}

internal sealed class ServiceOwnedModelDownloads : IModelDownloadCoordinator
{
    public Task<bool> StartAcceptedDownloadAsync(IModelDownloadRequired runtime,
        IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task StopAsync(IModelDownloadRequired runtime) => Task.CompletedTask;
}
