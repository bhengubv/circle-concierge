using Concierge.Ai;
using Concierge.Shared.Chat;
using Concierge.Shared.Devices;
using Concierge.Shared.Tools;
using Concierge.Shared.Planning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concierge.Tests;

public sealed class CircleAiSharedServiceChatRuntimeTests
{
    [Fact]
    public void Service_registration_removes_app_owned_model_loader_and_registry_runtime()
    {
        var services = new ServiceCollection();
        services.AddConciergeAi();
        services.AddSingleton<ICircleAiServiceChatLink>(new FakeLink());
        services.AddConciergeAiSharedServiceChat();
        Assert.DoesNotContain(services, d => d.ImplementationType?.Name == "CircleAiChatRuntimeLoader");
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(CircleAiChatRuntime));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<CircleAiSharedServiceChatRuntime>(provider.GetRequiredService<IChatRuntime>());
        Assert.Single(provider.GetServices<IHostedService>());
    }

    [Theory]
    [InlineData(true, "Ready", ChatRuntimeCapabilityState.Available)]
    [InlineData(true, "TextReady", ChatRuntimeCapabilityState.Available)]
    [InlineData(false, "NeedsSetup", ChatRuntimeCapabilityState.ActionRequired)]
    [InlineData(false, "Unavailable", ChatRuntimeCapabilityState.Unavailable)]
    public async Task Readiness_comes_from_service_not_an_embedded_model(bool ready, string stage, ChatRuntimeCapabilityState state)
    {
        var runtime = new CircleAiSharedServiceChatRuntime(new FakeLink { Status = new(ready, stage, "Service status", "58") });
        await runtime.RefreshAsync(default);
        Assert.Equal(ready, runtime.IsReady);
        Assert.Equal(state, runtime.CapabilityCatalog.Capabilities[0].State);
        Assert.Equal("Circle AI Android service", runtime.EngineLabel);
        Assert.False(runtime.GetSnapshot().RegistryAvailable);
    }

    [Fact]
    public async Task Exact_query_and_stable_conversation_go_to_the_service_without_system_prompt_as_user_text()
    {
        var link = new FakeLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        var id = Guid.NewGuid();
        using var scope = ToolApprovalRunContext.Enter(id, Guid.NewGuid());
        var answer = "";
        await foreach (var delta in runtime.StreamAsync([new("system", "Private client instructions"), new("user", "What is two plus three?")])) answer += delta;
        Assert.Equal("5", answer);
        Assert.Equal(id.ToString("D"), link.Session);
        Assert.Equal("What is two plus three?", link.Message);
        Assert.Equal("What is two plus three?", link.Question);
        Assert.False(link.Agentic); // No unsupported native action approval/cancellation bridge.
    }

    [Fact]
    public async Task Act_freely_is_the_only_mode_that_enables_service_agentic_work()
    {
        var link = new FakeLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid(), ToolPermissionMode.ActFreely);

        await foreach (var _ in runtime.StreamAsync([new("user", "Do the requested work")])) { }

        Assert.True(link.Agentic);
    }

    [Fact]
    public async Task Selected_notebook_context_is_carried_through_the_service_chat_contract()
    {
        var link = new FakeLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        var notebook = NotebookContext.ServiceContextStart
            + "Use this excerpt only; report when the answer is missing. {\"Sources\":[{\"Name\":\"Meeting notes\",\"Excerpt\":\"Friday at 10am. Nandi is organiser.\"}]}"
            + NotebookContext.ServiceContextEnd;
        await foreach (var _ in runtime.StreamAsync([
            new("system", "Private app instructions " + notebook),
            new("user", "When is the meeting?")])) { }
        Assert.Contains("Friday at 10am", link.Message);
        Assert.Contains("Meeting notes", link.Message);
        Assert.Contains("When is the meeting?", link.Message);
        Assert.DoesNotContain("Private app instructions", link.Message);
    }

    [Fact]
    public async Task User_selected_skill_instructions_are_carried_without_private_tool_prompt()
    {
        var link = new FakeLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        var system = "Private app instructions\n"
            + Concierge.Shared.Skills.SelectedSkillPrompt.Wrap("## Active skill: Clear writing\nUse short sentences and define jargon.")
            + "\nPrivate tool catalog: do not forward";
        await foreach (var _ in runtime.StreamAsync([
            new("system", system),
            new("user", "Explain the result.")])) { }
        Assert.Contains("Use short sentences and define jargon.", link.Message);
        Assert.Contains("Explain the result.", link.Message);
        Assert.DoesNotContain("Private app instructions", link.Message);
        Assert.DoesNotContain("Private tool catalog", link.Message);
    }

    [Fact]
    public async Task Circle_Ai_memory_recall_is_added_as_context_before_the_question()
    {
        var link = new FakeLink();
        var knowledge = new FakeKnowledgeLink
        {
            Recall = new(true, ["The user prefers concise answers.", "The project is called Circle OS."])
        };
        var runtime = new CircleAiSharedServiceChatRuntime(link, knowledge);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());

        await foreach (var _ in runtime.StreamAsync([new("user", "How should I explain this project?")])) { }

        Assert.Equal(5, knowledge.RecallLimit);
        Assert.Contains("How should I explain this project?", knowledge.Situation);
        Assert.Contains("The user prefers concise answers.", link.Message);
        Assert.Contains("The project is called Circle OS.", link.Message);
        Assert.Contains("How should I explain this project?", link.Message);
        Assert.Contains("Treat them as prior context", link.Message);
    }

    [Fact]
    public async Task Prior_conversation_turns_are_sent_with_the_follow_up_question()
    {
        var link = new FakeLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());

        await foreach (var _ in runtime.StreamAsync([
            new("user", "The meeting starts at ten."),
            new("assistant", "Understood."),
            new("user", "What time does it start?")])) { }

        Assert.Contains("Person: The meeting starts at ten.", link.Message);
        Assert.Contains("Circle AI: Understood.", link.Message);
        Assert.Contains("What time does it start?", link.Message);
        Assert.Equal("What time does it start?", link.Question);
    }

    [Fact]
    public async Task Explicit_remember_request_uses_service_memory_without_running_a_chat_turn()
    {
        var link = new FakeLink();
        var knowledge = new FakeKnowledgeLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link, knowledge);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        var answer = "";

        await foreach (var delta in runtime.StreamAsync([new("user", "Please remember that my preferred format is bullet points.")])) answer += delta;

        Assert.Equal("my preferred format is bullet points.", knowledge.RememberedFact);
        Assert.Equal(0, link.Sends);
        Assert.Contains("remember", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeating_a_question_still_calls_the_service_and_uses_its_new_reply()
    {
        var link = new FakeLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        await foreach (var _ in runtime.StreamAsync([new("user", "Same question")])) { }
        link.Reply = new(true, "New service answer", null);
        var answer = "";
        await foreach (var delta in runtime.StreamAsync([new("user", "Same question")])) answer += delta;
        Assert.Equal(2, link.Sends);
        Assert.Equal("New service answer", answer);
    }

    [Fact]
    public async Task A_failed_service_reply_is_a_failure_not_a_generated_answer()
    {
        var link = new FakeLink { Reply = new(false, null, "Service could not run the query") };
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        await Assert.ThrowsAsync<ChatRuntimeUnavailableException>(async () => { await foreach (var _ in runtime.StreamAsync([new("user", "Hi")])) { } });
        Assert.False(runtime.IsReady);
        Assert.Equal(ChatRuntimeCapabilityState.Unavailable, runtime.CapabilityCatalog.Capabilities[0].State);
    }

    [Fact]
    public async Task Unready_service_does_not_send_an_inference_request()
    {
        var link = new FakeLink { Status = new(false, "NeedsSetup", "Needs setup", "58") };
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        await Assert.ThrowsAsync<ChatRuntimeUnavailableException>(async () => { await foreach (var _ in runtime.StreamAsync([new("user", "Hi")])) { } });
        Assert.Null(link.Message);
    }

    [Fact]
    public async Task Queue_capable_service_accepts_question_without_claiming_text_ready()
    {
        var link = new FakeLink { Status = new(false, "Unknown", "starting", "60", AcceptsQueuedRequests: true) };
        var runtime = new CircleAiSharedServiceChatRuntime(link);
        await runtime.RefreshAsync(default);
        Assert.False(runtime.IsReady);
        Assert.True(runtime.CanWaitForPreparation);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        var answer = "";
        await foreach (var delta in runtime.StreamAsync([new("user", "What is two plus three?")])) answer += delta;
        Assert.Equal("5", answer);
        Assert.Equal(1, link.Sends);
        Assert.False(runtime.IsReady);
    }

    private sealed class FakeLink : ICircleAiServiceChatLink
    {
        public CircleAiServiceChatStatus Status = new(true, "Ready", "Ready", "58");
        public CircleAiServiceChatReply Reply = new(true, "5", null);
        public string? Session, Message, Question;
        public bool Agentic;
        public int Sends;
        public Task<CircleAiServiceChatStatus> ReadChatStatusAsync(CancellationToken token) => Task.FromResult(Status);
        public Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, string question, bool agentic, CancellationToken token)
        { Sends++; Session = sessionId; Message = message; Question = question; Agentic = agentic; return Task.FromResult(Reply); }
    }

    private sealed class FakeKnowledgeLink : ICircleAiServiceKnowledgeLink
    {
        public CircleAiKnowledgeRead Recall = new(true, []);
        public int RecallLimit;
        public string Situation = "";
        public string? RememberedFact;
        public Task<bool> RequestAccessAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyList<CircleAiServiceSkill>> SearchSkillsAsync(string? query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CircleAiServiceSkill>>([]);
        public Task<CircleAiKnowledgeRead> RecallAsync(string situation, int limit, CancellationToken cancellationToken)
        { RecallLimit = limit; Situation = situation; return Task.FromResult(Recall); }
        public Task<bool> RememberAsync(string fact, string? subject, CancellationToken cancellationToken)
        { RememberedFact = fact; return Task.FromResult(true); }
    }

    [Fact]
    public async Task Question_waits_locally_for_preparation_then_is_sent_once()
    {
        var link = new PreparingLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link) { PreparationPollInterval = TimeSpan.FromMilliseconds(5) };
        await runtime.RefreshAsync(default);
        Assert.False(runtime.IsReady);
        Assert.True(runtime.CanWaitForPreparation);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        var answer = "";
        var observedWaiting = false;
        runtime.StatusChanged += (_, _) => observedWaiting |= runtime.StatusMessage.Contains("Your question is waiting");
        var task = Task.Run(async () => { await foreach (var delta in runtime.StreamAsync([new("user", "When should I leave?")])) answer += delta; });
        await link.PolledWhilePreparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, link.Sends);
        link.Ready = true;
        await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, link.Sends);
        Assert.Equal("9:40 AM", answer);
        Assert.True(observedWaiting);
    }

    [Fact]
    public async Task Cancelling_local_preparation_does_not_send_a_service_question()
    {
        var link = new PreparingLink();
        var runtime = new CircleAiSharedServiceChatRuntime(link) { PreparationPollInterval = TimeSpan.FromMilliseconds(5) };
        await runtime.RefreshAsync(default);
        using var scope = ToolApprovalRunContext.Enter(Guid.NewGuid(), Guid.NewGuid());
        using var stop = new CancellationTokenSource();
        var task = Task.Run(async () => { await foreach (var _ in runtime.StreamAsync([new("user", "Hello")], stop.Token)) { } });
        await link.PolledWhilePreparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, link.Sends);
    }

    private sealed class PreparingLink : ICircleAiServiceChatLink
    {
        public volatile bool Ready;
        public int Sends;
        private int _reads;
        public TaskCompletionSource PolledWhilePreparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CircleAiServiceChatStatus> ReadChatStatusAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _reads) >= 3) PolledWhilePreparing.TrySetResult();
            return Task.FromResult(new CircleAiServiceChatStatus(Ready, Ready ? "Ready" : "Loading", "Loading resources", "60", !Ready, .5));
        }
        public Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, string question, bool agentic, CancellationToken token)
        {
            Assert.True(Ready);
            Interlocked.Increment(ref Sends);
            return Task.FromResult(new CircleAiServiceChatReply(true, "9:40 AM", null));
        }
    }
}
