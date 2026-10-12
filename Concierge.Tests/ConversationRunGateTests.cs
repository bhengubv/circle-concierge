using Concierge.Shared.Chat;

namespace Concierge.Tests;

public sealed class ConversationRunGateTests : IDisposable
{
    private readonly string _locks = Path.Combine(Path.GetTempPath(), $"conversation-gate-{Guid.NewGuid():N}");

    [Fact]
    public async Task Turns_for_one_conversation_wait_while_another_conversation_can_run()
    {
        var gate = new ConversationRunGate();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        using var first = await gate.EnterAsync(firstId);

        var queued = gate.EnterAsync(firstId).AsTask();
        using var independent = await gate.EnterAsync(secondId).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(queued.IsCompleted);
        first.Dispose();
        using var next = await queued.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelling_a_waiter_does_not_leave_the_conversation_gate_locked()
    {
        var gate = new ConversationRunGate();
        var conversationId = Guid.NewGuid();
        using var current = await gate.EnterAsync(conversationId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await gate.EnterAsync(conversationId, cancellation.Token));

        current.Dispose();
        using var next = await gate.EnterAsync(conversationId).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Separate_gate_instances_serialize_one_conversation_but_not_other_conversations()
    {
        var firstGate = new ConversationRunGate(_locks);
        var secondGate = new ConversationRunGate(_locks);
        var conversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        using var first = await firstGate.EnterAsync(conversationId);

        var queued = secondGate.EnterAsync(conversationId).AsTask();
        using var independent = await secondGate.EnterAsync(otherConversationId).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(queued.IsCompleted);
        first.Dispose();
        using var next = await queued.WaitAsync(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (Directory.Exists(_locks))
            Directory.Delete(_locks, recursive: true);
    }
}
