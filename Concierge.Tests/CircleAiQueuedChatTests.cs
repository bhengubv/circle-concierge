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

    [Fact]
    public async Task State_only_progress_is_explained_in_person_facing_language()
    {
        var replies = new Queue<LinkRowsReply>([
            Reply(AskState.Queued), Reply(AskState.WaitingForSetup),
            Reply(AskState.WaitingForModel), Reply(AskState.WaitingForPermission, permission: "Microphone"),
            Reply(AskState.Running), Reply(AskState.Paused),
            Reply(AskState.Succeeded, "Done")]);
        var progress = new List<string>();

        var answer = await CircleAiQueuedChat.SendAsync((_, _) => Task.FromResult(replies.Dequeue()),
            "request", "conversation", "question", "question", false, progress.Add, default);

        Assert.True(answer.Ok);
        Assert.Equal("Done", answer.Text);
        Assert.Equal([
            "Circle AI has your question and is waiting to start.",
            "Circle AI is preparing to answer.",
            "Circle AI is preparing a model.",
            "Circle AI is waiting for permission: Microphone.",
            "Circle AI is processing your question.",
            "Circle AI paused this question and will resume it when the device allows.",
            "Circle AI has finished the answer."
        ], progress);
    }

    private static LinkRowsReply Reply(AskState state, string text = "", string id = "ask-one", int textFrom = 0,
        string permission = "")
    {
        var now = DateTimeOffset.UtcNow;
        var ask = new AskRecord(id, "caller", "request", "session", AskLane.Foreground,
            "question", "question", false, false, true, state, 1, text, null,
            state.ToString(), permission, "service-selected", 1, now, now, null, null, null!);
        return new(true, [LinkAskRows.Ask(ask, 0, textFrom, true, false)], null);
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
            Reply(AskState.Running, "part"), Reply(AskState.Succeeded, "partFinal answer", textFrom: 4)]);
        Task<LinkRowsReply> Execute(LinkVerbRequest r, CancellationToken ct)
        { calls.Add(r); return Task.FromResult(replies.Dequeue()); }
        var answer = await CircleAiQueuedChat.SendAsync(Execute, "stable-request", "conversation",
            "Earlier answer: weather. Current message: Exact question", "Exact question", false, progress.Add, default);
        Assert.True(answer.Ok);
        Assert.Equal("partFinal answer", answer.Text);
        var submitted = Assert.Single(calls, c => c.Verb == LinkVerb.AskSubmit);
        Assert.Equal("stable-request", submitted.Args![LinkAskRows.ArgRequestId]);
        Assert.Equal("conversation", submitted.Args[LinkAskRows.ArgSession]);
        Assert.Equal("Earlier answer: weather. Current message: Exact question", submitted.Args[LinkAskRows.ArgMessage]);
        Assert.Equal("Exact question", submitted.Args[LinkAskRows.ArgQuestion]);
        Assert.Equal("0", submitted.Args[LinkAskRows.ArgAgentic]);
        var statusCalls = calls.Where(c => c.Verb == LinkVerb.AskStatus).ToArray();
        Assert.Equal(["0", "0", "4"], statusCalls.Select(c => c.Args![LinkAskRows.ArgSince]));
        Assert.All(statusCalls, c =>
        { Assert.Equal("ask-one", c.Query); Assert.Null(c.Subject); Assert.Equal("250", c.Args![LinkAskRows.ArgWaitMs]); });
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
        { calls++; return Task.FromResult(Reply(state)); }, "r", "s", "q", "q", false, null, default);
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
            Execute, "r", "s", "q", "q", false, _ => stop.Cancel(), stop.Token));
        Assert.Equal(new[] { LinkVerb.AskSubmit, LinkVerb.AskCancel }, calls.Select(c => c.Verb));
        Assert.Equal("ask-one", calls[1].Query);
    }

    [Fact]
    public async Task A_different_request_record_is_rejected()
    {
        var replies = new Queue<LinkRowsReply>([Reply(AskState.Queued), Reply(AskState.Succeeded, "other", "wrong")]);
        var answer = await CircleAiQueuedChat.SendAsync((r, ct) => Task.FromResult(replies.Dequeue()),
            "r", "s", "q", "q", false, null, default);
        Assert.False(answer.Ok);
        Assert.Null(answer.Text);
    }
}
