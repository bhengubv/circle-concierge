using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Rpc;
using Concierge.Shared.Sandboxing;
using Concierge.Shared.Tools;

namespace Concierge.CodeMode;

/// <summary>What running a model-written program produced.</summary>
/// <param name="Success">Whether it finished.</param>
/// <param name="Value">What it returned, rendered as text.</param>
/// <param name="Logs">Anything it printed, in order.</param>
/// <param name="Error">Why it did not finish, when it did not.</param>
public sealed record CodeRunResult(bool Success, string Value, IReadOnlyList<string> Logs, string? Error);

/// <summary>
/// Runs one model-written program against Concierge's tools.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: a model that can write a program does in one call what otherwise takes six
/// round trips, and round trips are the expensive thing when the model is on the device or the
/// link is thin.
/// </para>
/// <para>
/// Why it is careful: the program is written by a model and runs on somebody's own device.
/// DeepSeek Harness runs the equivalent in a worker thread and says the isolation is "not a
/// security claim". Here the boundary is explicit, and a caller that has none must say so
/// before anything runs.
/// </para>
/// </remarks>
public interface ICodeRuntime
{
    /// <summary>The boundary programs run inside.</summary>
    SandboxCapability Sandbox { get; }

    /// <summary>Run one program.</summary>
    Task<CodeRunResult> RunAsync(string program, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs each program in its own process, calling back for every tool it uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Out of process because in-process cannot be stopped.</b> A <c>CancellationToken</c> is
/// observed at await points; a tight loop has none, so <c>while (true) { }</c> in this process
/// would run until the machine is restarted — measured, not theorised. A child process can be
/// killed, and on Windows the job object guarantees it dies with the app even if the kill is
/// missed.
/// </para>
/// <para>
/// The child owns the program and nothing else. Every tool call comes back here over the pipe,
/// so the tools, the workspace and the approval seam all stay on this side of the boundary.
/// </para>
/// </remarks>
public sealed class ProcessCodeRuntime : ICodeRuntime
{
    private const int MaxLogEntries = 200;

    private readonly IAgentToolRegistry _tools;
    private readonly ICodeSandbox _sandbox;
    private readonly string _hostPath;
    private readonly string _workspaceRoot;
    private readonly TimeSpan _timeout;

    /// <param name="tools">What programs may call.</param>
    /// <param name="sandbox">The boundary programs run inside.</param>
    /// <param name="hostPath">The host executable, or its dll to run under <c>dotnet</c>.</param>
    /// <param name="workspaceRoot">Where the child process starts.</param>
    /// <param name="acceptNoSandbox">
    /// Required when the sandbox can enforce nothing. There is no default on purpose: running
    /// model-written code unconfined on someone's device should be a sentence somebody wrote,
    /// not a value somebody inherited.
    /// </param>
    /// <param name="timeout">How long a program may run before it is killed.</param>
    public ProcessCodeRuntime(
        IAgentToolRegistry tools,
        ICodeSandbox sandbox,
        string hostPath,
        string workspaceRoot,
        bool acceptNoSandbox = false,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _sandbox = sandbox;
        _hostPath = hostPath;
        _workspaceRoot = workspaceRoot;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        Sandbox = sandbox.Capability;

        if (Sandbox.Strength == SandboxStrength.None && !acceptNoSandbox)
        {
            throw new InvalidOperationException(
                $"Code mode will not run here: {Sandbox.Explanation} "
                + "Pass acceptNoSandbox to run anyway, having decided that is acceptable.");
        }
    }

    /// <inheritdoc />
    public SandboxCapability Sandbox { get; }

    /// <inheritdoc />
    public async Task<CodeRunResult> RunAsync(string program, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return new CodeRunResult(false, string.Empty, [], "There is no program to run.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        using var process = StartHost();
        if (process is null)
        {
            return new CodeRunResult(false, string.Empty, [], "The code host could not be started.");
        }

        try
        {
            _sandbox.Confine(process);
        }
        catch (Exception exception)
        {
            // Refusing to run is the right answer: a boundary that was asked for and could
            // not be applied is worse than one that was never claimed.
            KillQuietly(process);
            return new CodeRunResult(false, string.Empty, [], exception.Message);
        }

        try
        {
            return await ConverseAsync(process, program, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // This is the case in-process execution could not handle. The child is killed
            // whatever it was doing, including a loop with no await in it.
            KillQuietly(process);
            return new CodeRunResult(
                false,
                string.Empty,
                [],
                $"The program did not finish within {_timeout.TotalSeconds:0} seconds and was stopped.");
        }
        catch (Exception exception)
        {
            KillQuietly(process);
            return new CodeRunResult(false, string.Empty, [], exception.Message);
        }
        finally
        {
            KillQuietly(process);
        }
    }

    /// <summary>
    /// Sends the program to the child and answers its tool calls until it reports a result.
    /// </summary>
    private async Task<CodeRunResult> ConverseAsync(Process process, string program, CancellationToken cancellationToken)
    {
        var framing = new LineFraming();
        var outbound = process.StandardInput.BaseStream;
        var inbound = process.StandardOutput.BaseStream;

        await framing.WriteAsync(
            outbound,
            JsonRpcMessage.Request(1, "run", new JsonObject { ["code"] = program }).ToJson(),
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var raw = await framing.ReadAsync(inbound, cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                return new CodeRunResult(false, string.Empty, [], "The code host stopped without answering.");
            }

            var message = JsonRpcMessage.Parse(raw);
            if (message is null)
            {
                continue;
            }

            // The child asking for a tool.
            if (message.Method == "tool/call" && message.Id is { } callId)
            {
                var reply = await RunToolAsync(message.Params, cancellationToken).ConfigureAwait(false);
                await framing.WriteAsync(outbound, JsonRpcMessage.Reply(callId, reply).ToJson(), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            // The program's result.
            if (message.Id == 1 && message.Result is { } result)
            {
                return Read(result);
            }
        }
    }

    /// <summary>
    /// Runs one tool on this side of the boundary. Failures come back as text the program can
    /// read rather than as an error that ends it.
    /// </summary>
    private async Task<JsonObject> RunToolAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
        var tool = _tools.Tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            return new JsonObject { ["output"] = $"[no tool named '{name}']" };
        }

        try
        {
            var result = await tool.InvokeAsync(parameters?["arguments"], cancellationToken).ConfigureAwait(false);
            return new JsonObject
            {
                ["output"] = result.Success ? result.Output : $"[{name} failed: {result.FailureMessage}]",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new JsonObject { ["output"] = $"[{name} threw: {exception.Message}]" };
        }
    }

    private Process? StartHost()
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // A published executable is started directly; a dll is run under the shared runtime.
        if (_hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "dotnet";
            startInfo.ArgumentList.Add(_hostPath);
        }
        else
        {
            startInfo.FileName = _hostPath;
        }

        _sandbox.Prepare(startInfo, _workspaceRoot);

        var process = new Process { StartInfo = startInfo };
        return process.Start() ? process : null;
    }

    private static CodeRunResult Read(JsonObject result)
    {
        var logs = (result["logs"] as JsonArray)?
            .Select(line => line?.GetValue<string>() ?? string.Empty)
            .ToList() ?? [];

        return new CodeRunResult(
            result["success"]?.GetValue<bool>() ?? false,
            result["value"]?.GetValue<string>() ?? string.Empty,
            Cap(logs),
            result["error"]?.GetValue<string>());
    }

    /// <summary>
    /// Keeps the beginning of a program's output. A loop that prints forever produces nothing
    /// worth reading past the first page, and all of it would reach the conversation.
    /// </summary>
    private static IReadOnlyList<string> Cap(IReadOnlyList<string> logs)
        => logs.Count <= MaxLogEntries
            ? logs
            : [.. logs.Take(MaxLogEntries), $"[{logs.Count - MaxLogEntries} more lines not shown]"];

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already gone between the check and the kill.
        }
    }
}

/// <summary>
/// The model-facing <c>run_code</c> tool.
/// </summary>
/// <remarks>
/// Not read-only: a program can call any tool it likes, including ones that change things, so
/// it inherits the most careful classification of anything it might reach.
/// </remarks>
public sealed class RunCodeTool(ICodeRuntime runtime) : IAgentTool
{
    public string Name => "run_code";

    public string Description =>
        "Run a C# program that calls the other tools. Use `await CallAsync(\"tool_name\", new { arg = value })` "
        + "to call one, `Print(...)` to show something, and return a value as the answer. One program does the "
        + "work of several separate tool calls.";

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "code": { "type": "string", "description": "The program body. Top-level await and return both work." }
          },
          "required": ["code"] }
        """);

    public bool IsReadOnly => false;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var code = arguments?["code"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(code))
        {
            return new AgentToolResult(false, string.Empty, "Argument 'code' is required.");
        }

        var result = await runtime.RunAsync(code, cancellationToken).ConfigureAwait(false);

        var output = new StringBuilder();
        foreach (var line in result.Logs)
        {
            output.AppendLine(line);
        }

        if (!string.IsNullOrEmpty(result.Value))
        {
            output.Append(result.Value);
        }

        return result.Success
            ? new AgentToolResult(true, output.ToString().TrimEnd(), null)
            : new AgentToolResult(false, output.ToString().TrimEnd(), result.Error);
    }
}
