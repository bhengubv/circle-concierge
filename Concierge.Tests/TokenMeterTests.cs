using Concierge.Shared.Chat;
using Concierge.Shared.Context;

namespace Concierge.Tests;

/// <summary>
/// What the token meter must do (parity feature 7): say how much of the window a
/// conversation occupies, before a request is sent.
/// </summary>
/// <remarks>
/// On a 0.6B model running on a phone this is the measurement every other context feature
/// depends on. The estimate does not have to match the tokenizer exactly — it has to be
/// monotonic, cheap, and never optimistic.
/// </remarks>
public sealed class TokenMeterTests
{
    private readonly ITokenMeter _meter = new HeuristicTokenMeter();

    [Fact]
    public void Nothing_costs_nothing()
    {
        Assert.Equal(0, _meter.EstimateTokens(string.Empty));
    }

    [Fact]
    public void More_text_costs_more()
    {
        var shorter = _meter.EstimateTokens("hello");
        var longer = _meter.EstimateTokens("hello there, this is a longer sentence entirely");

        Assert.True(longer > shorter);
    }

    [Fact]
    public void The_same_text_always_costs_the_same()
    {
        const string text = "the quick brown fox";

        Assert.Equal(_meter.EstimateTokens(text), _meter.EstimateTokens(text));
    }

    [Fact]
    public void A_short_word_still_costs_at_least_one_token()
    {
        Assert.True(_meter.EstimateTokens("hi") >= 1);
    }

    // ── Measuring a conversation ───────────────────────────────────────

    [Fact]
    public void An_empty_conversation_is_empty()
    {
        var usage = _meter.Measure([], budgetTokens: 1000);

        Assert.Equal(0, usage.Tokens);
        Assert.False(usage.IsUnderPressure);
    }

    [Fact]
    public void A_conversation_costs_at_least_the_sum_of_its_turns()
    {
        var turns = new List<ChatTurn>
        {
            new("user", "hello there friend"),
            new("assistant", "hello to you as well"),
        };

        var usage = _meter.Measure(turns, budgetTokens: 1000);

        var sum = turns.Sum(turn => _meter.EstimateTokens(turn.Content));
        Assert.True(usage.Tokens >= sum);
    }

    [Fact]
    public void Every_turn_carries_an_overhead_so_many_small_turns_are_not_free()
    {
        var oneBigTurn = new List<ChatTurn> { new("user", new string('a', 400)) };
        var manySmallTurns = Enumerable.Range(0, 100)
            .Select(_ => new ChatTurn("user", new string('a', 4)))
            .ToList();

        var big = _meter.Measure(oneBigTurn, budgetTokens: 100_000).Tokens;
        var many = _meter.Measure(manySmallTurns, budgetTokens: 100_000).Tokens;

        Assert.True(many > big);
    }

    // ── Pressure ───────────────────────────────────────────────────────

    [Fact]
    public void A_conversation_well_inside_the_budget_is_not_under_pressure()
    {
        var usage = _meter.Measure([new ChatTurn("user", "short")], budgetTokens: 10_000);

        Assert.False(usage.IsUnderPressure);
    }

    [Fact]
    public void A_conversation_near_the_budget_is_under_pressure()
    {
        var turns = new List<ChatTurn> { new("user", new string('a', 4_000)) };

        var usage = _meter.Measure(turns, budgetTokens: 1_100);

        Assert.True(usage.IsUnderPressure);
    }

    [Fact]
    public void Fullness_is_reported_as_a_fraction_of_the_budget()
    {
        var turns = new List<ChatTurn> { new("user", new string('a', 400)) };

        var usage = _meter.Measure(turns, budgetTokens: 1_000);

        Assert.InRange(usage.Fraction, 0.05, 0.5);
    }

    [Fact]
    public void Fullness_past_the_budget_is_reported_as_over_one()
    {
        var turns = new List<ChatTurn> { new("user", new string('a', 8_000)) };

        var usage = _meter.Measure(turns, budgetTokens: 100);

        Assert.True(usage.Fraction > 1.0);
    }

    [Fact]
    public void A_budget_of_zero_does_not_divide_by_zero()
    {
        var usage = _meter.Measure([new ChatTurn("user", "anything")], budgetTokens: 0);

        Assert.True(double.IsFinite(usage.Fraction));
        Assert.True(usage.IsUnderPressure);
    }

    // ── The estimate is never optimistic ───────────────────────────────

    [Fact]
    public void Text_that_tokenizes_badly_is_not_underestimated()
    {
        // CJK and emoji cost far more tokens per character than Latin text. A meter that
        // assumed four characters per token would let these through and overflow the window.
        var latin = _meter.EstimateTokens(new string('a', 100));
        var cjk = _meter.EstimateTokens(new string('好', 100));

        Assert.True(cjk > latin);
    }
}
