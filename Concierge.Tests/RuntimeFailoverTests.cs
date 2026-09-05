using System.Runtime.CompilerServices;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

/// <summary>
/// Trying the next provider when one falls over.
///
/// Concierge had none of this. Three cloud runtimes and a local one, and whichever
/// was selected was the only one that would ever be asked — a provider having a bad
/// afternoon was a dead turn, a stream error pasted into the thread with two other
/// configured providers sitting idle. OpenDroid chains twelve.
///
/// What is tested here is mostly the three refusals, because a failover that tries
/// too hard is worse than none:
///
///   Never off the device. A local runtime failing must not quietly continue on
///   somebody else's computer — that would break the product's main claim at
///   exactly the moment nobody is watching.
///
///   Never past a refusal. A model that declines has answered. Asking the next
///   provider until one agrees is not resilience, it is shopping for a yes.
///
///   Never after the first token. Splicing two models mid-sentence produces
///   something neither of them said, at an invisible seam.
/// </summary>
public sealed class RuntimeFailoverTests
{
    private static IReadOnlyList<ChatTurn> Ask() => [new ChatTurn("user", "hello")];

    private static async Task<(string Text, FailoverOutcome? Outcome)> Run(
        IChatRuntime first, params IChatRuntime[] alternatives)
    {
        var text = new System.Text.StringBuilder();
        FailoverOutcome? outcome = null;

        await foreach (var chunk in RuntimeFailover.StreamAsync(
            first, alternatives, Ask(), o => outcome = o))
        {
            text.Append(chunk);
        }

        return (text.ToString(), outcome);
    }

    // ── Falling over ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_working_provider_answers_and_nothing_else_is_asked()
    {
        var first = Cloud("openai", "the answer");
        var second = Cloud("anthropic", "unused");

        var (text, outcome) = await Run(first, second);

        Assert.Equal("the answer", text);
        Assert.Empty(outcome!.FellBackFrom);
        Assert.False(second.WasAsked);
    }

    /// <summary>The whole point: one provider down is no longer a dead turn.</summary>
    [Fact]
    public async Task A_provider_that_fails_hands_over_to_the_next()
    {
        var first = Cloud("openai", failsWith: new HttpRequestException("503"));
        var second = Cloud("anthropic", "the answer");

        var (text, outcome) = await Run(first, second);

        Assert.Equal("the answer", text);
        Assert.Equal("anthropic engine", outcome!.Runtime.EngineLabel);
        Assert.Equal(["openai engine"], outcome.FellBackFrom);
    }

    [Fact]
    public async Task It_keeps_going_down_the_chain()
    {
        var (text, outcome) = await Run(
            Cloud("openai", failsWith: new HttpRequestException("503")),
            Cloud("anthropic", failsWith: new HttpRequestException("429")),
            Cloud("gemini", "the answer"));

        Assert.Equal("the answer", text);
        Assert.Equal(2, outcome!.FellBackFrom.Count);
    }

    /// <summary>
    /// When everything is down, the last failure is the one reported — rather than
    /// an empty reply that looks like the model had nothing to say.
    /// </summary>
    [Fact]
    public async Task When_everything_fails_the_last_failure_surfaces()
    {
        var text = new System.Text.StringBuilder();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var chunk in RuntimeFailover.StreamAsync(
                Cloud("openai", failsWith: new HttpRequestException("503")),
                [Cloud("anthropic", failsWith: new HttpRequestException("429"))],
                Ask()))
            {
                text.Append(chunk);
            }
        });
    }

    // ── The three refusals ────────────────────────────────────────────────

    /// <summary>
    /// The rule that matters most. Concierge's claim is that it works on your own
    /// machine; a failover that silently posts the conversation to a cloud provider
    /// would break that during an error, when nobody is looking.
    /// </summary>
    [Fact]
    public void A_local_runtime_never_falls_back_to_a_remote_one()
    {
        var chain = RuntimeFailover.Chain(
            Local("circleai"), [Cloud("openai", "x"), Cloud("anthropic", "y")]);

        var only = Assert.Single(chain);
        Assert.Equal("circleai", only.Id);
    }

    [Fact]
    public async Task A_local_runtime_that_fails_stays_failed()
    {
        var local = Local("circleai", failsWith: new InvalidOperationException("model died"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in RuntimeFailover.StreamAsync(
                local, [Cloud("openai", "would have answered")], Ask()))
            {
            }
        });
    }

    /// <summary>
    /// The other direction is allowed: the conversation was already leaving the
    /// device when the person chose a cloud provider, so trying a different one
    /// gives away nothing that was not already given.
    /// </summary>
    [Fact]
    public void A_remote_runtime_may_fall_back_to_another_remote_one()
        => Assert.Equal(2, RuntimeFailover.Chain(Cloud("openai", "x"), [Cloud("anthropic", "y")]).Count);

    /// <summary>
    /// A model that declines to answer has answered. Trying the next provider until
    /// one agrees would quietly defeat every safety judgement any of them make.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_an_answer_and_ends_the_turn()
    {
        var first = Cloud("openai", "I can't help with that.");
        var second = Cloud("anthropic", "Sure, here is how");

        var (text, _) = await Run(first, second);

        Assert.Equal("I can't help with that.", text);
        Assert.False(second.WasAsked);
    }

    /// <summary>
    /// An empty reply is a poor answer, not a failure. Asking somebody else the same
    /// question is the same shopping-for-a-yes problem wearing different clothes.
    /// </summary>
    [Fact]
    public async Task An_empty_reply_is_not_treated_as_a_failure()
    {
        var second = Cloud("anthropic", "something");

        var (text, _) = await Run(Cloud("openai"), second);

        Assert.Empty(text);
        Assert.False(second.WasAsked);
    }

    /// <summary>
    /// Once text has been shown, the reply is underway. Continuing from a different
    /// model would produce something neither of them said, spliced invisibly.
    /// </summary>
    [Fact]
    public async Task A_stream_that_dies_mid_sentence_keeps_what_it_said_and_stops()
    {
        var first = Cloud("openai", "Half a sen");
        first.FailAfterChunks = 1;
        var second = Cloud("anthropic", "a whole different answer");

        var text = new System.Text.StringBuilder();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var chunk in RuntimeFailover.StreamAsync(first, [second], Ask()))
            {
                text.Append(chunk);
            }
        });

        Assert.Equal("Half a sen", text.ToString());
        Assert.False(second.WasAsked);
    }

    /// <summary>
    /// Somebody pressing stop is not a provider failing, and asking the next one
    /// would be precisely the opposite of what they asked for.
    /// </summary>
    [Fact]
    public async Task Being_stopped_does_not_fall_over_to_anybody()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var second = Cloud("anthropic", "would have answered");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in RuntimeFailover.StreamAsync(
                Cloud("openai", "x"), [second], Ask(), null, cancellation.Token))
            {
            }
        });

        Assert.False(second.WasAsked);
    }

    // ── Who is in the chain ───────────────────────────────────────────────

    [Fact]
    public void A_runtime_that_is_not_ready_is_not_in_the_chain()
    {
        var sleeping = Cloud("anthropic", "x");
        sleeping.Ready = false;

        Assert.Single(RuntimeFailover.Chain(Cloud("openai", "y"), [sleeping]));
    }

    /// <summary>
    /// The registered list includes the chosen runtime, so without this the same
    /// provider would be tried twice and a real outage would look like two.
    /// </summary>
    [Fact]
    public void The_chosen_runtime_is_never_in_the_chain_twice()
    {
        var first = Cloud("openai", "x");

        Assert.Single(RuntimeFailover.Chain(first, [first, Cloud("openai", "same id")]));
    }

    [Fact]
    public void With_nothing_else_registered_the_chain_is_just_the_one()
        => Assert.Single(RuntimeFailover.Chain(Cloud("openai", "x"), []));

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Runtime Cloud(string id, params string[] chunks) => new(id, leaves: true, chunks);

    private static Runtime Local(string id, Exception? failsWith = null)
        => new(id, leaves: false, []) { Failure = failsWith };

    private static Runtime Cloud(string id, Exception failsWith)
        => new(id, leaves: true, []) { Failure = failsWith };

    private sealed class Runtime : IChatRuntime
    {
        private readonly string[] _chunks;

        public Runtime(string id, bool leaves, string[] chunks)
        {
            Id = id;
            LeavesDevice = leaves;
            _chunks = chunks;
        }

        public string Id { get; }
        public string EngineLabel => $"{Id} engine";
        public bool Ready { get; set; } = true;
        public bool IsReady => Ready;
        public string StatusMessage => "ready";
        public bool LeavesDevice { get; }
        public Exception? Failure { get; init; }

        /// <summary>Emit this many chunks, then fail — a stream dying mid-sentence.</summary>
        public int? FailAfterChunks { get; set; }

        public bool WasAsked { get; private set; }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WasAsked = true;

            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is not null)
            {
                await Task.Yield();
                throw Failure;
            }

            var emitted = 0;

            foreach (var chunk in _chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
                emitted++;

                if (FailAfterChunks == emitted)
                {
                    await Task.Yield();
                    throw new HttpRequestException("died mid-sentence");
                }
            }
        }
    }
}
