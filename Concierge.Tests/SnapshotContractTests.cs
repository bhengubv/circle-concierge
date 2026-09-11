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

    /// <summary>
    /// Beyond Code's contents, pinned so a change to them is a decision.
    ///
    /// **It was blind to the field that mattered.** Every one of the eleven capabilities
    /// carried `HardeningStatus.Ready` — six of them for things that did not exist — and the
    /// approved snapshot did not record a status for a single one, because Verify scrubs
    /// default values and `Ready` is the enum's zero. A test whose whole job is noticing a
    /// change could not see the field that was wrong on all eleven rows.
    ///
    /// `BeyondState` has `InTheApp` as its zero, so the same scrubbing now hides only the
    /// claim that is safe to hide — a row that says nothing is a row saying it is here, and
    /// the eight that are not built say so in the file. Worth knowing before adding an enum
    /// to anything a snapshot covers: the default value is the one that goes unrecorded.
    /// </summary>
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
