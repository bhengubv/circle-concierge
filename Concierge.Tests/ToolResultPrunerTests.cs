using Concierge.Shared.Context;

namespace Concierge.Tests;

/// <summary>
/// What pruning must do (parity feature 10): keep an oversized tool result useful while
/// stopping it from consuming the conversation it is meant to inform.
/// </summary>
/// <remarks>
/// A single <c>grep</c> across a repository can return more text than a 0.6B model's whole
/// window. Pruning happens before the result becomes conversation, so the cost is paid once
/// rather than on every subsequent turn.
/// </remarks>
public sealed class ToolResultPrunerTests
{
    private readonly IToolResultPruner _pruner = new HeadTailToolResultPruner(
        thresholdChars: 200,
        headChars: 80,
        tailChars: 40);

    [Fact]
    public void A_small_result_is_returned_untouched()
    {
        const string output = "three lines\nof perfectly\nreasonable output";

        Assert.Equal(output, _pruner.Prune(output).Text);
    }

    [Fact]
    public void A_small_result_is_not_marked_as_pruned()
    {
        Assert.False(_pruner.Prune("small").WasPruned);
    }

    [Fact]
    public void An_oversized_result_is_brought_under_the_threshold()
    {
        var output = new string('x', 5_000);

        Assert.True(_pruner.Prune(output).Text.Length < 5_000);
    }

    [Fact]
    public void An_oversized_result_is_marked_as_pruned()
    {
        Assert.True(_pruner.Prune(new string('x', 5_000)).WasPruned);
    }

    [Fact]
    public void The_beginning_is_kept()
    {
        var output = "THE-IMPORTANT-BEGINNING" + new string('x', 5_000);

        Assert.Contains("THE-IMPORTANT-BEGINNING", _pruner.Prune(output).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_end_is_kept()
    {
        // A failing build puts its error on the last line. Keeping only the head would
        // discard the one part of the output anybody needs.
        var output = new string('x', 5_000) + "THE-ERROR";

        Assert.Contains("THE-ERROR", _pruner.Prune(output).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_is_told_how_much_was_removed()
    {
        var result = _pruner.Prune(new string('x', 5_000));

        Assert.Contains("4880", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_amount_removed_is_reported_to_the_caller()
    {
        var result = _pruner.Prune(new string('x', 5_000));

        Assert.Equal(4_880, result.RemovedChars);
    }

    [Fact]
    public void Nothing_removed_is_reported_as_nothing()
    {
        Assert.Equal(0, _pruner.Prune("small").RemovedChars);
    }

    [Fact]
    public void An_empty_result_survives_pruning()
    {
        Assert.Equal(string.Empty, _pruner.Prune(string.Empty).Text);
    }

    [Fact]
    public void A_null_result_is_treated_as_empty()
    {
        Assert.Equal(string.Empty, _pruner.Prune(null).Text);
    }

    [Fact]
    public void A_result_exactly_at_the_threshold_is_left_alone()
    {
        var output = new string('x', 200);

        Assert.False(_pruner.Prune(output).WasPruned);
    }

    [Fact]
    public void Pruning_never_returns_more_than_it_was_given()
    {
        // The marker text is inserted, so a badly chosen head/tail could produce output
        // longer than the input for a result only slightly over the threshold.
        var output = new string('x', 201);

        Assert.True(_pruner.Prune(output).Text.Length <= output.Length);
    }
}
