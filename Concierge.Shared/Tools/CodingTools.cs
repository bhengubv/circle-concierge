using System.Text.Json.Nodes;
using Concierge.Shared.Sandboxing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Tools;

/// <summary>A coding assistant that can be handed a job without anybody sitting in front of it.</summary>
/// <param name="Name">What it is called — the word somebody would say.</param>
/// <param name="Command">The program on the path.</param>
/// <param name="Before">What goes in front of the job on the command line.</param>
/// <param name="Blurb">Whose it is, in a few words.</param>
public sealed record CodingTool(string Name, string Command, IReadOnlyList<string> Before, string Blurb);

/// <summary>
/// The other coding tools on this machine, and handing one of them a job.
///
/// open-design's whole shape is this: a harness that drives sixteen coding assistants rather
/// than being one. Concierge is the other way round — it is the assistant — and the useful
/// half of their idea survives the inversion. A person with Claude Code, Codex or Aider
/// already installed has already chosen their tool for large code changes, and Concierge
/// refusing to acknowledge that means they leave it to go and use one.
///
/// **Only what is actually on the machine is offered.** Nothing is installed, nothing is
/// downloaded, and a tool that is not on the path is not mentioned — the same rule every
/// device capability follows.
///
/// **It asks every single time, and the card carries the tool and the job.** This is further
/// from reversible than anything else here: it starts another agent, in the workspace, that
/// will edit files on its own judgement and not ours. "Run a coding tool" is not something
/// anybody can make a decision about; "aider — rewrite the export to stream" is.
///
/// Confined like every other command: born in a job object on Windows, `unshare` and
/// `prlimit` on Linux, and a time limit either way, because an agent that has misunderstood
/// its job can run for an afternoon.
/// </summary>
public sealed class CodingToolSource : IAgentToolSource
{
    private readonly string _root;
    private readonly IToolApprovalService? _approval;
    private readonly Func<string, string?> _find;

    /// <param name="workspaceRoot">Where the job runs.</param>
    /// <param name="approval">
    /// Asking first. Without it nothing that runs is offered — a tool that starts another
    /// agent without anybody agreeing to it is exactly what must not exist.
    /// </param>
    /// <param name="find">How a program is found on the path. Replaced in tests.</param>
    public CodingToolSource(
        string workspaceRoot, IToolApprovalService? approval = null, Func<string, string?>? find = null)
    {
        _root = string.IsNullOrWhiteSpace(workspaceRoot) ? Directory.GetCurrentDirectory() : workspaceRoot;
        _approval = approval;
        _find = find ?? OnThePath;
    }

    /// <summary>
    /// The ones this knows how to hand a job to, whether or not they are here.
    ///
    /// Each entry is a command and the words that go in front of the job — the way that tool
    /// takes a job non-interactively. Nothing is guessed: a tool whose non-interactive form
    /// nobody here is sure of is left out rather than listed with a flag that might be wrong,
    /// because a wrong flag opens an interactive session that nothing is sitting in front of
    /// and hangs until the time limit.
    /// </summary>
    public static readonly IReadOnlyList<CodingTool> Known =
    [
        new("claude", "claude", ["-p"], "Claude Code, Anthropic's."),
        new("codex", "codex", ["exec"], "Codex, OpenAI's."),
        new("gemini", "gemini", ["-p"], "Gemini CLI, Google's."),
        new("cursor-agent", "cursor-agent", ["-p"], "Cursor's agent."),
        new("aider", "aider", ["--message"], "Aider, which works against git."),
        new("opencode", "opencode", ["run"], "OpenCode."),
        new("crush", "crush", ["run"], "Crush, Charm's."),
        new("goose", "goose", ["run", "-t"], "Goose, Block's."),
        new("qwen", "qwen", ["-p"], "Qwen Code."),
    ];

    /// <summary>Which of them are actually here, with where they were found.</summary>
    public IReadOnlyList<(CodingTool Tool, string Path)> Here
        => [.. Known
            .Select(tool => (tool, path: _find(tool.Command)))
            .Where(found => found.path is not null)
            .Select(found => (found.tool, found.path!))];

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools
        => Here.Count == 0
            ? []
            : [
                new WhatElseIsHere(this),
                .. _approval is null ? Array.Empty<IAgentTool>() : [new HandItOver(this, _approval, _root)],
            ];

    /// <summary>
    /// A program on the path, or null.
    ///
    /// Windows needs the extensions tried, because `claude` on a path is `claude.cmd` and
    /// looking for a file called `claude` finds nothing at all.
    /// </summary>
    private static string? OnThePath(string command)
    {
        var endings = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", ".ps1", string.Empty }
            : [string.Empty];

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ending in endings)
            {
                try
                {
                    var path = Path.Combine(folder.Trim(), command + ending);

                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed entry in PATH costs that entry, not the search. There is
                    // usually one on a machine anybody has been working on for a while.
                }
            }
        }

        return null;
    }

    /// <summary>What else is on this machine that could take a job. Read-only.</summary>
    private sealed class WhatElseIsHere(CodingToolSource source) : IAgentTool
    {
        public string Name => "coding_tools";

        public string Description =>
            "List the other coding assistants installed on this machine that a job can be "
            + "handed to. Use this before offering to hand something over.";

        public JsonNode? ArgumentsSchema => new JsonObject { ["type"] = "object" };

        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var lines = source.Here.Select(found => $"{found.Tool.Name} — {found.Tool.Blurb}");

            return Task.FromResult(new AgentToolResult(
                true, "On this machine:" + Environment.NewLine + string.Join(Environment.NewLine, lines)));
        }
    }

    /// <summary>
    /// Handing a job to one of them.
    ///
    /// The most far-reaching thing in the catalogue, so the card carries which tool and the
    /// job in full rather than a summary of it. Everything else about it is ordinary: it runs
    /// confined, it has a time limit, and what comes back is what it printed.
    /// </summary>
    private sealed class HandItOver(CodingToolSource source, IToolApprovalService approval, string root)
        : IAgentTool
    {
        /// <summary>
        /// Long enough for a real change, short enough that a misunderstanding costs an hour
        /// rather than a day. There is no correct number; there is only the difference between
        /// having one and not.
        /// </summary>
        private static readonly TimeSpan RunFor = TimeSpan.FromMinutes(20);

        public string Name => "ask_coding_tool";

        public string Description =>
            "Hand a coding job to another assistant installed on this machine. It will change "
            + "files on its own judgement, so the person is asked first. Say the job in full.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Which one — see coding_tools.",
                },
                ["job"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The job, in full. It is all that assistant will be told.",
                },
            },
            ["required"] = new JsonArray("tool", "job"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var asked = Said(arguments, "tool");
            var job = Said(arguments, "job");

            if (asked.Length == 0 || job.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Say which tool, and what the job is.");
            }

            var found = source.Here.FirstOrDefault(here =>
                here.Tool.Name.Equals(asked, StringComparison.OrdinalIgnoreCase));

            if (found.Tool is null)
            {
                return new AgentToolResult(
                    false,
                    string.Empty,
                    $"{asked} is not on this machine. There is: "
                    + string.Join(", ", source.Here.Select(here => here.Tool.Name)) + ".");
            }

            // The tool and the job, not a summary of either. Nobody can decide about "run a
            // coding tool", and everybody can decide about the actual job.
            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    $"{found.Tool.Name}: {job}",
                    ConciergeToolRisk.High,
                    $"It starts {found.Tool.Name} in {root}, and that assistant will change files "
                    + "on its own judgement rather than asking again."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was handed over.");
            }

            var result = await ConfinedRun
                .Async(found.Path, [.. found.Tool.Before, job], root, RunFor, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Ran)
            {
                return new AgentToolResult(false, string.Empty, result.Problem ?? "It did not run.");
            }

            // A non-zero exit is reported as a failure with what it printed, because an
            // assistant that gave up halfway through is not a success with a note attached.
            var said = result.Output.Trim();

            return result.ExitCode == 0
                ? new AgentToolResult(
                    true,
                    said.Length > 0 ? said : $"{found.Tool.Name} finished and printed nothing.")
                : new AgentToolResult(
                    false,
                    string.Empty,
                    $"{found.Tool.Name} stopped with {result.ExitCode}.{Environment.NewLine}{said}");
        }

        private static string Said(JsonNode? arguments, string key)
            => arguments?[key] is JsonValue value && value.TryGetValue<string>(out var written)
                ? written.Trim()
                : string.Empty;
    }
}

/// <summary>Registering them.</summary>
public static class CodingToolsRegistration
{
    /// <summary>
    /// Adds the other coding tools as something this one can reach.
    ///
    /// Registered always: the source publishes nothing while nothing is installed, and it is
    /// asked every time the catalogue is built, so somebody who installs one afterwards does
    /// not have to restart the app to be offered it.
    /// </summary>
    public static IServiceCollection AddConciergeCodingTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource>(sp =>
            new CodingToolSource(
                (sp.GetService<IAgentHarnessService>()?.WorkspaceRoot) ?? Directory.GetCurrentDirectory(),
                sp.GetService<IToolApprovalService>())));

        return services;
    }
}
