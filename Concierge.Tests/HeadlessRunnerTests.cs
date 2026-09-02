using System.Runtime.CompilerServices;
using Concierge.Shared.Chat;
using Concierge.Shared.Headless;

namespace Concierge.Tests;

/// <summary>
/// What the headless runner must do (parity feature 35): take one task, run it to
/// completion, and return the answer, with no user interface at all.
/// </summary>
/// <remarks>
/// <c>Concierge.Cli</c> today prints service snapshots and drives no conversation — it can
/// tell you the engine is loaded but cannot ask it anything. This is the piece that makes
/// the CLI a real host and makes the product scriptable.
/// </remarks>
public sealed class HeadlessRunnerTests
{
    [Fact]
    public async Task A_task_comes_back_answered()
    {
        var runner = new HeadlessRunner(new ScriptedRuntime("the answer"));

        var result = await runner.RunAsync("the question");

        Assert.True(result.Success);
        Assert.Equal("the answer", result.Output);
    }

    [Fact]
    public async Task Streamed_fragments_are_assembled()
    {
        var runner = new HeadlessRunner(new ScriptedRuntime("one ", "two ", "three"));

        var result = await runner.RunAsync("the question");

        Assert.Equal("one two three", result.Output);
    }

    [Fact]
    public async Task The_task_reaches_the_model()
    {
        var runtime = new ScriptedRuntime("answered");
        var runner = new HeadlessRunner(runtime);

        await runner.RunAsync("THE-EXACT-QUESTION");

        Assert.Contains("THE-EXACT-QUESTION", runtime.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_persona_is_sent_ahead_of_the_task()
    {
        var runtime = new ScriptedRuntime("answered");
        var runner = new HeadlessRunner(runtime, persona: "You are Bell.");

        await runner.RunAsync("a question");

        Assert.Equal("system", runtime.LastTurns[0].Role);
        Assert.Contains("Bell", runtime.LastTurns[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_persona_means_no_system_turn()
    {
        var runtime = new ScriptedRuntime("answered");
        var runner = new HeadlessRunner(runtime);

        await runner.RunAsync("a question");

        Assert.DoesNotContain(runtime.LastTurns, turn => turn.Role == "system");
    }

    [Fact]
    public async Task An_engine_that_is_not_ready_fails_rather_than_hanging()
    {
        var runner = new HeadlessRunner(new NotReadyRuntime());

        var result = await runner.RunAsync("a question");

        Assert.False(result.Success);
        Assert.Contains("not ready", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_engine_that_throws_is_reported_not_propagated()
    {
        var runner = new HeadlessRunner(new ThrowingRuntime());

        var result = await runner.RunAsync("a question");

        Assert.False(result.Success);
        Assert.Contains("model unavailable", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_task_is_refused()
    {
        var result = await new HeadlessRunner(new ScriptedRuntime("x")).RunAsync("   ");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task A_run_can_be_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await new HeadlessRunner(new ScriptedRuntime("x")).RunAsync("a question", cancellation.Token);

        Assert.False(result.Success);
    }

    private sealed class ScriptedRuntime(params string[] chunks) : IChatRuntime
    {
        public string LastPrompt { get; private set; } = string.Empty;
        public IReadOnlyList<ChatTurn> LastTurns { get; private set; } = [];

        public string Id => "scripted";
        public string EngineLabel => "Scripted";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastTurns = messages;
            LastPrompt = string.Join("\n", messages.Select(message => message.Content));
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed class NotReadyRuntime : IChatRuntime
    {
        public string Id => "not-ready";
        public string EngineLabel => "Not ready";
        public bool IsReady => false;
        public string StatusMessage => "The engine is not ready: still loading the model.";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return StatusMessage;
        }
    }

    private sealed class ThrowingRuntime : IChatRuntime
    {
        public string Id => "throwing";
        public string EngineLabel => "Throwing";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("model unavailable");
#pragma warning disable CS0162 // Unreachable: an iterator needs a yield to be an iterator.
            yield break;
#pragma warning restore CS0162
        }
    }
}
