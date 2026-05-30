using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ProductionReadinessCoverageTests
{
    [Fact]
    public void Ten_open_items_are_tracked_in_code_with_evidence()
    {
        var items = new ProductionReadinessService().GetItems();

        Assert.Equal(11, items.Count);
        Assert.Equal(Enumerable.Range(1, 11), items.Select(item => item.Priority));
        Assert.All(items, item =>
        {
            Assert.Equal(HardeningStatus.Ready, item.Status);
            Assert.False(string.IsNullOrWhiteSpace(item.CodeCoverage));
            Assert.False(string.IsNullOrWhiteSpace(item.Evidence));
        });
    }

    [Fact]
    public async Task App_state_persistence_round_trips_versioned_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "concierge-state-tests", Guid.NewGuid().ToString("N"));
        var service = new AppStatePersistenceService(directory);
        var stateService = new ConciergeStateService();
        var beyondService = new BeyondClaudeService();
        var state = new PersistedConciergeState(1, DateTimeOffset.UtcNow, stateService.GetSnapshot(), beyondService.GetSnapshot());

        await service.SaveAsync(state);
        var loaded = await service.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(state.Snapshot.Providers.Count, loaded.Snapshot.Providers.Count);
        Assert.Equal(state.BeyondClaude.Capabilities.Count, loaded.BeyondClaude.Capabilities.Count);
    }

    [Fact]
    public void Provider_runtime_uses_official_or_partner_access_only()
    {
        var runtime = new ProviderRuntimeService(new ConciergeStateService());
        var plans = runtime.GetHealthPlans();

        Assert.Equal(10, plans.Count);
        Assert.Contains(plans, plan => plan.ProviderId == "meta" && plan.AccessKind == ProviderAccessKind.OfficialPartnerApi);
        Assert.All(plans, plan =>
        {
            Assert.True(plan.RequiresUserApiKey);
            Assert.True(plan.Endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) || plan.AccessKind == ProviderAccessKind.OfficialPartnerApi);
            Assert.Contains("official", plan.SafetyBoundary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Source_control_service_blocks_release_when_git_history_is_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-source-control-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var service = new SourceControlService();

        var missing = service.GetStatus(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var present = service.GetStatus(root);

        Assert.False(missing.IsGitRepository);
        Assert.Equal(HardeningStatus.Blocked, missing.GateStatus);
        Assert.True(present.IsGitRepository);
        Assert.Equal(HardeningStatus.Ready, present.GateStatus);
    }

    [Fact]
    public void Release_packaging_tracks_all_supported_platform_targets()
    {
        var targets = new ReleasePackagingService().GetTargets();

        Assert.Contains(targets, target => target.Id == "web" && target.TargetFramework == "net10.0");
        Assert.Contains(targets, target => target.Id == "windows" && target.TargetFramework.Contains("windows", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => target.Id == "android" && target.TargetFramework.Contains("android", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => target.Id == "ios" && target.TargetFramework.Contains("ios", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => target.Id == "macos" && target.TargetFramework.Contains("maccatalyst", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => target.Id == "linux");
        Assert.All(targets, target => Assert.False(string.IsNullOrWhiteSpace(target.EvidenceNeeded)));
    }

    [Fact]
    public void Core_dependency_registration_covers_readiness_services()
    {
        var services = new ServiceCollection()
            .AddConciergeCore()
            .BuildServiceProvider();

        Assert.NotNull(services.GetService<IProductionReadinessService>());
        Assert.NotNull(services.GetService<IAppStatePersistenceService>());
        Assert.NotNull(services.GetService<IProviderRuntimeService>());
        Assert.NotNull(services.GetService<ISourceControlService>());
        Assert.NotNull(services.GetService<IReleasePackagingService>());
        Assert.NotNull(services.GetService<IScalarApiService>());
    }
}
