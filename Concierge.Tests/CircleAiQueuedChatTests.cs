using CircleAI.Assistant.Requests;
using CircleAI.Linking;
using Concierge.Shared.Devices;

namespace Concierge.Tests;

public sealed class CircleAiQueuedChatTests
{
    [Theory]
    [InlineData(0, 250)]
    [InlineData(50, 200)]
    [InlineData(250, 0)]
    [InlineData(1000, 0)]
    public void Long_poll_wait_counts_towards_the_poll_rate_limit(int elapsed, int delay)
        => Assert.Equal(TimeSpan.FromMilliseconds(delay),
            CircleAiQueuedChat.PollDelay(TimeSpan.FromMilliseconds(elapsed)));

    [Fact]
    public void Service_long_poll_is_bounded_to_quarter_second_for_faster_progress_observation()
        => Assert.Equal(250, CircleAiQueuedChat.StatusWaitMilliseconds);

    private static LinkRowsReply Reply(AskState state, string text = "", string id = "ask-one")
    {
        var now = DateTimeOffset.UtcNow;
        var ask = new AskRecord(id, "caller", "request", "session", AskLane.Foreground,
            "question", "question", false, false, true, state, 1, text, null,
            state.ToString(), "", "service-selected", 1, now, now, null, null, null!);
        return new(true, [LinkAskRows.Ask(ask, 0, 0, true, false)], null);
    }

    [Fact]
    public void Queue_requires_handshake_and_both_verbs()
    {
        Assert.False(CircleAiQueuedChat.IsSupported(new(false, [], "unknown verb")));
        Assert.False(CircleAiQueuedChat.IsSupported(new(true,
            [LinkAskRows.Hello(2, 60, "boot", 1, ["AskSubmit"])], null)));
        Assert.True(CircleAiQueuedChat.IsSupported(new(true,
            [LinkAskRows.Hello(2, 60, "boot", 1, ["AskSubmit", "AskStatus"])], null)));
    }

    [Fact]
    public async Task Preparation_and_partial_answer_follow_one_accepted_request()
    {
        var calls = new List<LinkVerbRequest>();
        var progress = new List<string>();
        var replies = new Queue<LinkRowsReply>([
            Reply(AskState.WaitingForSetup), Reply(AskState.WaitingForModel),
            Reply(AskState.Running, "part"), Reply(AskState.Succeeded, "Final answer")]);
        Task<LinkRowsReply> Execute(LinkVerbRequest r, CancellationToken ct)
        { calls.Add(r); return Task.FromResult(replies.Dequeue()); }
        var answer = await CircleAiQueuedChat.SendAsync(Execute, "stable-request", "conversation",
            "Exact question", false, progress.Add, default);
        Assert.True(answer.Ok);
        Assert.Equal("Final answer", answer.Text);
        var submitted = Assert.Single(calls, c => c.Verb == LinkVerb.AskSubmit);
        Assert.Equal("stable-request", submitted.Args![LinkAskRows.ArgRequestId]);
        Assert.Equal("conversation", submitted.Args[LinkAskRows.ArgSession]);
        Assert.Equal("Exact question", submitted.Args[LinkAskRows.ArgMessage]);
        Assert.Equal("0", submitted.Args[LinkAskRows.ArgAgentic]);
        Assert.All(calls.Where(c => c.Verb == LinkVerb.AskStatus), c =>
        { Assert.Equal("ask-one", c.Query); Assert.Null(c.Subject); Assert.Equal("0", c.Args![LinkAskRows.ArgSince]); Assert.Equal("250", c.Args[LinkAskRows.ArgWaitMs]); });
        Assert.Equal(4, progress.Count);
    }

    [Theory]
    [InlineData(AskState.Failed)]
    [InlineData(AskState.Cancelled)]
    [InlineData(AskState.Expired)]
    public async Task Terminal_failure_is_never_an_answer_or_a_second_submission(AskState state)
    {
        var calls = 0;
        var answer = await CircleAiQueuedChat.SendAsync((r, ct) =>
        { calls++; return Task.FromResult(Reply(state)); }, "r", "s", "q", false, null, default);
        Assert.False(answer.Ok);
        Assert.Equal(1, calls);
        Assert.Null(answer.Text);
    }

    [Fact]
    public async Task Stop_cancels_only_the_accepted_ask()
    {
        using var stop = new CancellationTokenSource();
        var calls = new List<LinkVerbRequest>();
        Task<LinkRowsReply> Execute(LinkVerbRequest r, CancellationToken ct)
        { calls.Add(r); return Task.FromResult(Reply(AskState.Queued)); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CircleAiQueuedChat.SendAsync(
            Execute, "r", "s", "q", false, _ => stop.Cancel(), stop.Token));
        Assert.Equal(new[] { LinkVerb.AskSubmit, LinkVerb.AskCancel }, calls.Select(c => c.Verb));
        Assert.Equal("ask-one", calls[1].Query);
    }

    [Fact]
    public async Task A_different_request_record_is_rejected()
    {
        var replies = new Queue<LinkRowsReply>([Reply(AskState.Queued), Reply(AskState.Succeeded, "other", "wrong")]);
        var answer = await CircleAiQueuedChat.SendAsync((r, ct) => Task.FromResult(replies.Dequeue()),
            "r", "s", "q", false, null, default);
        Assert.False(answer.Ok);
        Assert.Null(answer.Text);
    }
}
