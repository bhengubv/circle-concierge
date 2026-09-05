using System.Text.Json;

namespace Concierge.Shared.Rpc.Mcp;

/// <summary>
/// One MCP server Concierge should start and talk to.
/// </summary>
/// <param name="Name">What to call it in the UI. Also disambiguates tool names.</param>
/// <param name="Command">The executable, e.g. <c>npx</c> or an absolute path.</param>
/// <param name="Arguments">Arguments, already split — no shell, so no quoting rules.</param>
/// <param name="Enabled">
/// False leaves it configured but not started. Useful for keeping a server on file
/// without granting it a way into the conversation.
/// </param>
public sealed record McpServerConfig(
    string Name,
    string Command,
    IReadOnlyList<string>? Arguments = null,
    bool Enabled = true);

/// <summary>
/// Which MCP servers to run, read from a file beside the other local state.
///
/// A file rather than a settings screen, deliberately. A server is a command
/// line with arguments and, often, an API key in its environment — that is a
/// thing people paste, version and share, not something anyone wants to retype
/// into a form. The Engineering room shows what is connected and where the
/// file lives; editing happens in an editor.
///
/// Nothing is configured by default. An assistant that silently starts talking
/// to other software because a sample file shipped with it would be a poor
/// trade for saving somebody one paste.
/// </summary>
public sealed class McpServerOptions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;

    // System.IO.Path qualified: this class exposes a Path property of its own,
    // matching IConciergeSecretStore and FileSessionState, which shadows it.
    public McpServerOptions(string? path = null)
        => _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Concierge",
            "mcp.json");

    /// <summary>Where the file is, so the UI can say so.</summary>
    public string Path => _path;

    /// <summary>
    /// What is configured. Never throws: a malformed file means no servers and
    /// a reason, not a failure to start.
    /// </summary>
    public (IReadOnlyList<McpServerConfig> Servers, string? Problem) Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return (Array.Empty<McpServerConfig>(), null);
            }

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return (Array.Empty<McpServerConfig>(), null);
            }

            var servers = JsonSerializer.Deserialize<List<McpServerConfig>>(json, Json);
            if (servers is null)
            {
                return (Array.Empty<McpServerConfig>(), "The MCP file could not be read.");
            }

            var usable = servers
                .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Command))
                .ToList();

            var skipped = servers.Count - usable.Count;
            return (usable, skipped > 0
                ? $"{skipped} entr{(skipped == 1 ? "y" : "ies")} skipped — each needs a name and a command."
                : null);
        }
        catch (JsonException ex)
        {
            // Named rather than swallowed: a person who has just edited this
            // file needs to know it did not take.
            return (Array.Empty<McpServerConfig>(), $"The MCP file is not valid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (Array.Empty<McpServerConfig>(), $"The MCP file could not be read: {ex.Message}");
        }
    }
}
