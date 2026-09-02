using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Concierge.Shared.Hooks;

/// <summary>When a hook runs.</summary>
public enum HookEvent
{
    /// <summary>Before a tool runs. A hook here can stop it.</summary>
    PreToolUse = 0,

    /// <summary>After a tool ran.</summary>
    PostToolUse = 1,

    /// <summary>When the user submits something.</summary>
    UserPromptSubmit = 2,

    /// <summary>When a turn finishes.</summary>
    Stop = 3,
}

/// <summary>What a hook decided.</summary>
/// <param name="Allowed">Whether the thing it was asked about may proceed.</param>
/// <param name="Reason">Why not, when it refused.</param>
/// <param name="ExtraContext">Anything the hook wants added to the conversation.</param>
public sealed record HookDecision(bool Allowed, string? Reason, string? ExtraContext);

/// <summary>
/// Runs external hook programs written for Claude Code or Codex.
/// </summary>
/// <remarks>
/// <para>
/// The format is a documented one: a JSON object on the hook's standard input, a JSON object
/// on its standard output. Owning the bridge means an ecosystem of existing hooks works here
/// with nothing ported, and nothing depended on.
/// </para>
/// <para>
/// A hook that fails is treated as no opinion, not as a refusal. A broken script in someone's
/// configuration must not make the assistant unusable — but a hook that explicitly refuses is
/// obeyed, which is the whole point of running it.
/// </para>
/// </remarks>
public interface IHookBridge
{
    /// <summary>Run the hooks registered for an event and combine what they said.</summary>
    Task<HookDecision> RunAsync(HookEvent hookEvent, JsonObject payload, CancellationToken cancellationToken = default);
}

/// <summary>One hook program and when it runs.</summary>
/// <param name="Event">Which event triggers it.</param>
/// <param name="Command">The executable.</param>
/// <param name="Arguments">Its arguments.</param>
public sealed record HookRegistration(HookEvent Event, string Command, IReadOnlyList<string> Arguments);

/// <summary>Hooks run as child processes, the way the format expects.</summary>
public sealed class ProcessHookBridge : IHookBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<HookRegistration> _hooks;
    private readonly TimeSpan _timeout;

    /// <param name="hooks">What to run and when.</param>
    /// <param name="timeout">
    /// How long a hook may take. Short by default: a hook runs in front of a user waiting for
    /// an answer, so a slow one is worse than a missing one.
    /// </param>
    public ProcessHookBridge(IReadOnlyList<HookRegistration> hooks, TimeSpan? timeout = null)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc />
    public async Task<HookDecision> RunAsync(
        HookEvent hookEvent,
        JsonObject payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var context = new StringBuilder();

        foreach (var hook in _hooks.Where(hook => hook.Event == hookEvent))
        {
            var decision = await RunOneAsync(hook, payload, cancellationToken).ConfigureAwait(false);

            // One refusal is enough. A hook exists to be able to say no, so the first no wins
            // and the rest are not consulted.
            if (!decision.Allowed)
            {
                return decision;
            }

            if (!string.IsNullOrWhiteSpace(decision.ExtraContext))
            {
                context.AppendLine(decision.ExtraContext);
            }
        }

        return new HookDecision(true, null, context.Length == 0 ? null : context.ToString().TrimEnd());
    }

    private async Task<HookDecision> RunOneAsync(
        HookRegistration hook,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = hook.Command,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            foreach (var argument in hook.Arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();

            try
            {
                await process.StandardInput.WriteAsync(payload.ToJsonString().AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The hook exited without reading its input, which most hooks do — they decide
                // from their arguments. A broken pipe here says nothing about their answer, and
                // treating it as a broken hook turned a refusal into permission.
            }

            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return ReadDecision(output, process.ExitCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A missing binary, a crash, a timeout. A broken hook in someone's configuration
            // must not make the assistant unusable, so this is no opinion rather than a no.
            return new HookDecision(true, null, null);
        }
    }

    /// <summary>
    /// Reads a hook's answer. A non-zero exit is a refusal even with no output, which is how
    /// the simplest possible hook — a shell script that exits 1 — is expected to work.
    /// </summary>
    public static HookDecision ReadDecision(string? output, int exitCode)
    {
        if (!string.IsNullOrWhiteSpace(output))
        {
            try
            {
                if (JsonNode.Parse(output) is JsonObject parsed)
                {
                    var allowed = parsed["allow"]?.GetValue<bool>()
                        ?? parsed["continue"]?.GetValue<bool>()
                        ?? exitCode == 0;

                    return new HookDecision(
                        allowed,
                        parsed["reason"]?.GetValue<string>(),
                        parsed["context"]?.GetValue<string>()
                            ?? parsed["additionalContext"]?.GetValue<string>());
                }
            }
            catch (JsonException)
            {
                // A hook that printed something other than JSON has still exited with a code,
                // and that is answer enough.
            }
        }

        return new HookDecision(exitCode == 0, exitCode == 0 ? null : $"A hook refused (exit code {exitCode}).", null);
    }
}
