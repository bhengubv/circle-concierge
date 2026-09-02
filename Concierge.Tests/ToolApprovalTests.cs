using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What must be true of the approval seam, stated as observable outcomes: whether bytes
/// reach the disk, whether a process ran, and what the person deciding was shown.
/// </summary>
/// <remarks>
/// Every assertion here is about an effect, not a shape. A stub implementation that
/// compiles cannot satisfy any of them.
/// </remarks>
public sealed class ToolApprovalTests : IDisposable
{
    private readonly string _workspace;
    private readonly AgentHarnessService _harness;

    public ToolApprovalTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"concierge-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _harness = new AgentHarnessService(_workspace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A child process may still hold a handle; the temp directory is disposable.
        }
    }

    // ── An unanswered request must not act ─────────────────────────────

    [Fact]
    public async Task No_approver_wired_leaves_the_file_unwritten()
    {
        var tool = new AgentHarnessWriteTool(_harness, UnavailableToolApprovalService.Instance);

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        Assert.False(File.Exists(Path.Combine(_workspace, "notes.txt")));
    }

    [Fact]
    public async Task No_approver_wired_leaves_the_command_unrun()
    {
        var marker = Path.Combine(_workspace, "ran.txt");
        var tool = new AgentHarnessRunTool(_harness, UnavailableToolApprovalService.Instance);

        await tool.InvokeAsync(RunArgs($"cmd /c echo x > \"{marker}\""));

        Assert.False(File.Exists(marker));
    }

    // ── Denial must not act ────────────────────────────────────────────

    [Fact]
    public async Task Denial_leaves_the_file_unwritten()
    {
        var tool = new AgentHarnessWriteTool(_harness, Answering(ToolApprovalDecision.Denied));

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        Assert.False(File.Exists(Path.Combine(_workspace, "notes.txt")));
    }

    [Fact]
    public async Task Denial_is_reported_to_the_caller_as_a_failure()
    {
        var tool = new AgentHarnessWriteTool(_harness, Answering(ToolApprovalDecision.Denied));

        var result = await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        Assert.False(result.Success);
    }

    // ── Approval must act ──────────────────────────────────────────────

    [Fact]
    public async Task Approval_puts_the_bytes_on_disk()
    {
        var tool = new AgentHarnessWriteTool(_harness, Answering(ToolApprovalDecision.Allowed));

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_workspace, "notes.txt")));
    }

    [Fact]
    public async Task Approval_replaces_the_contents_of_an_existing_file()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "notes.txt"), "before");
        var tool = new AgentHarnessWriteTool(_harness, Answering(ToolApprovalDecision.Allowed));

        await tool.InvokeAsync(WriteArgs("notes.txt", "after"));

        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(_workspace, "notes.txt")));
    }

    [Fact]
    public async Task Approval_lets_the_command_run()
    {
        var marker = Path.Combine(_workspace, "ran.txt");
        var tool = new AgentHarnessRunTool(_harness, Answering(ToolApprovalDecision.Allowed));

        await tool.InvokeAsync(RunArgs($"cmd /c echo x > \"{marker}\""));

        Assert.True(File.Exists(marker));
    }

    // ── A grant covers one call only ───────────────────────────────────

    [Fact]
    public async Task Each_call_is_asked_for_separately()
    {
        var approver = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var tool = new AgentHarnessWriteTool(_harness, approver);

        await tool.InvokeAsync(WriteArgs("one.txt", "a"));
        await tool.InvokeAsync(WriteArgs("two.txt", "b"));

        Assert.Equal(2, approver.Requests.Count);
    }

    // ── What the decider is shown ──────────────────────────────────────

    [Fact]
    public async Task A_write_shows_the_content_being_written_before_it_is_written()
    {
        var approver = new RecordingApprovalService(ToolApprovalDecision.Denied);
        var tool = new AgentHarnessWriteTool(_harness, approver);

        await tool.InvokeAsync(WriteArgs("notes.txt", "the new line"));

        var shown = Assert.Single(approver.Requests).Detail ?? string.Empty;
        Assert.Contains("the new line", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_over_an_existing_file_shows_what_is_being_lost()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "notes.txt"), "the old line");
        var approver = new RecordingApprovalService(ToolApprovalDecision.Denied);
        var tool = new AgentHarnessWriteTool(_harness, approver);

        await tool.InvokeAsync(WriteArgs("notes.txt", "the new line"));

        var shown = Assert.Single(approver.Requests).Detail ?? string.Empty;
        Assert.Contains("the old line", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_shows_the_exact_command_line()
    {
        var approver = new RecordingApprovalService(ToolApprovalDecision.Denied);
        var tool = new AgentHarnessRunTool(_harness, approver);

        await tool.InvokeAsync(RunArgs("dotnet --version"));

        var shown = Assert.Single(approver.Requests).Detail ?? string.Empty;
        Assert.Contains("dotnet --version", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_request_names_the_file_being_changed()
    {
        var approver = new RecordingApprovalService(ToolApprovalDecision.Denied);
        var tool = new AgentHarnessWriteTool(_harness, approver);

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        var request = Assert.Single(approver.Requests);
        Assert.Contains("notes.txt", request.Summary, StringComparison.Ordinal);
    }

    // ── Nobody is asked when there is nothing to decide ────────────────

    [Fact]
    public async Task A_malformed_call_is_rejected_without_asking_anyone()
    {
        var approver = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var tool = new AgentHarnessWriteTool(_harness, approver);

        var result = await tool.InvokeAsync(JsonNode.Parse("""{"path":""}"""));

        Assert.False(result.Success);
        Assert.Empty(approver.Requests);
    }

    [Fact]
    public async Task A_command_outside_the_allowlist_is_refused_without_asking_anyone()
    {
        var approver = new RecordingApprovalService(ToolApprovalDecision.Allowed);
        var tool = new AgentHarnessRunTool(_harness, approver);

        var result = await tool.InvokeAsync(RunArgs("curl https://example.com"));

        Assert.False(result.Success);
        Assert.Empty(approver.Requests);
    }

    [Fact]
    public async Task A_read_only_command_runs_without_asking_anyone()
    {
        var approver = new RecordingApprovalService(ToolApprovalDecision.Denied);
        var tool = new AgentHarnessRunTool(_harness, approver);

        var result = await tool.InvokeAsync(RunArgs("dotnet --info"));

        Assert.True(result.Success);
        Assert.Empty(approver.Requests);
    }

    // ── The decision is not lost ───────────────────────────────────────

    [Fact]
    public async Task A_refusal_is_visible_afterwards_in_the_audit_log()
    {
        var audit = new ToolApprovalAuditLog();
        var tool = new AgentHarnessRunTool(
            _harness,
            new AuditingToolApprovalService(Answering(ToolApprovalDecision.Denied), audit));

        await tool.InvokeAsync(RunArgs("dotnet --version"));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("run_command", entry.ToolName);
        Assert.Equal(ToolApprovalDecision.Denied, entry.Decision);
    }

    [Fact]
    public async Task An_approval_is_visible_afterwards_in_the_audit_log()
    {
        var audit = new ToolApprovalAuditLog();
        var tool = new AgentHarnessWriteTool(
            _harness,
            new AuditingToolApprovalService(Answering(ToolApprovalDecision.Allowed), audit));

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(ToolApprovalDecision.Allowed, entry.Decision);
        Assert.Contains("notes.txt", entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_audit_log_with_a_path_survives_the_process()
    {
        var path = Path.Combine(_workspace, "audit", "approvals.jsonl");
        var tool = new AgentHarnessWriteTool(
            _harness,
            new AuditingToolApprovalService(Answering(ToolApprovalDecision.Denied), new ToolApprovalAuditLog(path)));

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        Assert.Contains("write_file", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    private static IToolApprovalService Answering(ToolApprovalDecision decision)
        => new RecordingApprovalService(decision);

    private static JsonNode WriteArgs(string path, string content)
        => new JsonObject { ["path"] = path, ["content"] = content };

    private static JsonNode RunArgs(string command)
        => new JsonObject { ["command"] = command };

    /// <summary>Answers with a fixed decision and keeps what it was asked.</summary>
    private sealed class RecordingApprovalService(ToolApprovalDecision decision) : IToolApprovalService
    {
        public List<ToolApprovalRequest> Requests { get; } = [];

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(decision);
        }
    }
}
