using Concierge.CodeMode;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Context;
using Concierge.Shared.Design;
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

/// <summary>
/// That building a host's container and resolving from it actually completes.
/// </summary>
/// <remarks>
/// These exist because of a deadlock that reached a commit. Registering code mode created a
/// cycle — ICodeRuntime needs the tool registry, the registry needs every IAgentTool, and
/// run_code is one of them. .NET detects a cycle through constructors and throws; through a
/// factory lambda it cannot see one, so it deadlocked inside ConcurrentDictionary.GetOrAdd.
/// No exception, no log entry, just a request that never returned. Only the chat page touched
/// the tool list, so every other page looked fine.
///
/// Every assertion here is bounded by a timeout, because the failure mode being guarded
/// against is a hang, and a test that hangs proves nothing.
/// </remarks>
public sealed class ContainerResolutionTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"concierge-resolve-{Guid.NewGuid():N}");

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
    public async Task The_tool_registry_resolves_with_code_mode_registered()
    {
        // The exact resolution that deadlocked.
        await WithinBudget(() =>
        {
            using var provider = HostWithCodeMode();
            return provider.GetRequiredService<IAgentToolRegistry>().Tools.Count;
        });
    }

    [Fact]
    public async Task Every_tool_resolves_with_code_mode_registered()
    {
        await WithinBudget(() =>
        {
            using var provider = HostWithCodeMode();
            return provider.GetServices<IAgentTool>().Count();
        });
    }

    [Fact]
    public async Task The_code_runtime_resolves_on_its_own()
    {
        await WithinBudget(() =>
        {
            using var provider = HostWithCodeMode();
            return provider.GetService<Concierge.CodeMode.ICodeRuntime>() is null ? 0 : 1;
        });
    }

    [Fact]
    public async Task Everything_the_chat_page_injects_resolves()
    {
        // The page is where the deadlock surfaced: it is the only one that touches all of
        // these at once.
        await WithinBudget(() =>
        {
            using var provider = HostWithCodeMode();
            using var scope = provider.CreateScope();
            var services = scope.ServiceProvider;

            _ = services.GetRequiredService<IConversationStore>();
            _ = services.GetRequiredService<IAgentToolRegistry>();
            _ = services.GetRequiredService<IToolCallScheduler>();
            _ = services.GetRequiredService<IRepeatToolReminder>();
            _ = services.GetRequiredService<IToolResultPruner>();
            _ = services.GetRequiredService<ICompactionEngine>();
            _ = services.GetRequiredService<ConciergeToolLoopOptions>();
            return 1;
        });
    }

    /// <summary>
    /// Runs the resolution on a worker and fails if it does not finish. A deadlocked container
    /// never returns, so the assertion has to be the clock rather than the result.
    /// </summary>
    private static async Task WithinBudget(Func<int> resolve)
    {
        var work = Task.Run(resolve);
        var finished = await Task.WhenAny(work, Task.Delay(Budget));

        Assert.True(
            ReferenceEquals(finished, work),
            $"Resolution did not complete within {Budget.TotalSeconds:0}s — the container is deadlocked.");

        await work;
    }

    private ServiceProvider HostWithCodeMode()
        => new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeChat(Path.Combine(_dataRoot, "chat.db"))
            .AddConciergeTools()
            .AddConciergeRuntime()
            .AddConciergeState(_dataRoot)
            .AddConciergeCodeMode(AppContext.BaseDirectory)
            .BuildServiceProvider();

    /// <summary>
    /// Everything the desktop head registers, resolved together.
    ///
    /// Written when routines were added, because that is the same shape as the deadlock this
    /// class exists for: `ProductionRoutines` needs the tool registry, the registry is built
    /// from every tool source, and routines publish two of them. It takes a function rather
    /// than the registry for exactly that reason, and this is what says so out loud.
    /// </summary>
    [Fact]
    public async Task The_registry_resolves_with_every_later_tool_source_registered()
    {
        await WithinBudget(() =>
        {
            using var provider = HostWithEverything();
            return provider.GetRequiredService<IAgentToolRegistry>().Tools.Count;
        });
    }

    [Fact]
    public async Task And_a_routine_can_reach_the_registry_while_it_runs()
    {
        await WithinBudget(() =>
        {
            using var provider = HostWithEverything();

            // Asked for late, which is the whole point: this is the call that would have
            // recursed if the registry had been held.
            return provider.GetRequiredService<Concierge.Shared.Tools.ProductionRoutines>().All.Count;
        });
    }

    private ServiceProvider HostWithEverything()
        => new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeChat(Path.Combine(_dataRoot, "chat.db"))
            .AddConciergeTools()
            .AddConciergeRuntime()
            .AddConciergeState(_dataRoot)
            .AddConciergeCodeMode(AppContext.BaseDirectory)
            .AddConciergeMediaLook()
            .AddConciergeTranscription()
            .AddConciergeCodingTools()
            .AddConciergeMusicLibrary()
            .AddConciergeStockFootage()
            .AddConciergeRoutines(_dataRoot)
            .BuildServiceProvider();
}

/// <summary>
/// That a host which registers an interactive approver actually gets one, in front of the
/// audit log.
/// </summary>
/// <remarks>
/// Until this was wired, every host used the fail-closed default and so every write and every
/// command was refused. The model was offered three tools, two of which could only say no.
/// </remarks>
public sealed class ApproverWiringTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"concierge-approver-{Guid.NewGuid():N}");

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
    public void A_host_that_registers_an_interactive_approver_does_not_get_the_fail_closed_one()
    {
        using var provider = HostWithApprover();

        Assert.IsNotType<UnavailableToolApprovalService>(provider.GetRequiredService<IToolApprovalService>());
    }

    [Fact]
    public async Task An_interactive_host_can_actually_grant_a_write()
    {
        // The whole point. Before this, the answer was always no.
        using var provider = HostWithApprover();
        var approver = provider.GetRequiredService<InteractiveToolApprovalService>();
        var approval = provider.GetRequiredService<IToolApprovalService>();

        var asking = approval.RequestAsync(
            new ToolApprovalRequest("write_file", "Write notes.txt", ConciergeToolRisk.High)).AsTask();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (approver.Pending.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        approver.Answer(approver.Pending[0].Id, ToolApprovalDecision.Allowed);

        Assert.Equal(ToolApprovalDecision.Allowed, await asking);
    }

    [Fact]
    public async Task Every_decision_reaches_the_audit_log()
    {
        // A parent reviewing what was allowed depends on the decorator being in the chain,
        // not on each tool remembering to record.
        using var provider = HostWithApprover();
        var approver = provider.GetRequiredService<InteractiveToolApprovalService>();
        var audit = provider.GetRequiredService<IToolApprovalAuditLog>();

        var asking = provider.GetRequiredService<IToolApprovalService>()
            .RequestAsync(new ToolApprovalRequest("run_command", "Run dotnet build", ConciergeToolRisk.High)).AsTask();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (approver.Pending.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        approver.Answer(approver.Pending[0].Id, ToolApprovalDecision.Denied);
        await asking;

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("run_command", entry.ToolName);
        Assert.Equal(ToolApprovalDecision.Denied, entry.Decision);
    }

    private ServiceProvider HostWithApprover()
    {
        var services = new ServiceCollection()
            .AddConciergeCore()
            .AddConciergeChat(Path.Combine(_dataRoot, "chat.db"))
            .AddConciergeState(_dataRoot);

        // The order a host uses: the interactive approver before AddConciergeTools, whose
        // TryAdd would otherwise install the fail-closed default first and win.
        services.AddSingleton<InteractiveToolApprovalService>();
        services.AddSingleton<IToolApprovalService>(sp => new AuditingToolApprovalService(
            sp.GetRequiredService<InteractiveToolApprovalService>(),
            sp.GetRequiredService<IToolApprovalAuditLog>()));

        return services.AddConciergeTools().AddConciergeRuntime().BuildServiceProvider();
    }
}
