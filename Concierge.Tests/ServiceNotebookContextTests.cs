using Concierge.Ai;
using Concierge.Shared.Chat;
using Concierge.Shared.Devices;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

public sealed class ServiceNotebookContextTests
{
    [Fact]
    public async Task Selected_notebook_reference_is_sent_in_the_service_user_message()
    {
        var link = new FakeLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        var context = Concierge.Shared.Planning.NotebookContext.ServiceContextStart
            + "Use this excerpt only; report when the answer is missing. {\"Sources\":[{\"Name\":\"Meeting notes\",\"Excerpt\":\"Friday at 10am. Nandi is organiser.\"}]}"
            + Concierge.Shared.Planning.NotebookContext.ServiceContextEnd;
        await foreach (var _ in runtime.StreamAsync([
            new("system", "Private app instructions " + context),
            new("user", "When is the meeting?")])) { }
        Assert.Contains("Friday at 10am", link.Message);
        Assert.Contains("Meeting notes", link.Message);
        Assert.Contains("When is the meeting?", link.Message);
        Assert.Equal("When is the meeting?", link.Question);
        Assert.DoesNotContain("Private app instructions", link.Message);
    }

    private sealed class FakeLink : ICircleAiServiceChatLink
    {
        public string? Message, Question;
        public Task<CircleAiServiceChatStatus> ReadChatStatusAsync(CancellationToken token)
            => Task.FromResult(new CircleAiServiceChatStatus(true, "Ready", "Ready", "service"));
        public Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, string question, bool agentic, CancellationToken token)
        { Message = message; Question = question; return Task.FromResult(new CircleAiServiceChatReply(true, "Friday at 10am.", null)); }
    }
}
