using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class PricingServiceTests
{
    [Fact]
    public void Pricing_service_is_registered_in_core_services()
    {
        var services = new ServiceCollection()
            .AddConciergeCore()
            .BuildServiceProvider();

        Assert.NotNull(services.GetRequiredService<IPricingService>());
    }

    [Fact]
    public void Pricing_has_auth0_style_paths_usage_plans_and_addons()
    {
        var pricing = new PricingService().GetSnapshot();

        Assert.Equal(4, pricing.Plans.Count);
        Assert.True(pricing.Paths.Count >= 6);
        Assert.Equal(4, pricing.UsageBands.Count);
        Assert.True(pricing.AddOns.Count >= 5);
        Assert.Contains(pricing.Paths, path => path.Audience == PricingAudience.Startup);
        Assert.Contains(pricing.Paths, path => path.Audience == PricingAudience.Business);
        Assert.Contains(pricing.Paths, path => path.Audience == PricingAudience.Education);
        Assert.Contains(pricing.Paths, path => path.Audience == PricingAudience.Nonprofit);
    }

    [Fact]
    public void Pricing_keeps_generous_free_entry_and_contact_sales_scale()
    {
        var pricing = new PricingService().GetSnapshot();
        var free = pricing.Plans.Single(plan => plan.Kind == PricingPlanKind.Free);
        var scale = pricing.Plans.Single(plan => plan.Kind == PricingPlanKind.Scale);

        Assert.Equal("Free", free.MonthlyPriceLabel);
        Assert.False(free.IsContactSales);
        Assert.Contains(free.Included, item => item.Contains("Local agent workspace", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Contact us", scale.MonthlyPriceLabel);
        Assert.True(scale.IsContactSales);
        Assert.Contains(scale.Included, item => item.Contains("Policy packs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pricing_does_not_pretend_final_amounts_are_decided()
    {
        var pricing = new PricingService().GetSnapshot();
        var paidPlans = pricing.Plans.Where(plan => plan.Kind is PricingPlanKind.Builder or PricingPlanKind.Operator).ToList();

        Assert.All(paidPlans, plan => Assert.Equal("Price not final", plan.MonthlyPriceLabel));
        Assert.Contains("undecided", pricing.CurrencyMode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Addons_keep_expensive_or_optional_complexity_out_of_base_plans()
    {
        var pricing = new PricingService().GetSnapshot();

        Assert.All(pricing.AddOns, addOn => Assert.True(addOn.Optional));
        Assert.Contains(pricing.AddOns, addOn => addOn.Id == "cloud-workers");
        Assert.Contains(pricing.AddOns, addOn => addOn.Id == "compliance-pack");
        Assert.Contains(pricing.AddOns, addOn => addOn.Id == "premium-integrations");
    }
}
