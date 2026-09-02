using Concierge.Shared;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What an interactive approver must do: hold a tool call until a person answers, and answer
/// for them when nobody can.
/// </summary>
/// <remarks>
/// Until this existed, every host registered <c>UnavailableToolApprovalService</c> and so
/// every write and every command was refused — the tools were offered to the model and could
/// only ever say no. This is the piece that makes them real.
/// </remarks>
public sealed class InteractiveToolApprovalTests
{
    private static ToolApprovalRequest AnyRequest() =>
        new("write_file", "Write notes.txt", ConciergeToolRisk.High, "- old\n+ new");

    [Fact]
    public async Task A_request_waits_for_an_answer()
    {
        using var approver = new InteractiveToolApprovalService();

        var asking = approver.RequestAsync(AnyRequest()).AsTask();

        Assert.False(asking.IsCompleted);
    }

    [Fact]
    public async Task A_waiting_request_is_visible_to_the_surface()
    {
        // The UI has to know there is something to show.
        using var approver = new InteractiveToolApprovalService();
        _ = approver.RequestAsync(AnyRequest()).AsTask();

        await WaitUntil(() => approver.Pending.Count == 1);

        Assert.Equal("write_file", approver.Pending[0].Request.ToolName);
    }

    [Fact]
    public async Task Allowing_completes_the_call()
    {
        using var approver = new InteractiveToolApprovalService();
        var asking = approver.RequestAsync(AnyRequest()).AsTask();
        await WaitUntil(() => approver.Pending.Count == 1);

        approver.Answer(approver.Pending[0].Id, ToolApprovalDecision.Allowed);

        Assert.Equal(ToolApprovalDecision.Allowed, await asking);
    }

    [Fact]
    public async Task Denying_completes_the_call()
    {
        using var approver = new InteractiveToolApprovalService();
        var asking = approver.RequestAsync(AnyRequest()).AsTask();
        await WaitUntil(() => approver.Pending.Count == 1);

        approver.Answer(approver.Pending[0].Id, ToolApprovalDecision.Denied);

        Assert.Equal(ToolApprovalDecision.Denied, await asking);
    }

    [Fact]
    public async Task An_answered_request_stops_being_pending()
    {
        using var approver = new InteractiveToolApprovalService();
        var asking = approver.RequestAsync(AnyRequest()).AsTask();
        await WaitUntil(() => approver.Pending.Count == 1);

        approver.Answer(approver.Pending[0].Id, ToolApprovalDecision.Allowed);
        await asking;

        Assert.Empty(approver.Pending);
    }

    [Fact]
    public async Task The_surface_is_told_when_something_needs_answering()
    {
        using var approver = new InteractiveToolApprovalService();
        var raised = 0;
        approver.PendingChanged += (_, _) => Interlocked.Increment(ref raised);

        _ = approver.RequestAsync(AnyRequest()).AsTask();
        await WaitUntil(() => raised > 0);

        Assert.True(raised > 0);
    }

    [Fact]
    public async Task Several_requests_queue_rather_than_overwrite()
    {
        using var approver = new InteractiveToolApprovalService();
        _ = approver.RequestAsync(AnyRequest()).AsTask();
        _ = approver.RequestAsync(new ToolApprovalRequest("run_command", "Run dotnet build", ConciergeToolRisk.High)).AsTask();

        await WaitUntil(() => approver.Pending.Count == 2);

        Assert.Equal(["write_file", "run_command"], approver.Pending.Select(p => p.Request.ToolName));
    }

    [Fact]
    public async Task Answering_one_leaves_the_others_waiting()
    {
        using var approver = new InteractiveToolApprovalService();
        var first = approver.RequestAsync(AnyRequest()).AsTask();
        var second = approver.RequestAsync(new ToolApprovalRequest("run_command", "Run", ConciergeToolRisk.High)).AsTask();
        await WaitUntil(() => approver.Pending.Count == 2);

        approver.Answer(approver.Pending[0].Id, ToolApprovalDecision.Allowed);
        await first;

        Assert.False(second.IsCompleted);
    }

    [Fact]
    public async Task Nobody_answering_is_a_refusal_not_a_wait_forever()
    {
        // A person who has put the phone down must not leave a tool call pending until the
        // app is killed. Silence is a no.
        using var approver = new InteractiveToolApprovalService(TimeSpan.FromMilliseconds(300));

        Assert.Equal(ToolApprovalDecision.Unavailable, await approver.RequestAsync(AnyRequest()));
    }

    [Fact]
    public async Task A_timed_out_request_stops_being_pending()
    {
        using var approver = new InteractiveToolApprovalService(TimeSpan.FromMilliseconds(300));

        await approver.RequestAsync(AnyRequest());

        Assert.Empty(approver.Pending);
    }

    [Fact]
    public async Task Cancelling_the_turn_ends_the_wait()
    {
        using var approver = new InteractiveToolApprovalService();
        using var cancellation = new CancellationTokenSource();
        var asking = approver.RequestAsync(AnyRequest(), cancellation.Token).AsTask();
        await WaitUntil(() => approver.Pending.Count == 1);

        await cancellation.CancelAsync();

        Assert.Equal(ToolApprovalDecision.Unavailable, await asking);
    }

    [Fact]
    public async Task Shutting_down_refuses_everything_still_waiting()
    {
        // Closing the app must not leave a caller blocked on an answer that will never come.
        var approver = new InteractiveToolApprovalService();
        var asking = approver.RequestAsync(AnyRequest()).AsTask();
        await WaitUntil(() => approver.Pending.Count == 1);

        approver.Dispose();

        Assert.Equal(ToolApprovalDecision.Unavailable, await asking);
    }

    [Fact]
    public void Answering_something_that_is_not_waiting_is_harmless()
    {
        using var approver = new InteractiveToolApprovalService();

        approver.Answer(Guid.NewGuid(), ToolApprovalDecision.Allowed);
    }

    [Fact]
    public async Task What_the_person_is_shown_survives_the_queue()
    {
        using var approver = new InteractiveToolApprovalService();
        _ = approver.RequestAsync(AnyRequest()).AsTask();
        await WaitUntil(() => approver.Pending.Count == 1);

        var pending = approver.Pending[0];

        Assert.Contains("notes.txt", pending.Request.Summary, StringComparison.Ordinal);
        Assert.Contains("+ new", pending.Request.Detail!, StringComparison.Ordinal);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition never became true");
    }
}
