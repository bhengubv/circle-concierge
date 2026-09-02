using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Rpc;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Concierge.CodeMode.Host;

/// <summary>
/// Runs one model-written program in its own process.
/// </summary>
/// <remarks>
/// <para>
/// The program arrives on standard input as a JSON-RPC request. Every tool it calls goes back
/// to the parent as another JSON-RPC request on standard output, and the answer comes back on
/// standard input. The parent owns the tools; this process owns only the program.
/// </para>
/// <para>
/// A separate process because an in-process script cannot be stopped. A CancellationToken is
/// observed at await points, and a tight loop has none — so <c>while (true) { }</c> in the
/// same process runs until the machine is restarted. Out here, the parent kills it.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main()
    {
        // Binary streams: the framing counts bytes, and a text writer would re-encode and
        // corrupt anything outside ASCII on the way through.
        await using var outbound = Console.OpenStandardOutput();
        await using var inbound = Console.OpenStandardInput();

        var framing = new LineFraming();
        var pending = new Dictionary<int, TaskCompletionSource<JsonObject?>>();
        var nextId = 0;
        var writeGate = new SemaphoreSlim(1, 1);

        async Task SendAsync(JsonRpcMessage message)
        {
            await writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await framing.WriteAsync(outbound, message.ToJson()).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        // The program's only way out: ask the parent to run a tool and wait for the answer.
        async Task<string> CallToolAsync(string toolName, object? arguments)
        {
            var id = Interlocked.Increment(ref nextId);
            var waiter = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending)
            {
                pending[id] = waiter;
            }

            await SendAsync(JsonRpcMessage.Request(id, "tool/call", new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments is null
                    ? new JsonObject()
                    : JsonSerializer.SerializeToNode(arguments, JsonOptions),
            })).ConfigureAwait(false);

            var reply = await waiter.Task.ConfigureAwait(false);
            return reply?["output"]?.GetValue<string>() ?? string.Empty;
        }

        while (true)
        {
            var raw = await framing.ReadAsync(inbound).ConfigureAwait(false);
            if (raw is null)
            {
                // The parent closed the pipe. Nothing left to run for.
                return 0;
            }

            var message = JsonRpcMessage.Parse(raw);
            if (message is null)
            {
                continue;
            }

            // A reply to one of the program's own tool calls.
            if (message.Method is null && message.Id is { } replyId)
            {
                TaskCompletionSource<JsonObject?>? waiter;
                lock (pending)
                {
                    pending.Remove(replyId, out waiter);
                }

                waiter?.TrySetResult(message.IsError ? null : message.Result);
                continue;
            }

            if (message.Method != "run" || message.Id is not { } runId)
            {
                continue;
            }

            var program = message.Params?["code"]?.GetValue<string>() ?? string.Empty;

            // Run on its own task, never inline. This loop is the only thing that delivers
            // replies to the program's tool calls, so awaiting the program here would leave
            // it waiting for an answer this process is too busy to read.
            _ = Task.Run(async () =>
            {
                var result = await RunAsync(program, CallToolAsync).ConfigureAwait(false);
                await SendAsync(JsonRpcMessage.Reply(runId, result)).ConfigureAwait(false);
            });
        }
    }

    /// <summary>Compile and run one program, reporting everything as a result rather than throwing.</summary>
    private static async Task<JsonObject> RunAsync(string program, Func<string, object?, Task<string>> callTool)
    {
        var bindings = new ProgramBindings(callTool);

        // Only what the program needs. Every assembly listed here is reach it gains.
        var options = ScriptOptions.Default
            .WithReferences(typeof(object).Assembly, typeof(ProgramBindings).Assembly, typeof(Enumerable).Assembly)
            .WithImports("System", "System.Linq", "System.Threading.Tasks", "System.Collections.Generic");

        try
        {
            var state = await CSharpScript.RunAsync(
                program,
                options,
                globals: bindings,
                globalsType: typeof(ProgramBindings)).ConfigureAwait(false);

            return Result(true, state.ReturnValue?.ToString() ?? string.Empty, bindings.Logs, null);
        }
        catch (CompilationErrorException failure)
        {
            return Result(false, string.Empty, bindings.Logs, string.Join("\n", failure.Diagnostics));
        }
        catch (Exception exception)
        {
            return Result(false, string.Empty, bindings.Logs, exception.Message);
        }
    }

    private static JsonObject Result(bool success, string value, IReadOnlyList<string> logs, string? error)
    {
        var rendered = new JsonArray();
        foreach (var line in logs)
        {
            rendered.Add(line);
        }

        var result = new JsonObject
        {
            ["success"] = success,
            ["value"] = value,
            ["logs"] = rendered,
        };

        if (error is not null)
        {
            result["error"] = error;
        }

        return result;
    }
}

/// <summary>
/// Everything a program can reach by name: the tools, and somewhere to print.
/// </summary>
/// <remarks>
/// Deliberately tiny. A program gets what it was told about and nothing else the host has.
/// </remarks>
public sealed class ProgramBindings(Func<string, object?, Task<string>> callTool)
{
    private readonly List<string> _logs = [];

    /// <summary>What the program printed.</summary>
    public IReadOnlyList<string> Logs => _logs;

    /// <summary>Print something for the person reading the result.</summary>
    public void Print(object? value) => _logs.Add(value?.ToString() ?? string.Empty);

    /// <summary>Call one of Concierge's tools by name.</summary>
    public Task<string> CallAsync(string toolName, object? arguments = null) => callTool(toolName, arguments);
}
