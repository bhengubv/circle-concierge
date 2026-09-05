using System.Diagnostics;
using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Rpc.Mcp;

/// <summary>How one configured server is getting on.</summary>
/// <param name="Name">As configured.</param>
/// <param name="Connected">Whether the handshake succeeded.</param>
/// <param name="ToolCount">How many tools it published.</param>
/// <param name="Status">One plain line for the Engineering room.</param>
/// <param name="ToolNames">
/// What it published, by name. A count tells you a server is alive; the names
/// tell you what it can actually do to your machine, which is the question
/// Engineering exists to answer.
/// </param>
public sealed record McpServerStatus(
    string Name,
    bool Connected,
    int ToolCount,
    string Status,
    IReadOnlyList<string>? ToolNames = null);

/// <summary>
/// Starts the configured MCP servers and offers their tools to the registry.
///
/// Every server is a child process spoken to over its standard streams — the
/// same shape as the model host, and for one of the same reasons: a server that
/// dies takes its pipe with it and nothing else.
///
/// Two things this does that a bare connection would not:
///
/// It wraps every remote tool so a person is asked before it runs. MCP says
/// nothing about whether a tool reads or writes, and the local tools that can
/// act ask for themselves — a remote one that skipped the question would be the
/// only way to make something happen on this machine without being asked, which
/// is precisely backwards for code somebody else wrote.
///
/// It namespaces them. Two servers may both publish "search", and a collision
/// silently resolved by first-wins is a tool call going somewhere unintended.
/// </summary>
public sealed class McpToolSource : IAgentToolSource, IAsyncDisposable
{
    private readonly McpServerOptions _options;
    private readonly IToolApprovalService _approval;
    private readonly List<Connection> _connections = new();
    private readonly object _gate = new();

    private IReadOnlyList<IAgentTool> _tools = [];
    private IReadOnlyList<McpServerStatus> _statuses = [];
    private string? _problem;

    public McpToolSource(McpServerOptions options, IToolApprovalService approval)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
    }

    public IReadOnlyList<IAgentTool> Tools
    {
        get { lock (_gate) { return _tools; } }
    }

    /// <summary>What to show in Engineering: every configured server and how it went.</summary>
    public IReadOnlyList<McpServerStatus> Statuses
    {
        get { lock (_gate) { return _statuses; } }
    }

    /// <summary>Where the file is, and anything wrong with it.</summary>
    public string ConfigPath => _options.Path;

    public string? ConfigProblem
    {
        get { lock (_gate) { return _problem; } }
    }

    /// <summary>
    /// Starts every enabled server and collects its tools. Failures are recorded
    /// per server rather than thrown: one server refusing to start must not cost
    /// the others, and must certainly not cost the app.
    /// </summary>
    public async Task ConnectAllAsync(CancellationToken cancellationToken = default)
    {
        var (servers, problem) = _options.Load();

        var tools = new List<IAgentTool>();
        var statuses = new List<McpServerStatus>();

        foreach (var server in servers)
        {
            if (!server.Enabled)
            {
                statuses.Add(new McpServerStatus(server.Name, false, 0, "Turned off."));
                continue;
            }

            var (connection, status) = await StartAsync(server, cancellationToken).ConfigureAwait(false);
            statuses.Add(status);

            if (connection is null)
            {
                continue;
            }

            lock (_gate)
            {
                _connections.Add(connection);
            }

            tools.AddRange(connection.Tools);
        }

        lock (_gate)
        {
            _tools = tools;
            _statuses = statuses;
            _problem = problem;
        }
    }

    private async Task<(Connection? Connection, McpServerStatus Status)> StartAsync(
        McpServerConfig server, CancellationToken cancellationToken)
    {
        Process? process = null;

        try
        {
            var start = new ProcessStartInfo(server.Command)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in server.Arguments ?? Array.Empty<string>())
            {
                start.ArgumentList.Add(argument);
            }

            process = Process.Start(start);
            if (process is null)
            {
                return (null, new McpServerStatus(server.Name, false, 0, "Would not start."));
            }

            // Drained, or a server that logs will fill its pipe and wedge —
            // the same trap the model host hit.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                    {
                    }
                }
                catch (Exception)
                {
                }
            }, CancellationToken.None);

            var client = await McpClient.ConnectAsync(
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                cancellationToken).ConfigureAwait(false);

            var published = await client.GetAgentToolsAsync(cancellationToken).ConfigureAwait(false);

            var wrapped = published
                .Select(tool => (IAgentTool)new ApprovedMcpTool(server.Name, tool, _approval))
                .ToList();

            return (new Connection(process, client, wrapped),
                    new McpServerStatus(server.Name, true, wrapped.Count,
                        wrapped.Count == 1 ? "Connected, 1 tool." : $"Connected, {wrapped.Count} tools.",
                        published.Select(t => t.Name).ToList()));
        }
        catch (Exception ex)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            process?.Dispose();
            return (null, new McpServerStatus(server.Name, false, 0, $"Did not connect: {ex.Message}"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Connection> connections;
        lock (_gate)
        {
            connections = _connections.ToList();
            _connections.Clear();
            _tools = [];
        }

        foreach (var connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record Connection(Process Process, McpClient Client, IReadOnlyList<IAgentTool> Tools)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            Process.Dispose();
        }
    }
}

/// <summary>
/// A remote tool, namespaced and gated behind a person.
///
/// The name is prefixed with its server because two servers may both publish
/// "search", and a silent collision means a call going somewhere nobody chose.
///
/// The approval is the more important half. Local tools that can act ask for
/// themselves; a remote tool arriving through a protocol that says nothing
/// about what it does would otherwise be the one way to make something happen
/// on this machine unasked — the exact opposite of the guarantee.
/// </summary>
internal sealed class ApprovedMcpTool : IAgentTool
{
    private readonly IAgentTool _inner;
    private readonly IToolApprovalService _approval;
    private readonly string _server;

    public ApprovedMcpTool(string server, IAgentTool inner, IToolApprovalService approval)
    {
        _server = server;
        _inner = inner;
        _approval = approval;
        Name = $"{Slug(server)}__{inner.Name}";
    }

    public string Name { get; }

    public string Description => $"{_inner.Description} (from {_server})";

    public JsonNode? ArgumentsSchema => _inner.ArgumentsSchema;

    /// <summary>
    /// Never read-only. The protocol does not say, and assuming the careful
    /// answer is the only sound default for code written by someone else.
    /// </summary>
    public bool IsReadOnly => false;

    public async Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var decision = await _approval.RequestAsync(
            new ToolApprovalRequest(
                Name,
                $"Run {_inner.Name} on {_server}",
                ConciergeToolRisk.High,
                arguments?.ToJsonString()),
            cancellationToken).ConfigureAwait(false);

        if (decision != ToolApprovalDecision.Allowed)
        {
            return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was run.");
        }

        return await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private static string Slug(string name)
        => new(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
