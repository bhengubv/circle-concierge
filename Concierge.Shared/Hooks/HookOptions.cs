using System.Text.Json;

namespace Concierge.Shared.Hooks;

/// <summary>One hook program, as written in the file.</summary>
/// <param name="Event">
/// When to run it: <c>preToolUse</c>, <c>postToolUse</c>, <c>userPromptSubmit</c>
/// or <c>stop</c>.
/// </param>
/// <param name="Command">The executable. No shell, so no quoting rules.</param>
/// <param name="Arguments">Arguments, already split.</param>
/// <param name="Enabled">
/// False leaves it on file without running it. Turning a hook off to find out
/// whether it is the thing misbehaving should not mean deleting it.
/// </param>
public sealed record HookConfig(
    string Event,
    string Command,
    IReadOnlyList<string>? Arguments = null,
    bool Enabled = true);

/// <summary>
/// Which hook programs to run, read from a file beside the other local state.
///
/// `ProcessHookBridge` was written, complete, and never constructed. This is the
/// configuration it was missing.
///
/// A file rather than a settings screen, for the reason MCP uses one: a hook is a
/// command line, and command lines are things people paste, version and share.
/// Engineering shows what is registered and where the file lives.
///
/// **Nothing is configured by default, and that is the important part.** A hook is
/// an arbitrary program Concierge runs on your machine before it does things — the
/// most powerful extension point in the product and the one most worth being
/// deliberate about. It exists because a `preToolUse` hook can *refuse* a tool
/// call, which is a genuine safety control somebody may want to write for
/// themselves. Nothing ships enabled, no sample file is written, and an absent
/// file means no hooks and no complaint.
/// </summary>
public sealed class HookOptions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;

    // System.IO.Path qualified: this class exposes a Path property of its own,
    // which shadows it — the same trip already taken in McpServerOptions and
    // FileSessionState.
    public HookOptions(string? path = null)
        => _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Concierge",
            "hooks.json");

    /// <summary>Where the file is, so the UI can say so.</summary>
    public string Path => _path;

    /// <summary>
    /// What is registered. Never throws: a malformed file means no hooks and a
    /// reason, not a failure to start.
    ///
    /// Failing closed matters more here than it does for MCP. A broken hooks file
    /// that silently ran *some* of its hooks would give somebody a safety control
    /// they believe is in force and is not.
    /// </summary>
    public (IReadOnlyList<HookRegistration> Hooks, string? Problem) Load()
    {
        if (!File.Exists(_path))
        {
            return ([], null);
        }

        List<HookConfig>? configured;

        try
        {
            configured = JsonSerializer.Deserialize<List<HookConfig>>(File.ReadAllText(_path), Json);
        }
        catch (JsonException)
        {
            return ([], $"{_path} is not valid JSON, so no hooks are running.");
        }
        catch (IOException unreadable)
        {
            return ([], $"{_path} could not be read: {unreadable.Message}");
        }

        if (configured is null || configured.Count == 0)
        {
            return ([], null);
        }

        var hooks = new List<HookRegistration>();
        var skipped = 0;

        foreach (var hook in configured)
        {
            if (!hook.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(hook.Command) || EventOf(hook.Event) is not { } when)
            {
                skipped++;
                continue;
            }

            hooks.Add(new HookRegistration(when, hook.Command, hook.Arguments ?? []));
        }

        var problem = skipped == 0
            ? null
            : skipped == 1
                ? "1 hook was skipped: it needs a command and a known event."
                : $"{skipped} hooks were skipped: each needs a command and a known event.";

        return (hooks, problem);
    }

    /// <summary>
    /// The event by name, or null. Unknown names are refused rather than defaulted
    /// — a typo that silently becomes <c>preToolUse</c> would run somebody's
    /// program in front of every tool call they own.
    /// </summary>
    internal static HookEvent? EventOf(string? name) => name?.Replace("-", string.Empty).ToLowerInvariant() switch
    {
        "pretooluse" or "pretool" => HookEvent.PreToolUse,
        "posttooluse" or "posttool" => HookEvent.PostToolUse,
        "userpromptsubmit" or "prompt" => HookEvent.UserPromptSubmit,
        "stop" => HookEvent.Stop,
        _ => null,
    };
}
