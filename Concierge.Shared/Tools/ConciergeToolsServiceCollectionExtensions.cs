using Concierge.Shared.Context;
using Concierge.Shared.Diagnostics;
using Concierge.Shared.Jobs;
using Concierge.Shared.Planning;
using Concierge.Shared.Presets;
using Concierge.Shared.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Tools;

public static class ConciergeToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent-tool registry plus the harness-backed read/write/run tools so the
    /// chat flow can offer them to the LLM. Additional tools (MCP bridges, custom skills)
    /// register themselves the same way — anything implementing <see cref="IAgentTool"/>
    /// gets picked up by <see cref="AgentToolRegistry"/>.
    /// </summary>
    public static IServiceCollection AddConciergeTools(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        // Fail closed by default: with no host-supplied approver, write and run refuse.
        // A host wires its own surface (MAUI dialog, web prompt) over this registration.
        services.TryAddSingleton<IToolApprovalService>(_ => UnavailableToolApprovalService.Instance);
        services.AddSingleton<IAgentTool, AgentHarnessReadTool>();
        services.AddSingleton<IAgentTool, AgentHarnessWriteTool>();
        services.AddSingleton<IAgentTool, AgentHarnessRunTool>();

        // Notebooks. read_file and write_file already reach a .ipynb, which is
        // the problem rather than the answer: reading one that way spends the
        // context window on base64 outputs, and writing one that way means the
        // whole document coming back from the model's memory. These two do the
        // reading and the surgery; nothing here runs a kernel.
        services.AddSingleton<IAgentTool, NotebookReadTool>();
        services.AddSingleton<IAgentTool, NotebookEditTool>();

        // Reaching the network. Both ask before they run — Concierge's claim is
        // that it works on your own device, so a turn that leaves it is a turn
        // somebody should have agreed to. Search additionally needs a provider,
        // and reports plainly when none is configured rather than failing per
        // call; the host replaces UnconfiguredWebSearch once a key is saved.
        services.TryAddSingleton<Concierge.Shared.Web.IWebAccess>(_ => new Concierge.Shared.Web.HttpWebAccess());
        services.TryAddSingleton<Concierge.Shared.Web.IWebSearch>(_ => new Concierge.Shared.Web.UnconfiguredWebSearch());
        services.AddSingleton<IAgentTool, WebFetchTool>();
        services.AddSingleton<IAgentTool, WebSearchTool>();

        return services;
    }

    /// <summary>
    /// Registers everything the tool loop and the context budget need: the scheduler, the
    /// repeat guard, metering, pruning, compaction, timeouts, jobs, presets and diagnostics.
    /// </summary>
    /// <remarks>
    /// A host that calls this gets the behaviour the loop was built for. Without it the loop
    /// still runs, but nothing measures the context, nothing trims a large result, and a
    /// model going in circles is not told.
    /// </remarks>
    public static IServiceCollection AddConciergeRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<ConciergeToolLoopOptions>();

        services.TryAddSingleton<IToolCallScheduler>(provider =>
            new ToolCallScheduler(provider.GetRequiredService<IAgentToolRegistry>()));

        // Per-conversation state, so one runaway loop is not remembered against the next.
        services.TryAddScoped<IRepeatToolReminder>(_ => new RepeatToolReminder());

        services.TryAddSingleton<ITokenMeter, HeuristicTokenMeter>();

        services.TryAddSingleton<IToolResultPruner>(provider =>
        {
            var options = provider.GetRequiredService<ConciergeToolLoopOptions>();
            return new HeadTailToolResultPruner(options.ToolResultThresholdChars);
        });

        services.TryAddSingleton<ICompactionEngine>(provider => new BasicCompactionEngine(
            provider.GetRequiredService<ITokenMeter>(),
            provider.GetRequiredService<Chat.IChatRuntime>()));

        services.TryAddSingleton<IToolTimeoutPolicy>(provider =>
            new FixedToolTimeoutPolicy(provider.GetRequiredService<ConciergeToolLoopOptions>().ToolTimeout));

        services.TryAddSingleton<IJobRuntime, InMemoryJobRuntime>();
        services.TryAddSingleton<IRetryPolicy>(_ => new BoundedRetryPolicy());
        services.TryAddSingleton<IRuntimeDiagnostics, RuntimeDiagnostics>();
        services.TryAddSingleton(_ => AgentPresetRegistry.BuiltIn);
        services.TryAddSingleton<PlanModeState>();

        return services;
    }

    /// <summary>
    /// Registers the durable stores that outlive a conversation: the todo list, goals,
    /// scheduled work, feedback and attachments.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="dataRoot">
    /// Where these live. Each host passes its own — app-scoped storage on a phone, a per-user
    /// folder on the web — rather than anything guessing from the process's directory.
    /// </param>
    public static IServiceCollection AddConciergeState(this IServiceCollection services, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        services.TryAddSingleton<ITodoStore>(_ => new FileTodoStore(Path.Combine(dataRoot, "todo.json")));
        services.TryAddSingleton<IGoalStore>(_ => new FileGoalStore(Path.Combine(dataRoot, "goals.json")));
        services.TryAddSingleton<Settings.IScheduledTaskStore>(_ =>
            new Settings.FileScheduledTaskStore(Path.Combine(dataRoot, "schedule.json")));
        services.TryAddSingleton<IFeedbackChannel>(_ =>
            new FileFeedbackChannel(Path.Combine(dataRoot, "feedback.jsonl")));
        services.TryAddSingleton<Attachments.IAttachmentStore>(_ =>
            new Attachments.FileAttachmentStore(Path.Combine(dataRoot, "attachments")));
        services.TryAddSingleton<ISpillStore>(_ =>
            new FileSpillStore(Path.Combine(dataRoot, "spill")));
        services.TryAddSingleton<IToolApprovalAuditLog>(_ =>
            new ToolApprovalAuditLog(Path.Combine(dataRoot, "approvals.jsonl")));

        return services;
    }
}
