using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Tools;
using Concierge.Shared.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdkMcpClient = ModelContextProtocol.Client.McpClient;

namespace Concierge.Shared.Rpc.Mcp;

public sealed record NotaConnectionStatus(bool Connected, string Message, IReadOnlyList<string> ToolNames);
public sealed record NotaAudioDraft(string Path, string Format, long Bytes, bool? Audible = null);

/// <summary>Opt-in link to the running Nota desktop studio's loopback MCP endpoint.</summary>
public sealed class NotaStudio : IAgentToolSource, IAsyncDisposable
{
    private static readonly HashSet<string> CreativeTools = new(StringComparer.Ordinal)
    {
        "get_overview", "get_transport", "set_tempo", "add_audio_track", "add_midi_clip",
        "get_clip_notes", "set_clip_notes", "add_notes", "undo", "redo", "export_master",
        "export_stems", "play", "stop", "list_sections", "set_sections",
    };

    private readonly IToolApprovalService _approval;
    private readonly string _outputRoot;
    private readonly Uri _endpoint;
    private SdkMcpClient? _client;
    private IReadOnlyList<IAgentTool> _tools = [];

    public NotaStudio(IToolApprovalService approval, string outputRoot, Uri? endpoint = null)
    {
        _approval = approval;
        _outputRoot = Path.GetFullPath(outputRoot);
        _endpoint = endpoint ?? new Uri("http://127.0.0.1:3900/");
    }

    public IReadOnlyList<IAgentTool> Tools => _tools;
    public bool IsConnected => _client is not null;
    public string OutputRoot => _outputRoot;

    public async Task<NotaConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_endpoint.Scheme != Uri.UriSchemeHttp || !_endpoint.IsLoopback ||
            _endpoint.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
            return new(false, "Nota must be on this computer; remote audio control is not enabled.", []);

        if (_client is not null)
            return new(true, "Connected to Nota.", _tools.Select(tool => tool.Name).ToArray());

        SdkMcpClient? candidate = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            candidate = await SdkMcpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = _endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(8),
            }), cancellationToken: timeout.Token).ConfigureAwait(false);
            var published = await candidate.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            var names = published.Select(tool => tool.Name).ToArray();
            if (!names.Contains("get_overview") || !names.Contains("export_master"))
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                return new(false, "Nota is connected, but it does not offer song overview and audio export.", names);
            }

            _client = candidate;
            _tools = published.Where(tool => CreativeTools.Contains(tool.Name))
                .Select(tool => (IAgentTool)new ApprovedMcpTool("Nota",
                    new NotaAgentTool(candidate, tool), _approval))
                .ToArray();
            return new(true, "Connected to Nota. Your open song is ready for Concierge.", names);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            return new(false, "Nota did not answer in time. Open Nota and turn on AI control in Settings.", []);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            return new(false, $"Could not connect to Nota: {error.Message}. Open Nota and turn on AI control in Settings.", []);
        }
    }

    public async Task<NotaAudioDraft> ExportAsync(string name, double totalBeats, string format = "mp3",
        CancellationToken cancellationToken = default)
    {
        if (_client is null) throw new InvalidOperationException("Connect to Nota before exporting.");
        if (totalBeats is <= 0 or > 100_000 || !double.IsFinite(totalBeats))
            throw new ArgumentOutOfRangeException(nameof(totalBeats), "Choose a positive song length.");
        if (format is not ("wav" or "flac" or "mp3"))
            throw new ArgumentException("Choose WAV, FLAC, or MP3.", nameof(format));
        var safeName = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Nota draft";
        safeName = safeName[..Math.Min(safeName.Length, 60)];
        Directory.CreateDirectory(_outputRoot);
        var path = Path.Combine(_outputRoot, $"{safeName}-{Guid.NewGuid():N}.{format}");
        try
        {
            var result = await _client.CallToolAsync("export_master", new Dictionary<string, object?>
            {
                ["path"] = path, ["totalBeats"] = totalBeats, ["format"] = format,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.IsError == true || !File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException("Nota did not produce an audio file. Check the open project and try again.");
            bool? audible = null;
            if (Encoders.Here)
            {
                try
                {
                    var check = await new MediaLook().SilentAsync(path, cancellationToken).ConfigureAwait(false);
                    if (check.Problem is null) audible = !check.Silent;
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
            var draft = new NotaAudioDraft(path, format, new FileInfo(path).Length, audible);
            // A small local sidecar avoids decoding every old draft on each page visit.
            try { await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(draft), cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { }
            return draft;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public static NotaAudioDraft ReadDraft(string path)
    {
        var info = new FileInfo(path);
        bool? audible = null;
        try
        {
            var sidecar = path + ".json";
            if (File.Exists(sidecar)) audible = JsonSerializer.Deserialize<NotaAudioDraft>(File.ReadAllText(sidecar))?.Audible;
        }
        catch (Exception) { }
        return new(path, info.Extension.TrimStart('.').ToLowerInvariant(), info.Length, audible);
    }

    public async Task<string> OverviewAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null) throw new InvalidOperationException("Connect to Nota first.");
        var result = await _client.CallToolAsync("get_overview", new Dictionary<string, object?>(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.IsError == true) throw new IOException("Nota could not describe the open song.");
        var raw = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (!root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
                return "A song is open in Nota.";
            var regular = tracks.EnumerateArray()
                .Where(track => !track.TryGetProperty("type", out var type) || type.GetString() != "return")
                .ToArray();
            var clips = regular.Sum(track => track.TryGetProperty("clips", out var items) &&
                items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0);
            var bpm = root.TryGetProperty("bpm", out var tempo) && tempo.TryGetDouble(out var value)
                ? $"{value:0.##} BPM · " : string.Empty;
            return $"{bpm}{regular.Length} track{(regular.Length == 1 ? "" : "s")} · "
                + $"{clips} clip{(clips == 1 ? "" : "s")}";
        }
        catch (JsonException)
        {
            return raw.Length <= 300 ? raw : raw[..300] + "…";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _tools = [];
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
    }

    private sealed class NotaAgentTool(SdkMcpClient client, McpClientTool tool) : IAgentTool
    {
        public string Name => tool.Name;
        public string Description => tool.Description ?? string.Empty;
        public JsonNode? ArgumentsSchema => JsonNode.Parse(tool.JsonSchema.GetRawText());
        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments?.ToJsonString() ?? "{}")!;
                var result = await client.CallToolAsync(tool.Name, values, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var output = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
                return result.IsError == true
                    ? new AgentToolResult(false, string.Empty, output)
                    : new AgentToolResult(true, output, null);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return new AgentToolResult(false, string.Empty, $"Nota could not run {tool.Name}: {error.Message}");
            }
        }
    }
}

public static class NotaStudioServiceCollectionExtensions
{
    public static IServiceCollection AddConciergeNotaStudio(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => new NotaStudio(
            provider.GetRequiredService<IToolApprovalService>(),
            PrepareDrafts()));
        services.AddSingleton<IAgentToolSource>(provider => provider.GetRequiredService<NotaStudio>());
        return services;
    }

    private static string PrepareDrafts()
    {
        var paths = ConciergeDataPaths.Default;
        paths.CopyLegacyNotaDrafts();
        return paths.NotaDrafts;
    }
}
