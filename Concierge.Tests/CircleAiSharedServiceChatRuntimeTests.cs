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

    private sealed class FakeLink : ICircleAiServiceChatLink
    {
        public CircleAiServiceChatStatus Status = new(true, "Ready", "Ready", "58");
        public CircleAiServiceChatReply Reply = new(true, "5", null);
        public string? Session, Message;
        public bool Agentic;
        public Task<CircleAiServiceChatStatus> ReadChatStatusAsync(CancellationToken token) => Task.FromResult(Status);
        public Task<CircleAiServiceChatReply> SendChatAsync(string sessionId, string message, bool agentic, CancellationToken token)
        { Session = sessionId; Message = message; Agentic = agentic; return Task.FromResult(Reply); }
    }
}
