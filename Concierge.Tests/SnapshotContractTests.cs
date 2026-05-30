using Concierge.Shared;

namespace Concierge.Tests;

public sealed class SnapshotContractTests
{
    [Fact]
    public Task Skill_catalog_snapshot_stays_intentional()
    {
        var skills = new SkillCatalogService().GetSkills()
            .Select(skill => new
            {
                skill.Id,
                skill.Name,
                skill.Area,
                skill.Summary
            });

        return Verify(skills);
    }

    [Fact]
    public Task Pricing_snapshot_stays_intentional()
    {
        return Verify(new PricingService().GetSnapshot());
    }

    [Fact]
    public Task Provider_runtime_snapshot_stays_intentional()
    {
        return Verify(new ProviderRuntimeService(new ConciergeStateService()).GetHealthPlans());
    }

    [Fact]
    public Task Scalar_api_room_snapshot_stays_intentional()
    {
        return Verify(new ScalarApiService().GetSnapshot());
    }

    [Fact]
    public Task Beyond_code_snapshot_stays_intentional()
    {
        return Verify(new BeyondClaudeService().GetSnapshot());
    }

    [Fact]
    public Task Release_targets_snapshot_stays_intentional()
    {
        return Verify(new ReleasePackagingService().GetTargets());
    }
}
