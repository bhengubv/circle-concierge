using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CircleAI.Core;
using Concierge.Shared.Chat;
using Concierge.Shared.Settings;
using Concierge.Shared.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concierge.Ai;

/// <summary>HTTP client only. Circle AI's Windows service owns native inference and weights.</summary>
public sealed class CircleAiWindowsServiceRuntime : IChatRuntime, IChatRuntimeCapabilitySource, IChatRuntimeStatusEvents, ICircleAiSettingsGateway, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _connectionPath;
    private readonly CircleAiServicePreferences? _preferences;
    public CircleAiWindowsServiceRuntime(CircleAiServicePreferences preferences) : this(null, null, preferences) { }
    internal CircleAiWindowsServiceRuntime(string? connectionPath = null, HttpClient? http = null,
        CircleAiServicePreferences? preferences = null)
    {
        _preferences = preferences;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _connectionPath = connectionPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Concierge", "CircleAIService", "connection.json");
    }
    private readonly SemaphoreSlim _inference = new(1, 1);
    private string? _model;
    private string _version = "unverified";
    private sealed record ServiceConnection(Uri BaseUrl, string Header, string Credential, bool Loopback);
    private ServiceConnection? _connection;
    private Uri Endpoint(string path, ServiceConnection? connection) => new(connection?.BaseUrl ?? _http.BaseAddress
        ?? throw new InvalidOperationException("Circle AI is not connected."), path);
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ServiceConnection? connection,
        CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        if (connection is not null) request.Headers.TryAddWithoutValidation(connection.Header, connection.Credential);
        return await _http.SendAsync(request, completion, cancellationToken);
    }
    private async Task<JsonDocument?> GetJsonAsync(string path, ServiceConnection? connection, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(path, connection));
        using var response = await SendAsync(request, connection, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
    }
    private ChatRuntimeCapabilityState _state = ChatRuntimeCapabilityState.Preparing;
    public string Id => "circleai";
    public string EngineLabel => _model is null ? "Circle AI service" : $"{_model} (Circle AI service)";
    public bool LeavesDevice => false;
    public bool IsReady { get; private set; }
    public CircleAiPromptDiagnostics? LastPromptDiagnostics { get; private set; }
    internal static CircleAiPromptDiagnostics MeasurePrompt(IReadOnlyList<ChatTurn> messages, string? model)
    {
        var meter = new Concierge.Shared.Context.HeuristicTokenMeter();
        return new(DateTimeOffset.UtcNow, model, messages.Count,
            messages.Sum(m => m.Content?.Length ?? 0),
            meter.Measure(messages, 4096).Tokens,
            messages.Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Sum(m => meter.EstimateTokens(m.Content) + 4));
    }
    private string _status = "Connecting to Circle AI service…";
    public event EventHandler? StatusChanged;
    public ModelDownloadProgress? CurrentDownloadProgress => null;
    public string StatusMessage
    {
        get => _status;
        private set { if (_status == value) return; _status = value; StatusChanged?.Invoke(this, EventArgs.Empty); }
    }
    public ChatRuntimeCapabilityCatalog CapabilityCatalog => new(1, Id, _version, DateTimeOffset.UtcNow,
        [new("conversation", "Conversation", IsReady ? ChatRuntimeCapabilityState.Available : _state, StatusMessage)], null);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // A connection handoff must not change the endpoint or auth header while
        // an answer is in flight. StreamAsync uses this same gate.
        await _inference.WaitAsync(cancellationToken);
        try { await InitializeCoreAsync(cancellationToken); }
        finally { _inference.Release(); }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (!IsReady)
        {
            _state = ChatRuntimeCapabilityState.Preparing;
            StatusMessage = "Connecting to Circle AI service…";
        }
        try
        {
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(_connectionPath, cancellationToken));
            var baseUrl = new Uri(config.RootElement.GetProperty("BaseUrl").GetString()!);
            if (!baseUrl.IsLoopback || baseUrl.Scheme != "http")
                throw new InvalidOperationException("The installed service must use a local endpoint.");
            var loopback = config.RootElement.TryGetProperty("Token", out var token);
            var candidate = new ServiceConnection(baseUrl, loopback ? "X-Butler-Token" : "X-CircleAI-Api-Key",
                (loopback ? token.GetString() : config.RootElement.GetProperty("ApiKey").GetString())!, loopback);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            if (loopback)
            {
                using var report = await GetJsonAsync("/v1/status", candidate, timeout.Token);
                var root = report!.RootElement;
                var brain = root.GetProperty("brain");
                _model = brain.TryGetProperty("modelId", out var model) ? model.GetString() : null;
                // A lazy installed model accepts a question and loads on demand.
                // Rejecting canAnswerSoon strands the very first conversation.
                var canAnswerNow = root.GetProperty("canAnswerNow").GetBoolean();
                var canAnswerSoon = root.TryGetProperty("canAnswerSoon", out var soon)
                    && soon.ValueKind == JsonValueKind.True;
                IsReady = canAnswerNow || canAnswerSoon;
                _state = IsReady ? ChatRuntimeCapabilityState.Available : ChatRuntimeCapabilityState.Preparing;
                // The readiness schema does not carry a binary version. Do not
                // present the client package's version as a server measurement.
                _version = "loopback schema " + root.GetProperty("schema").GetInt32();
                StatusMessage = canAnswerSoon && !canAnswerNow
                    ? "Circle AI will load its selected model when you ask."
                    : brain.GetProperty("detail").GetString() ?? "Circle AI is preparing assistance.";
                _connection = candidate;
                return;
            }
            using var diagnosticRequest = new HttpRequestMessage(HttpMethod.Get, Endpoint("/v1/diagnostics", candidate));
            using var diagnosticResponse = await SendAsync(diagnosticRequest, candidate, timeout.Token);
            if (diagnosticResponse.IsSuccessStatusCode)
            {
                using var diagnostics = await diagnosticResponse.Content.ReadFromJsonAsync<JsonDocument>(timeout.Token);
                if (diagnostics?.RootElement.TryGetProperty("server_version", out var version) == true)
                    _version = version.GetString() ?? "unverified";
            }
            using var models = await GetJsonAsync("/v1/models", candidate, timeout.Token);
            _model = SingleLoadedStreamingModel(models);
            IsReady = _model is not null;
            _state = IsReady ? ChatRuntimeCapabilityState.Available : ChatRuntimeCapabilityState.Preparing;
            StatusMessage = IsReady ? "Circle AI service is ready."
                : "Circle AI service has not reported a ready model yet. The service manages model preparation.";
            _connection = candidate;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            IsReady = false;
            _state = ChatRuntimeCapabilityState.Unavailable;
            StatusMessage = "Circle AI service could not prepare assistance: " + exception.Message;
        }
    }

    private async Task RefreshChangedConnectionAsync(CancellationToken cancellationToken)
    {
        // An injected HttpClient.BaseAddress is the legacy gateway contract;
        // there is no protected loopback connection file to rotate.
        if (_connection is null && _http.BaseAddress is not null && IsReady) return;
        if (_connection is null || !IsReady)
        {
            await InitializeCoreAsync(cancellationToken);
            return;
        }
        try
        {
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(_connectionPath, cancellationToken));
            var root = config.RootElement;
            var baseUrl = new Uri(root.GetProperty("BaseUrl").GetString()!);
            var credential = root.TryGetProperty("Token", out var token)
                ? token.GetString() : root.GetProperty("ApiKey").GetString();
            if (baseUrl != _connection.BaseUrl || credential != _connection.Credential)
                await InitializeCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // InitializeCoreAsync reports the actionable connection failure in
            // StatusMessage. Never keep sending to a stale endpoint after the
            // protected handoff file becomes unreadable or invalid.
            await InitializeCoreAsync(cancellationToken);
        }
    }

    internal static string? SingleLoadedStreamingModel(JsonDocument? models)
    {
        if (models?.RootElement.TryGetProperty("data", out var data) != true
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1) return null;
        var entry = data[0];
        if (!entry.TryGetProperty("supports_streaming", out var streaming) || streaming.ValueKind != JsonValueKind.True
            || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
        return string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString();
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The installed service admits one inference request at a time. All
        // Desktop and linked-device turns share this client and wait their turn.
        await _inference.WaitAsync(cancellationToken);
        try
        {
        await RefreshChangedConnectionAsync(cancellationToken);
        if (!IsReady) throw new InvalidOperationException(StatusMessage);
        var connection = _connection;
        var loopback = connection?.Loopback ?? false;
        LastPromptDiagnostics = MeasurePrompt(messages, _model);
        // Snapshot preferences once: changing Settings does not reroute an active answer.
        var options = _preferences?.Current ?? new CircleAiServiceOptions();
        if (loopback)
            StatusMessage = options.AgenticWork
                ? "Circle AI is answering. Service agentic work is not connected yet."
                : "Circle AI is answering.";
        else if (!options.StreamAnswers && options.AgenticWork)
        {
            // Companion owns its session history. Never silently replace the existing
            // Concierge conversation or send its system instructions as a user message.
            // 3.8.1 exposes no history/approval import contract, so this cannot safely
            // take over Concierge runs yet, even if its offline backend is connected.
            StatusMessage = "Circle AI is answering conversationally. Service agentic work is not connected yet.";
        }
        else StatusMessage = options.AgenticWork
            ? "Circle AI is streaming conversationally. Service 3.8.1 does not combine streaming with agentic work."
            : "Circle AI service is ready for conversation.";
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(loopback
            ? options.StreamAnswers ? "/butler/stream" : "/butler/chat" : "/v1/chat/completions", connection))
        {
            // Concierge's action/approval responses should not depend on a
            // randomly sampled completion. The service still owns inference.
            Content = loopback
                ? JsonContent.Create(new { options = new { temperature = 0 },
                    messages = messages.Select(message => new { role = message.Role, content = message.Content }) })
                : JsonContent.Create(new { model = _model, stream = options.StreamAnswers, temperature = 0,
                    messages = messages.Select(message => new { role = message.Role, content = message.Content }) })
        };
        using var response = await SendAsync(request, connection, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (!options.StreamAnswers)
        {
            using var answer = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            if (loopback)
            {
                yield return answer!.RootElement.GetProperty("content").GetString()!;
                yield break;
            }
            if (answer?.RootElement.TryGetProperty("choices", out var completeChoices) != true)
                throw new IOException("Circle AI service returned no completed answer.");
            foreach (var choice in completeChoices.EnumerateArray())
                if (choice.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    yield return content.GetString()!;
            yield break;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var doneEvent = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (loopback && line.StartsWith("event:", StringComparison.Ordinal))
            {
                doneEvent = line[6..].Trim() == "done";
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (loopback)
            {
                if (doneEvent) yield break;
                using var piece = JsonDocument.Parse(payload);
                if (piece.RootElement.ValueKind != JsonValueKind.String)
                    throw new IOException("Circle AI returned an invalid stream event.");
                yield return piece.RootElement.GetString()!;
                continue;
            }
            if (payload == "[DONE]") yield break;
            using var chunk = JsonDocument.Parse(payload);
            if (chunk.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.ToString());
            if (chunk.RootElement.TryGetProperty("choices", out var choices))
                foreach (var choice in choices.EnumerateArray())
                    if (choice.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        yield return content.GetString()!;
        }
        throw new IOException("Circle AI service disconnected before confirming the answer was complete.");
        }
        finally { _inference.Release(); }
    }
    public async Task<CircleAiSettingsReport> ReadAsync(CancellationToken cancellationToken)
    {
        var connection = _connection;
        var loopback = connection?.Loopback ?? false;
        var readiness = new List<CircleAiSettingFact>();
        if (connection is null && _http.BaseAddress is null) readiness.Add(new("Connection", "Not connected", StatusMessage));
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var models = await GetJsonAsync(loopback ? "/v1/status" : "/v1/models", connection, timeout.Token);
                var model = loopback ? models!.RootElement.GetProperty("brain").GetProperty("modelId").GetString()
                    : SingleLoadedStreamingModel(models);
                readiness.Add(new("Service connection", "Responding"));
                readiness.Add(new("Service version", _version));
                var canAnswer = !loopback || models!.RootElement.GetProperty("canAnswerNow").GetBoolean()
                    || (models.RootElement.TryGetProperty("canAnswerSoon", out var soon) && soon.ValueKind == JsonValueKind.True);
                readiness.Add(new("Conversation model", model is not null && canAnswer ? model : "Not reported ready",
                    "Reported by the service; this check does not run a conversation or select a model."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { readiness.Add(new("Service connection", "Unavailable", "The local service did not return a current model report.")); }
        }
        var sections = new CircleAiSettingsSection[]
        {
            new("language", "Language and replies", "Input and fixed reply language controls are not exposed by the installed Windows service contract.", []),
            new("voice", "Voice and wake", "Wake phrase and background listening controls are not exposed by this Windows service. Concierge does not start its own listener.", []),
            new("readiness", "Readiness and capabilities", "Current local service report. A ready conversation model does not establish readiness of voice, images, or agentic work.", readiness),
            new("permissions", "Permissions and setup", "The Windows service uses its installed local connection credentials. Microphone and notification setup must be supplied by the service before Concierge can manage them.", []),
            new("storage", "Storage and housekeeping", "Models, retention, cache limits, and cleanup belong to Circle AI. The installed Windows API does not expose these settings; Concierge does not delete or rank its models.", []),
            new("borrowing", "Trusted-device assistance", "The installed Windows service does not expose borrowing preferences. Concierge's device connection is separate from Circle AI borrowing.", [])
        };
        return new("Checked the local Windows service. Settings not exposed by its contract are labelled explicitly.", sections, false, false);
    }
    public Task OpenCircleAiAsync() => Task.FromException(new NotSupportedException("Windows service controls are not exposed."));
    public Task OpenPermissionsAsync() => Task.FromException(new NotSupportedException("Windows service permission controls are not exposed."));
    public void Dispose() => _http.Dispose();
}

/// <summary>Payload size only; never retains conversation content or credentials. Token counts are heuristic estimates.</summary>
public sealed record CircleAiPromptDiagnostics(DateTimeOffset ObservedAt, string? Model,
    int MessageCount, int Characters, int EstimatedTokens, int EstimatedSystemTokens);

public sealed class CircleAiWindowsServiceLoader(CircleAiWindowsServiceRuntime runtime) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loading;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loading = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await runtime.InitializeAsync(_stop.Token);
                    // Resource pressure and a late service start can resolve without
                    // restarting the UI. Retry is sequential; it never starts downloads.
                    await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        if (_loading is not null) await _loading.WaitAsync(cancellationToken);
    }
    public void Dispose() => _stop.Dispose();
}

public static class CircleAiWindowsServiceRegistration
{
    public static IServiceCollection AddConciergeAiWindowsService(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IChatRuntime)
            || descriptor.ImplementationType?.Name == "CircleAiChatRuntimeLoader"
            || descriptor.ServiceType == typeof(CircleAiChatRuntime)).ToArray())
            services.Remove(descriptor);
        services.AddSingleton<CircleAiWindowsServiceRuntime>();
        services.AddSingleton(_ => new CircleAiServicePreferences(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Concierge", "CircleAIService", "concierge-preferences.json")));
        services.AddSingleton<IChatRuntime>(provider => provider.GetRequiredService<CircleAiWindowsServiceRuntime>());
        services.AddSingleton<ICircleAiSettingsGateway>(provider => provider.GetRequiredService<CircleAiWindowsServiceRuntime>());
        services.AddHostedService<CircleAiWindowsServiceLoader>();
        return services;
    }
}
