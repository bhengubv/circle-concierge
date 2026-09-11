using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Reaching the other coding tools on this machine.
///
/// open-design's whole shape is a harness that drives sixteen coding assistants rather than
/// being one. Concierge is the other way round — it is the assistant — and the useful half
/// survives the inversion: somebody with Claude Code or Aider already installed has already
/// chosen their tool for large code changes, and an assistant that cannot acknowledge that
/// is one they leave in order to use it.
///
/// What these check is the part that can go badly. Handing a job over starts another agent
/// that will edit files on its own judgement, so it asks every time, the card carries the
/// job in full, and nothing that is not on the machine is ever offered.
/// </summary>
public sealed class CodingToolTests
{
    /// <summary>An approver that answers the same way every time and remembers the card.</summary>
    private sealed class Answers(ToolApprovalDecision decision) : IToolApprovalService
    {
        public ToolApprovalRequest? Asked { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Asked = request;
            return ValueTask.FromResult(decision);
        }
    }

    /// <summary>A path with whatever we say is on it and nothing else.</summary>
    private static Func<string, string?> Holding(params string[] commands)
        => command => commands.Contains(command, StringComparer.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetTempPath(), command)
            : null;

    private static CodingToolSource Source(
        Func<string, string?> path, IToolApprovalService? approval = null)
        => new(Path.GetTempPath(), approval, path);

    // ── What is offered ───────────────────────────────────────────────────

    [Fact]
    public void With_nothing_installed_nothing_is_offered()
        => Assert.Empty(Source(Holding(), new Answers(ToolApprovalDecision.Allowed)).Tools);

    [Fact]
    public void With_one_installed_both_tools_appear()
    {
        var names = Source(Holding("claude"), new Answers(ToolApprovalDecision.Allowed))
            .Tools.Select(tool => tool.Name).ToList();

        Assert.Contains("coding_tools", names);
        Assert.Contains("ask_coding_tool", names);
    }

    /// <summary>
    /// A tool that starts another agent without anybody agreeing to it is exactly what must
    /// not exist. With no way to ask, the listing stays and the handing over does not.
    /// </summary>
    [Fact]
    public void With_no_way_to_ask_the_one_that_runs_something_is_absent()
    {
        var names = Source(Holding("claude")).Tools.Select(tool => tool.Name).ToList();

        Assert.Contains("coding_tools", names);
        Assert.DoesNotContain("ask_coding_tool", names);
    }

    [Fact]
    public void Only_what_is_actually_on_the_machine_is_listed()
    {
        var here = Source(Holding("aider", "codex")).Here
            .Select(found => found.Tool.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["aider", "codex"], here);
    }

    [Fact]
    public async Task Asked_what_is_here_it_names_them()
    {
        var result = await Source(Holding("aider")).Tools
            .Single(tool => tool.Name == "coding_tools").InvokeAsync(new JsonObject());

        Assert.Contains("aider", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("claude", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Looking_at_what_is_here_changes_nothing_so_it_does_not_ask()
        => Assert.True(Source(Holding("claude")).Tools
            .Single(tool => tool.Name == "coding_tools").IsReadOnly);

    // ── Handing a job over ────────────────────────────────────────────────

    private static IAgentTool Hand(CodingToolSource source)
        => source.Tools.Single(tool => tool.Name == "ask_coding_tool");

    [Fact]
    public async Task A_tool_that_is_not_here_is_refused_without_asking_anybody()
    {
        var approver = new Answers(ToolApprovalDecision.Allowed);

        var result = await Hand(Source(Holding("aider"), approver)).InvokeAsync(
            new JsonObject { ["tool"] = "claude", ["job"] = "Rewrite the export." });

        Assert.False(result.Success);
        Assert.Null(approver.Asked);
        Assert.Contains("aider", result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nobody can make a decision about "run a coding tool". Everybody can make one about
    /// which tool and what it is being asked to do.
    /// </summary>
    [Fact]
    public async Task The_card_carries_the_tool_and_the_job_in_full()
    {
        var approver = new Answers(ToolApprovalDecision.Denied);

        await Hand(Source(Holding("aider"), approver)).InvokeAsync(
            new JsonObject { ["tool"] = "aider", ["job"] = "Rewrite the export to stream." });

        Assert.NotNull(approver.Asked);
        Assert.Contains("aider", approver.Asked!.Summary, StringComparison.Ordinal);
        Assert.Contains("Rewrite the export to stream.", approver.Asked.Summary, StringComparison.Ordinal);
        Assert.Equal(ConciergeToolRisk.High, approver.Asked.Risk);
    }

    [Fact]
    public async Task Refused_means_nothing_was_started()
    {
        var result = await Hand(Source(Holding("aider"), new Answers(ToolApprovalDecision.Denied)))
            .InvokeAsync(new JsonObject { ["tool"] = "aider", ["job"] = "Delete everything." });

        Assert.False(result.Success);
        Assert.Contains("Not allowed", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_job_it_asks_for_one_rather_than_starting_anything()
    {
        var approver = new Answers(ToolApprovalDecision.Allowed);

        var result = await Hand(Source(Holding("aider"), approver))
            .InvokeAsync(new JsonObject { ["tool"] = "aider" });

        Assert.False(result.Success);
        Assert.Null(approver.Asked);
    }

    [Fact]
    public void Handing_a_job_over_changes_things_and_says_so()
        => Assert.False(Hand(Source(Holding("claude"), new Answers(ToolApprovalDecision.Allowed))).IsReadOnly);

    // ── What it knows how to say ──────────────────────────────────────────

    /// <summary>
    /// A wrong flag opens an interactive session nothing is sitting in front of, and it hangs
    /// until the time limit. So every entry carries the way that tool takes a job with nobody
    /// watching, and a tool whose form nobody is sure of is left out rather than guessed at.
    /// </summary>
    [Fact]
    public void Every_one_it_knows_carries_how_to_hand_it_a_job_without_anybody_watching()
    {
        Assert.NotEmpty(CodingToolSource.Known);

        foreach (var tool in CodingToolSource.Known)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Command), tool.Name);
            Assert.NotEmpty(tool.Before);
            Assert.False(string.IsNullOrWhiteSpace(tool.Blurb), tool.Name);
        }
    }

    [Fact]
    public void No_two_are_called_the_same_thing()
        => Assert.Equal(
            CodingToolSource.Known.Count,
            CodingToolSource.Known.Select(tool => tool.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
}
