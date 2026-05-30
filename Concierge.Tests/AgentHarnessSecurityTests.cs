using Concierge.Shared;

namespace Concierge.Tests;

public sealed class AgentHarnessSecurityTests
{
    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task File_tools_reject_paths_outside_the_workspace(string unsafePath)
    {
        var harness = CreateHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ReadFileAsync(unsafePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.PreviewWriteFileAsync(unsafePath, "nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.WriteFileAsync(unsafePath, "nope", approved: true));
    }

    [Fact]
    public async Task Safe_file_preview_shows_reviewable_diff_before_write()
    {
        var harness = CreateHarness();
        var path = $".concierge-artifacts/tests/preview-{Guid.NewGuid():N}.txt";

        var preview = await harness.PreviewWriteFileAsync(path, "first line");

        Assert.True(preview.RequiresApproval);
        Assert.Equal(path, preview.RelativePath);
        Assert.Contains("--- before", preview.DiffPreview, StringComparison.Ordinal);
        Assert.Contains("+++ after", preview.DiffPreview, StringComparison.Ordinal);
        Assert.Contains("+ first line", preview.DiffPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_read_only_commands_require_approval_even_when_executable_is_allowlisted()
    {
        var harness = CreateHarness();

        var result = await harness.RunCommandAsync("dotnet --version", approved: false);

        Assert.Equal(ConciergeToolOutcome.ApprovalRequired, result.Outcome);
        Assert.Contains("requires approval", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shell_output_redacts_common_secret_prefixes()
    {
        var harness = CreateHarness();

        var result = await harness.RunCommandAsync("cmd /c echo sk-1234567890 ghp_abcdefghij AKIA1234567890", approved: true);

        Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
        Assert.Contains("sk-[REDACTED]", result.Output, StringComparison.Ordinal);
        Assert.Contains("ghp_[REDACTED]", result.Output, StringComparison.Ordinal);
        Assert.Contains("AKIA[REDACTED]", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-1234567890", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_abcdefghij", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA1234567890", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_goal_stops_after_first_denied_command_and_persists_log()
    {
        var harness = CreateHarness();

        var log = await harness.RunGoalAsync(
            "Stop when unsafe",
            ["dotnet --info && dotnet build", "dotnet --info"],
            approved: true);

        Assert.Single(log.Results);
        Assert.Equal(ConciergeToolOutcome.Denied, log.Results[0].Outcome);
        Assert.Contains(harness.GetRunLogs(), persisted => persisted.Id == log.Id && persisted.Results.Count == 1);
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".env")]
    [InlineData("appsettings.json")]
    [InlineData("secrets.json")]
    public async Task File_tools_reject_protected_workspace_files(string protectedPath)
    {
        var harness = CreateHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.PreviewWriteFileAsync(protectedPath, "secret"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.WriteFileAsync(protectedPath, "secret", approved: true));
    }

    [Fact]
    public async Task File_tools_reject_sibling_paths_that_share_a_prefix_with_the_workspace()
    {
        // Workspace at <tmp>\<guid>\concierge-prefix-tests\ws and an attacker tries to
        // reach a sibling directory whose name starts with "ws" (e.g. "ws-secret").
        // A naive StartsWith check on the root path would accept this; the harness must reject it.
        var parent = Path.Combine(Path.GetTempPath(), $"concierge-prefix-tests-{Guid.NewGuid():N}");
        var workspace = Path.Combine(parent, "ws");
        var sibling = Path.Combine(parent, "ws-secret");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "leak.txt"), "should not be reachable");
        await File.WriteAllTextAsync(Path.Combine(workspace, "Concierge.slnx"), string.Empty);

        var harness = new AgentHarnessService(workspace);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ReadFileAsync(Path.Combine("..", "ws-secret", "leak.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.PreviewWriteFileAsync(Path.Combine("..", "ws-secret", "leak.txt"), "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.WriteFileAsync(Path.Combine("..", "ws-secret", "leak.txt"), "x", approved: true));
    }

    [Fact]
    public async Task Shell_output_redacts_extended_provider_secret_shapes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var harness = CreateHarness();

        var result = await harness.RunCommandAsync(
            "cmd /c echo sk-ant-api03-abcdefghij github_pat_11ABCDEFG0_xyz0123456789 AIzaSyD-fake-key-1234567890 sk_live_abcdefghij1234567890 xoxa-1234-abcdefghij",
            approved: true);

        Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
        Assert.Contains("sk-ant-[REDACTED]", result.Output, StringComparison.Ordinal);
        Assert.Contains("github_pat_[REDACTED]", result.Output, StringComparison.Ordinal);
        Assert.Contains("AIza[REDACTED]", result.Output, StringComparison.Ordinal);
        Assert.Contains("sk_live_[REDACTED]", result.Output, StringComparison.Ordinal);
        Assert.Contains("xoxa-[REDACTED]", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("api03-abcdefghij", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("11ABCDEFG0_xyz", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_git_subcommands_still_run_without_approval()
    {
        var harness = CreateHarness();

        var status = await harness.RunCommandAsync("git status", approved: false);

        // git is allowlisted but the workspace is not a repo, so the call may fail; the important
        // contract is that the command was not denied or held for approval.
        Assert.NotEqual(ConciergeToolOutcome.ApprovalRequired, status.Outcome);
        Assert.NotEqual(ConciergeToolOutcome.Denied, status.Outcome);
    }

    [Fact]
    public async Task Tokenized_read_only_check_does_not_match_a_smuggled_git_subcommand()
    {
        var harness = CreateHarness();

        // "git statusfoo" should NOT be treated as the read-only "git status"; it must require approval.
        var smuggled = await harness.RunCommandAsync("git statusfoo", approved: false);

        Assert.Equal(ConciergeToolOutcome.ApprovalRequired, smuggled.Outcome);
    }

    [Fact]
    public async Task File_tools_reject_reparse_points_when_present()
    {
        var root = CreateWorkspaceRoot();
        var outside = Path.Combine(Path.GetTempPath(), "concierge-harness-outside", Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "linked");
        Directory.CreateDirectory(outside);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/D", link, outside }
        });
        process!.WaitForExit();
        if (process.ExitCode != 0)
        {
            return;
        }

        var harness = new AgentHarnessService(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.PreviewWriteFileAsync("linked/file.txt", "escape"));
    }

    private static AgentHarnessService CreateHarness()
    {
        return new AgentHarnessService(CreateWorkspaceRoot());
    }

    private static string CreateWorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-harness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Concierge.slnx"), string.Empty);
        return root;
    }
}
