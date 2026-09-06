using System.Text.Json.Nodes;
using Concierge.Shared.Hooks;
using Concierge.Shared.Planning;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Two more subsystems that were written and never reached.
///
/// FileTodoStore was registered in DI and nothing ever resolved it - a durable
/// list with no way in and nobody reading it. ProcessHookBridge was complete and
/// never constructed, because what it was missing was configuration.
///
/// Both are wired the careful way round: the todo list changes nothing anybody
/// else can see and so never interrupts, while a hook is an arbitrary program run
/// in front of tool calls and so is off unless somebody deliberately wrote a file.
/// </summary>
public sealed class TodoAndHookTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "concierge-todo-hooks", Guid.NewGuid().ToString("N"));

    public TodoAndHookTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    private ITodoStore Store() => new FileTodoStore(Path.Combine(_dir, "todo.json"));

    private string HooksFile => Path.Combine(_dir, "hooks.json");

    private static JsonNode Args(string json) => JsonNode.Parse(json)!;

    // ── The working list ──────────────────────────────────────────────────

    [Fact]
    public async Task An_empty_list_says_so_rather_than_saying_nothing()
    {
        var result = await new TodoReadTool(Store()).InvokeAsync(null);

        Assert.True(result.Success);
        Assert.Contains("empty", result.Output);
    }

    [Fact]
    public async Task What_is_written_can_be_read_back()
    {
        var store = Store();

        await new TodoWriteTool(store).InvokeAsync(Args("""
            { "items": [
                { "content": "Read the config", "status": "completed" },
                { "content": "Change the port", "status": "in_progress" },
                { "content": "Run the tests" }
            ] }
            """));

        var read = (await new TodoReadTool(store).InvokeAsync(null)).Output;

        Assert.Contains("[x] Read the config", read);
        Assert.Contains("[~] Change the port", read);
        Assert.Contains("[ ] Run the tests", read);
        Assert.Contains("2 still to do", read);
    }

    /// <summary>
    /// It outlives the turn, which is the entire reason it exists rather than the
    /// plan strip. The plan says what is happening now and is cleared when the turn
    /// ends; this says what is still outstanding on Monday.
    /// </summary>
    [Fact]
    public async Task The_list_survives_everything_being_thrown_away()
    {
        await new TodoWriteTool(Store()).InvokeAsync(Args("""
            { "items": [ { "content": "Still here tomorrow" } ] }
            """));

        // A completely new store over the same file, as a restart would build.
        var read = (await new TodoReadTool(Store()).InvokeAsync(null)).Output;

        Assert.Contains("Still here tomorrow", read);
    }

    /// <summary>
    /// A status nobody planned for leaves work on the list rather than quietly
    /// marking it finished.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_status_is_not_treated_as_done()
    {
        var store = Store();

        await new TodoWriteTool(store).InvokeAsync(Args("""
            { "items": [ { "content": "Ambiguous", "status": "probably fine" } ] }
            """));

        Assert.Contains("[ ] Ambiguous", (await new TodoReadTool(store).InvokeAsync(null)).Output);
    }

    /// <summary>
    /// A model asked to plan will occasionally produce sixty. A list that long is
    /// not a plan, and it costs a slice of every later request in the conversation.
    /// </summary>
    [Fact]
    public async Task A_runaway_list_is_cut_to_something_usable()
    {
        var many = string.Join(",", Enumerable.Range(1, 80).Select(i => $"{{\"content\":\"Task {i}\"}}"));

        var store = Store();
        await new TodoWriteTool(store).InvokeAsync(Args($"{{ \"items\": [{many}] }}"));

        Assert.Equal(TodoWriteTool.MaxItems, store.Read().Count);
    }

    [Fact]
    public async Task Blank_lines_are_left_out_rather_than_kept_forever()
    {
        var store = Store();

        await new TodoWriteTool(store).InvokeAsync(Args("""
            { "items": [ { "content": "Real" }, { "content": "   " }, { "content": "" } ] }
            """));

        Assert.Single(store.Read());
    }

    /// <summary>
    /// Reading your own notes is not an act, and writing them changes nothing
    /// anybody else can see. Prompting for either is the interruption that teaches
    /// somebody to stop reading prompts, which is what makes every other approval
    /// in the product worthless.
    /// </summary>
    [Fact]
    public void Reading_the_list_never_interrupts_anybody()
        => Assert.True(new TodoReadTool(Store()).IsReadOnly);

    // ── Hooks, off by default ─────────────────────────────────────────────

    /// <summary>
    /// A hook is an arbitrary program run in front of tool calls. Shipping one
    /// enabled, or writing a sample file, would be the least defensible default in
    /// the product.
    /// </summary>
    [Fact]
    public void With_no_file_there_are_no_hooks_and_no_complaint()
    {
        var (hooks, problem) = new HookOptions(HooksFile).Load();

        Assert.Empty(hooks);
        Assert.Null(problem);
    }

    [Fact]
    public void A_registered_hook_is_read()
    {
        File.WriteAllText(HooksFile, """
            [ { "event": "preToolUse", "command": "node", "arguments": ["guard.js"] } ]
            """);

        var (hooks, problem) = new HookOptions(HooksFile).Load();

        Assert.Null(problem);
        var hook = Assert.Single(hooks);
        Assert.Equal(HookEvent.PreToolUse, hook.Event);
        Assert.Equal("node", hook.Command);
        Assert.Single(hook.Arguments);
    }

    [Fact]
    public void A_hook_can_be_turned_off_without_being_deleted()
    {
        File.WriteAllText(HooksFile, """
            [ { "event": "preToolUse", "command": "node", "enabled": false } ]
            """);

        Assert.Empty(new HookOptions(HooksFile).Load().Hooks);
    }

    /// <summary>
    /// A typo that silently became preToolUse would run somebody's program in
    /// front of every tool call they own.
    /// </summary>
    [Fact]
    public void An_event_nobody_recognises_is_refused_rather_than_defaulted()
    {
        File.WriteAllText(HooksFile, """
            [ { "event": "preToolUze", "command": "node" } ]
            """);

        var (hooks, problem) = new HookOptions(HooksFile).Load();

        Assert.Empty(hooks);
        Assert.Contains("skipped", problem!);
    }

    /// <summary>
    /// Failing closed matters more here than for MCP: a broken file that ran some
    /// of its hooks would give somebody a safety control they believe is in force
    /// and is not.
    /// </summary>
    [Fact]
    public void A_broken_file_runs_nothing_and_says_why()
    {
        File.WriteAllText(HooksFile, "{ this is not json");

        var (hooks, problem) = new HookOptions(HooksFile).Load();

        Assert.Empty(hooks);
        Assert.Contains("not valid JSON", problem!);
    }

    // ── A hook refusing a call ────────────────────────────────────────────

    /// <summary>
    /// The reason hooks are worth having at all. Somebody knows what their machine
    /// holds and Concierge does not, so a rule they write themselves is worth more
    /// than any list shipped in the box.
    /// </summary>
    [Fact]
    public async Task A_hook_that_refuses_stops_the_call()
    {
        var tool = new Recording();
        var scheduler = new ToolCallScheduler(
            new AgentToolRegistry([tool]),
            maxParallel: 1,
            hooks: new Fixed(new HookDecision(false, "Not that one.", null)));

        var outcome = Assert.Single(await scheduler.ExecuteAsync([new PlannedToolCall("thing", null)]));

        Assert.False(outcome.Result.Success);
        Assert.Contains("Not that one", outcome.Result.FailureMessage!);
        Assert.False(tool.Ran);
    }

    [Fact]
    public async Task A_hook_that_allows_lets_it_through()
    {
        var tool = new Recording();
        var scheduler = new ToolCallScheduler(
            new AgentToolRegistry([tool]),
            maxParallel: 1,
            hooks: new Fixed(new HookDecision(true, null, null)));

        Assert.True((await scheduler.ExecuteAsync([new PlannedToolCall("thing", null)]))[0].Result.Success);
        Assert.True(tool.Ran);
    }

    /// <summary>
    /// With none registered, every call runs exactly as it did before hooks
    /// existed. That is the state almost everybody is in.
    /// </summary>
    [Fact]
    public async Task With_no_hooks_nothing_changes()
    {
        var tool = new Recording();
        var scheduler = new ToolCallScheduler(new AgentToolRegistry([tool]), maxParallel: 1);

        Assert.True((await scheduler.ExecuteAsync([new PlannedToolCall("thing", null)]))[0].Result.Success);
        Assert.True(tool.Ran);
    }

    /// <summary>
    /// The hook is told which tool it is deciding about, or it cannot answer
    /// usefully: a guard that cannot tell read_file from run_command has to refuse
    /// everything or nothing.
    /// </summary>
    [Fact]
    public async Task The_hook_is_told_what_it_is_deciding_about()
    {
        var seen = new Fixed(new HookDecision(true, null, null));
        var scheduler = new ToolCallScheduler(
            new AgentToolRegistry([new Recording()]), maxParallel: 1, hooks: seen);

        await scheduler.ExecuteAsync([new PlannedToolCall("thing", JsonNode.Parse("""{"path":"a.txt"}"""))]);

        Assert.Equal("thing", seen.Payload!["tool_name"]!.GetValue<string>());
        Assert.Contains("a.txt", seen.Payload["tool_input"]!.ToJsonString());
    }

    private sealed class Recording : IAgentTool
    {
        public string Name => "thing";
        public string Description => "does a thing";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => true;
        public bool Ran { get; private set; }

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            Ran = true;
            return Task.FromResult(new AgentToolResult(true, "done", null));
        }
    }

    private sealed class Fixed : IHookBridge
    {
        private readonly HookDecision _decision;

        public Fixed(HookDecision decision) => _decision = decision;

        public JsonObject? Payload { get; private set; }

        public Task<HookDecision> RunAsync(
            HookEvent hookEvent, JsonObject payload, CancellationToken cancellationToken = default)
        {
            Payload = payload;
            return Task.FromResult(_decision);
        }
    }
}
