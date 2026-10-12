using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Concierge.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concierge.Ai;

/// <summary>HTTP client only. Circle AI's Windows service owns native inference and weights.</summary>
public sealed class CircleAiWindowsServiceRuntime : IChatRuntime, IChatRuntimeCapabilitySource, IChatRuntimeStatusEvents, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _inference = new(1, 1);
    private string? _model;
    private string _version = "unverified";
    private ChatRuntimeCapabilityState _state = ChatRuntimeCapabilityState.Preparing;
    public string Id => "circleai";
    public string EngineLabel => _model is null ? "Circle AI service" : $"{_model} (Circle AI service)";
    public bool LeavesDevice => false;
    public bool IsReady { get; private set; }
    private string _status = "Connecting to Circle AI service…";
    public event EventHandler? StatusChanged;
    public ModelDownloadProgress? CurrentDownloadProgress => null;
    public string StatusMessage
    {
        get => _status;
        private set { _status = value; StatusChanged?.Invoke(this, EventArgs.Empty); }
    }
    public ChatRuntimeCapabilityCatalog CapabilityCatalog => new(1, Id, _version, DateTimeOffset.UtcNow,
        [new("conversation", "Conversation", IsReady ? ChatRuntimeCapabilityState.Available : _state, StatusMessage)], null);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsReady) return;
        _state = ChatRuntimeCapabilityState.Preparing;
        StatusMessage = "Connecting to Circle AI service…";
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Concierge", "CircleAIService", "connection.json");
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var baseUrl = new Uri(config.RootElement.GetProperty("BaseUrl").GetString()!);
            if (!baseUrl.IsLoopback || baseUrl.Scheme != "http")
                throw new InvalidOperationException("The installed service must use a local endpoint.");
            if (_http.BaseAddress is null) _http.BaseAddress = baseUrl;
            _http.DefaultRequestHeaders.Remove("X-CircleAI-Api-Key");
            _http.DefaultRequestHeaders.Add("X-CircleAI-Api-Key", config.RootElement.GetProperty("ApiKey").GetString());
            string? serviceBackend = null;
            string? serviceTier = null;
            try
            {
                using var diagnosticTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                diagnosticTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var diagnosticResponse = await _http.GetAsync("/v1/diagnostics", diagnosticTimeout.Token);
                if (diagnosticResponse.IsSuccessStatusCode)
                {
                    using var diagnostics = await diagnosticResponse.Content.ReadFromJsonAsync<JsonDocument>(diagnosticTimeout.Token);
                    if (diagnostics?.RootElement.TryGetProperty("server_version", out var version) == true)
                        _version = version.GetString() ?? "unverified";
                    if (diagnostics?.RootElement.TryGetProperty("backend_selection", out var selection) == true)
                    {
                        serviceBackend = selection.GetProperty("backend").GetString();
                        serviceTier = selection.GetProperty("tier").GetString();
                    }
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or OperationCanceledException or JsonException) { }

            using var residentModels = await _http.GetFromJsonAsync<JsonDocument>("/v1/models", cancellationToken);
            if (SingleLoadedStreamingModel(residentModels) is { } residentModel)
            {
                // The service already owns this allocation. Re-assessing it
                // against remaining free RAM would count its memory twice.
                _model = residentModel;
                IsReady = true;
                StatusMessage = "Circle AI service is ready.";
                return;
            }
            var modelsRoot = config.RootElement.GetProperty("ModelsRoot").GetString()!;
            // The SDK owns the choice. This client never loads a generator or weights.
            DeviceProbe.PlatformMemoryProbe = WindowsAvailableMemoryProbe.Read;
            var probe = DeviceProbe.Snapshot(modelsRoot);
            using var selector = new CircleAiModelSelectorContext(modelsRoot);
            selector.Assess(probe);
            var selected = selector.BestFit(probe, ChatCapability.None);
            using var cache = new BundleModelLoader(modelsRoot);
            if (selected.Quality != SelectionQuality.NothingFits && !cache.ModelPresent(selected.ModelId))
                selected = selector.AllCandidates(probe).FirstOrDefault(candidate =>
                    candidate.Quality != SelectionQuality.NothingFits && cache.ModelPresent(candidate.ModelId)) ?? selected;
            if (selected.Quality == SelectionQuality.NothingFits)
            {
                _state = ChatRuntimeCapabilityState.Preparing;
                StatusMessage = "Circle AI is waiting for enough free memory to prepare assistance. Concierge will retry automatically.";
                return;
            }
            if (!cache.ModelPresent(selected.ModelId))
            {
                _state = ChatRuntimeCapabilityState.ActionRequired;
                StatusMessage = "Circle AI service needs model preparation before it can answer. Downloads have not been started.";
                return;
            }
            _model = selected.ModelId;
            using var models = await _http.GetFromJsonAsync<JsonDocument>("/v1/models", cancellationToken);
            if (!ContainsModel(models, _model))
            {
                if (string.IsNullOrWhiteSpace(serviceBackend) || string.IsNullOrWhiteSpace(serviceTier))
                    throw new InvalidOperationException("The service has not reported its device backend and resource tier yet.");
                StatusMessage = "Circle AI service is preparing the model already on this device…";
                using var response = await _http.PostAsJsonAsync("/v1/admin/models/load", new
                {
                    modelId = _model, backend = serviceBackend, tier = serviceTier,
                    vramRequiredBytes = 0L, ramRequiredBytes = selected.EstimatedBytes
                }, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            using var readyModels = await _http.GetFromJsonAsync<JsonDocument>("/v1/models", cancellationToken);
            IsReady = ContainsModel(readyModels, _model);
            StatusMessage = IsReady ? "Circle AI service is ready." : "Circle AI service has not reported a ready model.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            IsReady = false;
            _state = ChatRuntimeCapabilityState.Unavailable;
            StatusMessage = "Circle AI service could not prepare assistance: " + exception.Message;
        }
    }

    private static bool ContainsModel(JsonDocument? models, string id)
        => models?.RootElement.TryGetProperty("data", out var data) == true
            && data.EnumerateArray().Any(entry => entry.TryGetProperty("id", out var modelId) && modelId.GetString() == id);

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
        if (!IsReady) throw new InvalidOperationException(StatusMessage);
        // The installed service admits one inference request at a time. All
        // Desktop and linked-device turns share this client and wait their turn.
        await _inference.WaitAsync(cancellationToken);
        try
        {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            // Concierge's action/approval responses should not depend on a
            // randomly sampled completion. The service still owns inference.
            Content = JsonContent.Create(new { model = _model, stream = true, temperature = 0,
                messages = messages.Select(message => new { role = message.Role, content = message.Content }) })
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
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
    public void Dispose() => _http.Dispose();
}

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
                    if (runtime.IsReady) return;
                    // Resource pressure and a late service start can resolve without
                    // restarting the UI. Retry is sequential; it never starts downloads.
                    await Task.Delay(TimeSpan.FromMinutes(1), _stop.Token);
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
        services.AddSingleton<IChatRuntime>(provider => provider.GetRequiredService<CircleAiWindowsServiceRuntime>());
        services.AddHostedService<CircleAiWindowsServiceLoader>();
        return services;
    }
}
