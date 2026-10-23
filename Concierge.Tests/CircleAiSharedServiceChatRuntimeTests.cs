using Concierge.Ai;
using Concierge.Shared.Chat;
using Concierge.Shared.Devices;
using Concierge.Shared.Tools;
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
        Assert.False(link.Agentic); // No unsupported native action approval/cancellation bridge.
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
        public string? Session, Message;
        public bool Agentic;
        public int Sends;
        public Task<CircleAiServiceChatStatus> ReadChatStatusAsync(CancellationToken token) => Task.FromResult(Status);
        public Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, bool agentic, CancellationToken token)
        { Sends++; Session = sessionId; Message = message; Agentic = agentic; return Task.FromResult(Reply); }
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
        public Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, bool agentic, CancellationToken token)
        {
            Assert.True(Ready);
            Interlocked.Increment(ref Sends);
            return Task.FromResult(new CircleAiServiceChatReply(true, "9:40 AM", null));
        }
    }
}
