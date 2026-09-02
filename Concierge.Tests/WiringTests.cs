using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Context;
using Concierge.Shared.Diagnostics;
using Concierge.Shared.Jobs;
using Concierge.Shared.Planning;
using Concierge.Shared.Presets;
using Concierge.Shared.Safety;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// That a host which calls the registration methods actually gets the behaviour, rather than
/// a container missing a piece nobody notices until a phone in the field behaves oddly.
/// </summary>
/// <remarks>
/// These exist because of a real defect: the CLI never called <c>AddConciergeSafety</c>, so
/// content filtering and parental controls were silently absent from one host while present
/// in the other two. Nothing caught it, because nothing was checking what a host ends up with.
/// </remarks>
public sealed class HostWiringTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"concierge-wiring-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory, and it may never have been created.
        }
    }

    [Fact]
    public void The_runtime_registration_provides_the_tool_loop_parts()
    {
        using var provider = Host();

        Assert.NotNull(provider.GetService<IToolCallScheduler>());
        Assert.NotNull(provider.GetService<IToolResultPruner>());
        Assert.NotNull(provider.GetService<ITokenMeter>());
        Assert.NotNull(provider.GetService<ConciergeToolLoopOptions>());
    }

    [Fact]
    public void The_runtime_registration_provides_compaction()
    {
        // The one the smallest model depends on most.
        using var provider = Host();

        Assert.NotNull(provider.GetService<ICompactionEngine>());
    }

    [Fact]
    public void The_repeat_guard_is_per_conversation_not_shared()
    {
        // Shared state here would carry one runaway loop's count into the next conversation.
        using var provider = Host();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IRepeatToolReminder>(),
            second.ServiceProvider.GetRequiredService<IRepeatToolReminder>());
    }

    [Fact]
    public void The_runtime_registration_provides_the_supporting_services()
    {
        using var provider = Host();

        Assert.NotNull(provider.GetService<IJobRuntime>());
        Assert.NotNull(provider.GetService<IRuntimeDiagnostics>());
        Assert.NotNull(provider.GetService<AgentPresetRegistry>());
        Assert.NotNull(provider.GetService<PlanModeState>());
    }

    [Fact]
    public void The_state_registration_provides_the_durable_stores()
    {
        using var provider = Host();

        Assert.NotNull(provider.GetService<ITodoStore>());
        Assert.NotNull(provider.GetService<IGoalStore>());
        Assert.NotNull(provider.GetService<IFeedbackChannel>());
        Assert.NotNull(provider.GetService<Shared.Attachments.IAttachmentStore>());
        Assert.NotNull(provider.GetService<ISpillStore>());
    }

    [Fact]
    public void Approval_is_wired_and_fails_closed_by_default()
    {
        using var provider = Host();

        var approval = provider.GetService<IToolApprovalService>();

        Assert.NotNull(approval);
        Assert.IsType<UnavailableToolApprovalService>(approval);
    }

    [Fact]
    public void Approval_decisions_have_somewhere_durable_to_go()
    {
        using var provider = Host();

        Assert.NotNull(provider.GetService<IToolApprovalAuditLog>());
    }

    [Fact]
    public void A_host_that_wires_safety_gets_the_filter()
    {
        // The gap that started this: one host had the safety stack and another did not.
        using var provider = Host(withSafety: true);

        Assert.NotNull(provider.GetService<CircleAI.ContentPolicy.IContentFilter>());
    }

    [Fact]
    public void Wiring_safety_puts_the_filter_in_front_of_the_chat_runtime()
    {
        using var provider = Host(withSafety: true);

        Assert.IsType<ContentFilterChatRuntimeDecorator>(provider.GetRequiredService<IChatRuntime>());
    }

    [Fact]
    public void The_tool_loop_budgets_are_settings_not_constants()
    {
        using var provider = Host();
        var options = provider.GetRequiredService<ConciergeToolLoopOptions>();

        options.MaxToolIterations = 12;

        Assert.Equal(12, provider.GetRequiredService<ConciergeToolLoopOptions>().MaxToolIterations);
    }

    private ServiceProvider Host(bool withSafety = false)
    {
        var services = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeChat(Path.Combine(_dataRoot, "chat.db"))
            .AddConciergeTools()
            .AddConciergeRuntime()
            .AddConciergeState(_dataRoot);

        if (withSafety)
        {
            services.AddConciergeSafety();
        }

        return services.BuildServiceProvider();
    }
}
