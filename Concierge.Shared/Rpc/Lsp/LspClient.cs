using System.Text.Json.Nodes;

namespace Concierge.Shared.Rpc.Lsp;

/// <summary>A place in a file.</summary>
/// <param name="FilePath">The file, as a local path.</param>
/// <param name="Line">Zero-based line.</param>
/// <param name="Character">Zero-based column.</param>
public sealed record SourceLocation(string FilePath, int Line, int Character);

/// <summary>Something a language server reported about a file.</summary>
/// <param name="Message">What is wrong.</param>
/// <param name="Line">Zero-based line it is on.</param>
/// <param name="Severity">1 error, 2 warning, 3 information, 4 hint.</param>
public sealed record SourceDiagnostic(string Message, int Line, int Severity);

/// <summary>
/// Talks to a language server, so the model can find where something is defined instead of
/// guessing at a codebase it cannot hold in context.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. A complete language-server client is a project of its own; this covers
/// definitions, references and diagnostics, which is what a model needs to navigate code, and
/// it should stay there.
/// </para>
/// <para>
/// LSP frames messages with a <c>Content-Length</c> header rather than one per line, which is
/// the only reason <see cref="HeaderFraming"/> exists.
/// </para>
/// </remarks>
public sealed class LspClient : IAsyncDisposable
{
    private readonly JsonRpcClient _rpc;

    private LspClient(JsonRpcClient rpc) => _rpc = rpc;

    /// <summary>Connect to a server over a pair of streams and complete the handshake.</summary>
    /// <param name="rootPath">The workspace the server should index.</param>
    public static async Task<LspClient> ConnectAsync(
        Stream outbound,
        Stream inbound,
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var rpc = new JsonRpcClient(outbound, inbound, new HeaderFraming());
        var client = new LspClient(rpc);

        await rpc.InvokeAsync(
            "initialize",
            new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = ToFileUri(rootPath),
                ["capabilities"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["definition"] = new JsonObject(),
                        ["references"] = new JsonObject(),
                        ["publishDiagnostics"] = new JsonObject(),
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);

        // The spec requires this after initialize; a server that never receives it may
        // refuse every later request.
        await rpc.NotifyAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);

        return client;
    }

    /// <summary>Where the symbol at this position is defined.</summary>
    public async Task<IReadOnlyList<SourceLocation>> GoToDefinitionAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
        => await LocationsAsync("textDocument/definition", filePath, line, character, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Everywhere the symbol at this position is used.</summary>
    public async Task<IReadOnlyList<SourceLocation>> FindReferencesAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
        => await LocationsAsync("textDocument/references", filePath, line, character, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>What the server currently reports about a file.</summary>
    public async Task<IReadOnlyList<SourceDiagnostic>> GetDiagnosticsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _rpc.InvokeAsync(
                "textDocument/diagnostic",
                new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = ToFileUri(filePath) },
                },
                cancellationToken).ConfigureAwait(false);

            if (result?["items"] is not JsonArray items)
            {
                return [];
            }

            return items
                .OfType<JsonObject>()
                .Select(item => new SourceDiagnostic(
                    item["message"]?.GetValue<string>() ?? string.Empty,
                    item["range"]?["start"]?["line"]?.GetValue<int>() ?? 0,
                    item["severity"]?.GetValue<int>() ?? 1))
                .ToList();
        }
        catch (JsonRpcException)
        {
            // Not every server implements pull diagnostics. An empty list is the honest
            // answer; the model asked what is wrong and nothing was reported.
            return [];
        }
    }

    private async Task<IReadOnlyList<SourceLocation>> LocationsAsync(
        string method,
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = ToFileUri(filePath) },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
        };

        if (method.EndsWith("references", StringComparison.Ordinal))
        {
            parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        }

        try
        {
            var result = await _rpc.InvokeAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            return ReadLocations(result);
        }
        catch (JsonRpcException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads locations from a reply. Servers answer with a single location, a list, or a
    /// wrapped list depending on the server and the request — all three are accepted.
    /// </summary>
    private static IReadOnlyList<SourceLocation> ReadLocations(JsonObject? result)
    {
        if (result is null)
        {
            return [];
        }

        var array = result["locations"] as JsonArray
            ?? result["result"] as JsonArray
            ?? (result["uri"] is not null ? new JsonArray(result.DeepClone()) : null);

        if (array is null)
        {
            return [];
        }

        return array
            .OfType<JsonObject>()
            .Select(location => new SourceLocation(
                FromFileUri(location["uri"]?.GetValue<string>() ?? string.Empty),
                location["range"]?["start"]?["line"]?.GetValue<int>() ?? 0,
                location["range"]?["start"]?["character"]?.GetValue<int>() ?? 0))
            .Where(location => !string.IsNullOrEmpty(location.FilePath))
            .ToList();
    }

    /// <summary>A local path as the <c>file:</c> URI the protocol expects.</summary>
    internal static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>A <c>file:</c> URI back to a local path, leaving anything else alone.</summary>
    internal static string FromFileUri(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? parsed.LocalPath
            : uri;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
}
