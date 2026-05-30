namespace Concierge.Shared;

public enum PricingAudience
{
    Personal,
    Startup,
    Business,
    Enterprise,
    Education,
    Nonprofit
}

public enum PricingPlanKind
{
    Free,
    Builder,
    Operator,
    Scale
}

public sealed record PricingUsageBand(
    string Id,
    string Name,
    string Summary,
    int IncludedMonthlyRuns,
    int IncludedTeamSeats,
    bool RequiresContact);

public sealed record PricingPlan(
    string Id,
    string Name,
    PricingPlanKind Kind,
    string AudienceFit,
    string MonthlyPriceLabel,
    string Summary,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Limits,
    string UpgradeTrigger,
    bool IsContactSales);

public sealed record PricingAddOn(
    string Id,
    string Name,
    string Summary,
    string BillingModel,
    bool Optional);

public sealed record PricingPath(
    PricingAudience Audience,
    string Label,
    string Summary,
    IReadOnlyList<string> RecommendedPlanIds);

public sealed record PricingSnapshot(
    string Positioning,
    string CurrencyMode,
    IReadOnlyList<PricingUsageBand> UsageBands,
    IReadOnlyList<PricingPath> Paths,
    IReadOnlyList<PricingPlan> Plans,
    IReadOnlyList<PricingAddOn> AddOns);
