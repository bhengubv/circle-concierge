namespace Concierge.Shared;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public interface IConciergeStateService
{
    ConciergeSnapshot GetSnapshot();

    IReadOnlyList<SkillInfo> GetSkills(string? area = null);

    IReadOnlyList<ProductionTask> GetProductionTasks();

    IReadOnlyList<DiagnosticCheck> GetDiagnostics();
}

public sealed class ConciergeStateService : IConciergeStateService
{
    private readonly ISkillCatalogService _skillCatalog;

    public ConciergeStateService()
        : this(new SkillCatalogService())
    {
    }

    public ConciergeStateService(ISkillCatalogService skillCatalog)
    {
        _skillCatalog = skillCatalog;
    }

    private static readonly IReadOnlyList<ProviderInfo> Providers =
    [
        new("openai", "OpenAI", "https://api.openai.com", "GPT"),
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com", "Gemini"),
        new("anthropic", "Anthropic Claude", "https://api.anthropic.com", "Claude"),
        new("moonshot", "Moonshot / Kimi", "https://api.moonshot.ai", "Kimi"),
        new("zai", "Z.ai / GLM", "https://open.bigmodel.cn", "GLM"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "DeepSeek"),
        new("qwen", "Alibaba / Qwen", "https://dashscope.aliyuncs.com", "Qwen"),
        new("xai", "xAI / Grok", "https://api.x.ai", "Grok"),
        new("meta", "Meta / Llama", "official partner APIs", "Llama", ProviderAccessKind.OfficialPartnerApi),
        new("mistral", "Mistral", "https://api.mistral.ai", "Mistral")
    ];

    private static readonly IReadOnlyList<ProductionTask> Tasks =
    [
        Task(1, "Real Git and release support", "Source control, diffs, branches, commits, release tags, and rollback checkpoints."),
        Task(2, "Command execution hardening", "Allowlisted commands, no shell fallback, timeouts, cleanup, and secret redaction."),
        Task(3, "File sandbox hardening", "Trusted roots, deny paths, generated zones, secret files, and symlink protection."),
        Task(4, "MCP hardening", "Live transport guarded by approvals, disabled-server blocks, and audited invocations."),
        Task(5, "Provider runtime hardening", "Health checks, retries, rate limits, model metadata, and usage visibility."),
        Task(6, "Secrets layer completion", "Portable encryption, recovery bundles, key rotation, and biometric-ready unlock."),
        Task(7, "Durable workload engine", "Long work survives UI disconnects and cleans up owned processes."),
        Task(8, "Audit and evidence export", "Durable proof trail for prompts, commands, approvals, MCP, and release checks."),
        Task(9, "Integration test harnesses", "Fake providers, MCP, GitHub, webhooks, timeout, and rate-limit tests."),
        Task(10, "Browser and mobile QA automation", "Desktop, tablet, mobile, dark/light screenshots, overflow, and accessibility checks."),
        Task(11, "Packaging automation", "Artifact discovery, checksums, publish plans, and platform signing gates."),
        Task(12, "Production diagnostics", "Startup, dependency, portability, and redacted failure checks."),
        Task(13, "CLI production parity", "JSON mode, CI exit codes, diagnostics, preflight, evidence, and non-interactive flows."),
        Task(14, "Migration and portability tests", "No hidden machine paths, transfer manifest, excluded secrets, and clean rebuild."),
        Task(15, "Performance and resource tests", "Concurrency caps, cancellation, cleanup, resource warnings, and emergency stop.")
    ];

    private static readonly IReadOnlyList<ProductionGate> Gates =
    [
        new("build-tests", "Build and tests", HardeningStatus.Ready, ".NET 10 solution builds and tests are expected to pass.", "Recreated solution and test project."),
        new("agent-harness", "Agent harness", HardeningStatus.Ready, "Core agent safety surfaces are represented in the rebuilt app.", "Production tasklist and diagnostics."),
        new("source-control", "Source control", HardeningStatus.Blocked, "This rebuilt folder still needs Git history before release tagging.", "No .git repository detected by the app yet."),
        new("owner-credentials", "Signing and store credentials", HardeningStatus.NeedsOwner, "Owner-controlled credentials are not stored in source.", "Release cockpit tracks them without exposing secrets.")
    ];

    public ConciergeSnapshot GetSnapshot()
    {
        var skills = _skillCatalog.GetSkills();

        return new ConciergeSnapshot(
            Providers,
            skills,
            Tasks,
            Gates,
            // Nothing. Approvals are not state this service knows about: they
            // are tool calls blocked on a person, held by
            // InteractiveToolApprovalService until answered.
            //
            // Two entries used to live here, and every surface that read them
            // showed a queue of two that had never been requested — including
            // Allow buttons that answered nothing. Kept empty rather than
            // removed from the record so the shape survives for a real source
            // later; see TASKS.md for the watch, which has no tool loop.
            [],
            [
                new("workload-001", "Rebuild Concierge shell", "Running", "Verified", DateTimeOffset.UtcNow.AddMinutes(-3)),
                new("workload-002", "Browser QA baseline", "Ready", "Pending", DateTimeOffset.UtcNow.AddMinutes(-1))
            ],
            new ResourceControlSettings(2, 1, 30, "Balanced", true, true),
            [
                new("dotnet", ".NET 10 runtime", HardeningStatus.Ready, Environment.Version.ToString()),
                new("providers", "Official provider adapters", HardeningStatus.Ready, $"{Providers.Count} provider families configured."),
                new("skills", "Skill catalog", HardeningStatus.Ready, $"{skills.Count} curated skills bundled into the solution.")
            ]);
    }

    public IReadOnlyList<SkillInfo> GetSkills(string? area = null)
    {
        var skills = _skillCatalog.GetSkills();
        return string.IsNullOrWhiteSpace(area)
            ? skills
            : skills.Where(skill => string.Equals(skill.Area, area, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public IReadOnlyList<ProductionTask> GetProductionTasks()
    {
        return Tasks;
    }

    public IReadOnlyList<DiagnosticCheck> GetDiagnostics()
    {
        return GetSnapshot().Diagnostics;
    }

    private static ProductionTask Task(int priority, string title, string summary)
    {
        return new ProductionTask(priority, title, HardeningStatus.Ready, summary);
    }
}

public static class ConciergeServiceCollectionExtensions
{
    public static IServiceCollection AddConciergeCore(this IServiceCollection services)
    {
        // Skill catalog: composes the embedded bundle (always present) with
        // whatever ISkillSources have been registered (filesystem at dev time,
        // bundled-asset readers on mobile, remote indices later).
        services.AddSingleton<ISkillCatalogService>(sp =>
            new SkillCatalogService(sp.GetServices<Concierge.Shared.Skills.ISkillSource>()));
        // Activation runtime: returns the loaded SKILL.md body for any id, plus
        // composes multi-skill system-prompt addenda for stacked activations.
        services.AddSingleton<Concierge.Shared.Skills.ISkillRuntime, Concierge.Shared.Skills.SkillRuntime>();

        services.AddSingleton<IConciergeStateService, ConciergeStateService>();
        services.AddConciergeIntegrationDefaults();
        services.AddSingleton<IAgentHarnessService>(sp =>
            new AgentHarnessService(
                AgentHarnessService.LocateDefaultWorkspaceRoot(),
                sp.GetService<IAgentRunLogPublisher>()));
        // Where you were: the thread, the skills that were on, and anything
        // typed and not sent. Local file, beside the drafts.
        // Folders of your own skills. Registered here rather than only in
        // AddLocalSkillSources because the Skills panel offers to add one
        // whether or not any local source was configured.
        services.TryAddSingleton(_ => new Concierge.Shared.Skills.UserSkillFolders());

        services.AddSingleton<Concierge.Shared.Session.ISessionState>(
            _ => new Concierge.Shared.Session.FileSessionState());
        services.AddSingleton<IBeyondClaudeService, BeyondClaudeService>();
        services.AddSingleton<IProductionReadinessService, ProductionReadinessService>();
        services.AddSingleton<IAppStatePersistenceService, AppStatePersistenceService>();
        services.AddSingleton<IProviderRuntimeService, ProviderRuntimeService>();
        services.AddSingleton<ISourceControlService, SourceControlService>();
        services.AddSingleton<IReleasePackagingService, ReleasePackagingService>();
        services.AddSingleton<IScalarApiService, ScalarApiService>();
        services.AddSingleton<IPricingService, PricingService>();
        return services;
    }
}
