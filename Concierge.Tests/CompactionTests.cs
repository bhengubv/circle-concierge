using System.Runtime.CompilerServices;
using Concierge.Shared.Chat;
using Concierge.Shared.Context;

namespace Concierge.Tests;

/// <summary>
/// What compaction must do (parity features 8 and 9): keep a conversation inside the window
/// by replacing old turns with a summary, while never losing the parts that make the next
/// reply coherent.
/// </summary>
/// <remarks>
/// This is the feature the on-device model depends on most. A 0.6B model has the smallest
/// window in the product, so without compaction a conversation ends after a handful of turns.
/// </remarks>
public sealed class CompactionTests
{
    private readonly ITokenMeter _meter = new HeuristicTokenMeter();

    // ── Leaving things alone ───────────────────────────────────────────

    [Fact]
    public async Task A_short_conversation_is_left_alone()
    {
        var engine = EngineWith("a summary");
        var turns = Conversation(4);

        var result = await engine.CompactIfNeededAsync(turns, budgetTokens: 100_000);

        Assert.False(result.WasCompacted);
        Assert.Equal(turns.Count, result.Turns.Count);
    }

    [Fact]
    public async Task A_short_conversation_is_not_summarised_at_all()
    {
        var summariser = new RecordingRuntime("a summary");
        var engine = new BasicCompactionEngine(_meter, summariser);

        await engine.CompactIfNeededAsync(Conversation(4), budgetTokens: 100_000);

        Assert.Equal(0, summariser.Calls);
    }

    // ── Reducing ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_long_conversation_is_made_shorter()
    {
        var engine = EngineWith("a summary");
        var turns = Conversation(60);

        var result = await engine.CompactIfNeededAsync(turns, budgetTokens: 500);

        Assert.True(result.Turns.Count < turns.Count);
    }

    [Fact]
    public async Task A_long_conversation_is_brought_under_the_budget()
    {
        var engine = EngineWith("a summary");
        var turns = Conversation(60);

        var result = await engine.CompactIfNeededAsync(turns, budgetTokens: 2_000);

        Assert.False(_meter.Measure(result.Turns, 2_000).IsUnderPressure);
    }

    [Fact]
    public async Task Compaction_reports_what_it_reclaimed()
    {
        var engine = EngineWith("a summary");

        var result = await engine.CompactIfNeededAsync(Conversation(60), budgetTokens: 500);

        Assert.True(result.TokensReclaimed > 0);
    }

    // ── What must survive ──────────────────────────────────────────────

    [Fact]
    public async Task The_system_prompt_survives()
    {
        var turns = new List<ChatTurn> { new("system", "You are Bell.") };
        turns.AddRange(Conversation(60));
        var engine = EngineWith("a summary");

        var result = await engine.CompactIfNeededAsync(turns, budgetTokens: 500);

        Assert.Equal("system", result.Turns[0].Role);
        Assert.Contains("Bell", result.Turns[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_most_recent_exchange_survives_word_for_word()
    {
        var turns = Conversation(60);
        turns.Add(new ChatTurn("user", "THE-LAST-THING-I-SAID"));
        var engine = EngineWith("a summary");

        var result = await engine.CompactIfNeededAsync(turns, budgetTokens: 500);

        Assert.Contains(result.Turns, turn => turn.Content == "THE-LAST-THING-I-SAID");
    }

    [Fact]
    public async Task The_summary_of_what_was_dropped_is_kept()
    {
        var engine = EngineWith("THE-SUMMARY-OF-EARLIER");

        var result = await engine.CompactIfNeededAsync(Conversation(60), budgetTokens: 500);

        Assert.Contains(result.Turns, turn => turn.Content.Contains("THE-SUMMARY-OF-EARLIER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_summariser_is_shown_the_turns_being_dropped()
    {
        var summariser = new RecordingRuntime("a summary");
        var engine = new BasicCompactionEngine(_meter, summariser);
        var turns = Conversation(60);
        turns[0] = new ChatTurn("user", "THE-EARLIEST-THING");

        await engine.CompactIfNeededAsync(turns, budgetTokens: 500);

        Assert.Contains("THE-EARLIEST-THING", summariser.LastPrompt, StringComparison.Ordinal);
    }

    // ── Compacting on demand ───────────────────────────────────────────

    [Fact]
    public async Task Compacting_on_demand_reduces_a_conversation_that_is_not_yet_full()
    {
        var engine = EngineWith("a summary");
        var turns = Conversation(60);

        var result = await engine.CompactNowAsync(turns, budgetTokens: 1_000_000);

        Assert.True(result.WasCompacted);
        Assert.True(result.Turns.Count < turns.Count);
    }

    [Fact]
    public async Task Compacting_on_demand_does_nothing_when_there_is_nothing_to_drop()
    {
        var engine = EngineWith("a summary");

        var result = await engine.CompactNowAsync(Conversation(2), budgetTokens: 1_000_000);

        Assert.False(result.WasCompacted);
    }

    // ── Failing safely ─────────────────────────────────────────────────

    [Fact]
    public async Task A_summariser_that_throws_leaves_the_conversation_intact()
    {
        // The model failing mid-summary must not destroy history. Better an over-full
        // conversation than a conversation with a hole in it.
        var engine = new BasicCompactionEngine(_meter, new ThrowingRuntime());
        var turns = Conversation(60);

        var result = await engine.CompactIfNeededAsync(turns, budgetTokens: 500);

        Assert.False(result.WasCompacted);
        Assert.Equal(turns.Count, result.Turns.Count);
    }

    [Fact]
    public async Task A_summariser_that_returns_nothing_leaves_the_conversation_intact()
    {
        var engine = EngineWith(string.Empty);
        var turns = Conversation(60);

        var result = await engine.CompactIfNeededAsync(turns, budgetTokens: 500);

        Assert.False(result.WasCompacted);
        Assert.Equal(turns.Count, result.Turns.Count);
    }

    [Fact]
    public async Task Compacting_twice_is_safe()
    {
        var engine = EngineWith("a summary");

        var once = await engine.CompactIfNeededAsync(Conversation(60), budgetTokens: 500);
        var twice = await engine.CompactIfNeededAsync(once.Turns, budgetTokens: 500);

        Assert.NotEmpty(twice.Turns);
    }

    [Fact]
    public async Task An_empty_conversation_is_handled()
    {
        var engine = EngineWith("a summary");

        var result = await engine.CompactIfNeededAsync([], budgetTokens: 500);

        Assert.False(result.WasCompacted);
        Assert.Empty(result.Turns);
    }

    private BasicCompactionEngine EngineWith(string summary)
        => new(_meter, new RecordingRuntime(summary));

    private static List<ChatTurn> Conversation(int turnCount)
        => Enumerable.Range(0, turnCount)
            .Select(index => new ChatTurn(
                index % 2 == 0 ? "user" : "assistant",
                $"turn {index}: {new string('w', 60)}"))
            .ToList();

    /// <summary>A chat runtime that returns a fixed summary and remembers what it was asked.</summary>
    private sealed class RecordingRuntime(string summary) : IChatRuntime
    {
        public int Calls { get; private set; }
        public string LastPrompt { get; private set; } = string.Empty;

        public string Id => "recording";
        public string EngineLabel => "Recording";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPrompt = string.Join("\n", messages.Select(m => m.Content));
            await Task.CompletedTask;
            if (summary.Length > 0)
            {
                yield return summary;
            }
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
#pragma warning disable CS0162 // Unreachable: the iterator needs a yield to be an iterator.
            yield break;
#pragma warning restore CS0162
        }
    }
}
