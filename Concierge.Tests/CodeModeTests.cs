using System.Diagnostics;
using System.Text.Json.Nodes;
using Concierge.CodeMode;
using Concierge.Shared.Sandboxing;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What the sandbox must do (parity feature 3): say what it can actually enforce, so a caller
/// can decline to run when the answer is "nothing".
/// </summary>
public sealed class CodeSandboxTests
{
    [Fact]
    public void A_platform_reports_what_it_can_enforce()
    {
        Assert.False(string.IsNullOrWhiteSpace(CodeSandbox.DescribeCurrentPlatform().Mechanism));
    }

    [Fact]
    public void A_platform_that_cannot_confine_explains_why()
    {
        // The explanation is the point. "No sandbox" with no reason is indistinguishable from
        // nobody having looked.
        var capability = new UnconfinedSandbox().Capability;

        Assert.Equal(SandboxStrength.None, capability.Strength);
        Assert.False(string.IsNullOrWhiteSpace(capability.Explanation));
    }

    [Fact]
    public void Windows_offers_a_process_boundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(SandboxStrength.Process, CodeSandbox.DescribeCurrentPlatform().Strength);
    }

    [Fact]
    public void The_windows_boundary_does_not_claim_to_confine_the_filesystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A job object limits and terminates; it does not restrict which files may be opened.
        // Claiming otherwise would be the overstatement this whole seam exists to avoid.
        Assert.NotEqual(SandboxStrength.Confined, CodeSandbox.DescribeCurrentPlatform().Strength);
    }
}

/// <summary>
/// What code mode must do (parity feature 14): run a model-written program that calls tools,
/// stop one that will not stop itself, and refuse to run at all where there is no boundary
/// unless somebody has said that is acceptable.
/// </summary>
/// <remarks>
/// The program runs in its own process. An in-process script cannot be interrupted — a
/// CancellationToken is observed at await points and a tight loop has none — which was
/// measured here, not assumed: an earlier in-process version burned ten minutes of CPU on
/// <c>while (true) { }</c> and had to be killed from outside.
/// </remarks>
public sealed class CodeModeTests
{
    // ── Refusing to run blind ──────────────────────────────────────────

    [Fact]
    public void A_runtime_refuses_to_exist_with_no_boundary_and_no_decision()
    {
        Assert.Throws<InvalidOperationException>(() => new ProcessCodeRuntime(
            Registry(),
            new UnconfinedSandbox(),
            HostPath(),
            Path.GetTempPath()));
    }

    [Fact]
    public void The_refusal_says_what_is_missing()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new ProcessCodeRuntime(
            Registry(),
            new UnconfinedSandbox(),
            HostPath(),
            Path.GetTempPath()));

        Assert.Contains("confine", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_caller_that_accepts_the_risk_may_proceed()
    {
        Assert.Equal(SandboxStrength.None, Runtime(new UnconfinedSandbox()).Sandbox.Strength);
    }

    // ── Running programs ───────────────────────────────────────────────

    [Fact]
    public async Task A_program_returns_its_value()
    {
        var result = await Runtime().RunAsync("return 6 * 7;");

        Assert.True(result.Success, result.Error);
        Assert.Equal("42", result.Value);
    }

    [Fact]
    public async Task A_program_can_print()
    {
        var result = await Runtime().RunAsync("""Print("hello"); return "done";""");

        Assert.Equal("hello", Assert.Single(result.Logs));
    }

    [Fact]
    public async Task A_program_can_call_a_tool()
    {
        var result = await Runtime(new EchoTool()).RunAsync(
            """return await CallAsync("echo", new { text = "from the program" });""");

        Assert.Contains("from the program", result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_can_call_several_tools_in_one_run()
    {
        // The whole point of code mode: what would be several round trips is one.
        var result = await Runtime(new EchoTool()).RunAsync("""
            var first = await CallAsync("echo", new { text = "one" });
            var second = await CallAsync("echo", new { text = "two" });
            return first + " and " + second;
            """);

        Assert.Contains("one", result.Value, StringComparison.Ordinal);
        Assert.Contains("two", result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tools_run_on_the_parent_side_of_the_boundary()
    {
        // The child owns the program and nothing else: it has no registry, no workspace and
        // no approval seam. A tool that runs is proof the call came back across the pipe.
        var tool = new EchoTool();

        await Runtime(tool).RunAsync("""return await CallAsync("echo", new { text = "x" });""");

        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task Calling_a_tool_that_is_not_there_is_reported_to_the_program()
    {
        var result = await Runtime().RunAsync("""return await CallAsync("imaginary");""");

        Assert.Contains("imaginary", result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_that_fails_is_reported_to_the_program_rather_than_ending_it()
    {
        var result = await Runtime(new FailingTool()).RunAsync("""
            var answer = await CallAsync("breaks");
            return "carried on: " + answer;
            """);

        Assert.StartsWith("carried on:", result.Value, StringComparison.Ordinal);
    }

    // ── Failing safely ─────────────────────────────────────────────────

    [Fact]
    public async Task A_program_that_does_not_compile_gets_the_errors_back()
    {
        var result = await Runtime().RunAsync("this is not C#");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task A_program_that_throws_is_reported_not_propagated()
    {
        var result = await Runtime().RunAsync("""throw new Exception("it broke");""");

        Assert.False(result.Success);
        Assert.Contains("it broke", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_program_is_refused()
    {
        Assert.False((await Runtime().RunAsync("   ")).Success);
    }

    // ── The one in-process could not do ────────────────────────────────

    [Fact]
    public async Task A_program_that_never_finishes_is_stopped()
    {
        // The reason the whole design is out-of-process. This loop has no await in it, so no
        // cancellation token can reach it — only killing the process ends it.
        var runtime = Runtime(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        var result = await runtime.RunAsync("while (true) { }");
        stopwatch.Stop();

        Assert.False(result.Success);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task A_stopped_program_says_it_ran_out_of_time()
    {
        var result = await Runtime(TimeSpan.FromSeconds(5)).RunAsync("while (true) { }");

        Assert.Contains("did not finish", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_program_that_prints_without_stopping_does_not_flood_the_conversation()
    {
        var result = await Runtime().RunAsync("""
            for (var i = 0; i < 5000; i++) { Print(i); }
            return "done";
            """);

        Assert.True(result.Logs.Count < 500, $"kept {result.Logs.Count} lines");
    }

    // ── As a tool ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_run_code_tool_returns_what_the_program_produced()
    {
        var tool = new RunCodeTool(Runtime());

        var result = await tool.InvokeAsync(new JsonObject { ["code"] = """Print("working"); return 1 + 1;""" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Contains("working", result.Output, StringComparison.Ordinal);
        Assert.Contains("2", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_code_tool_needs_a_program()
    {
        Assert.False((await new RunCodeTool(Runtime()).InvokeAsync(new JsonObject())).Success);
    }

    [Fact]
    public void The_run_code_tool_is_treated_as_able_to_change_things()
    {
        // A program can call any tool, so it inherits the most careful classification of
        // anything it might reach.
        Assert.False(new RunCodeTool(Runtime()).IsReadOnly);
    }

    private static ProcessCodeRuntime Runtime(params IAgentTool[] tools)
        => Runtime(TimeSpan.FromSeconds(60), tools);

    private static ProcessCodeRuntime Runtime(TimeSpan timeout, params IAgentTool[] tools)
        => new(
            Registry(tools),
            new UnconfinedSandbox(),
            HostPath(),
            Path.GetTempPath(),
            acceptNoSandbox: true,
            timeout);

    private static ProcessCodeRuntime Runtime(ICodeSandbox sandbox)
        => new(Registry(), sandbox, HostPath(), Path.GetTempPath(), acceptNoSandbox: true);

    private static IAgentToolRegistry Registry(params IAgentTool[] tools) => new AgentToolRegistry(tools);

    /// <summary>
    /// The host built beside the tests. A project reference puts it in the same output folder,
    /// so no path has to be guessed or configured.
    /// </summary>
    private static string HostPath()
        => Path.Combine(AppContext.BaseDirectory, "Concierge.CodeMode.Host.dll");

    private sealed class EchoTool : IAgentTool
    {
        private int _calls;

        public int Calls => _calls;

        public string Name => "echo";
        public string Description => "Echoes its text back";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new AgentToolResult(
                true,
                arguments?["text"]?.GetValue<string>() ?? string.Empty,
                null));
        }
    }

    private sealed class FailingTool : IAgentTool
    {
        public string Name => "breaks";
        public string Description => "Always fails";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolResult(false, string.Empty, "it did not work"));
    }
}
