namespace Concierge.Shared;

public interface IPricingService
{
    PricingSnapshot GetSnapshot();
}

public sealed class PricingService : IPricingService
{
    private static readonly IReadOnlyList<PricingUsageBand> UsageBands =
    [
        new("light", "Light use", "Personal learning, occasional tasks, and local experiments.", 100, 1, false),
        new("active", "Active builder", "Daily work for one person or a very small team.", 1_000, 3, false),
        new("operating", "Operating company", "Multiple business rooms, approvals, integrations, and release evidence.", 10_000, 15, false),
        new("scale", "Scale or regulated", "Large teams, strict governance, private workers, and custom compliance needs.", 0, 0, true)
    ];

    private static readonly IReadOnlyList<PricingPlan> Plans =
    [
        new(
            "free",
            "Free",
            PricingPlanKind.Free,
            "Anyone learning, exploring, or running small local work.",
            "Free",
            "Start safely without needing to understand enterprise software.",
            [
                "Local agent workspace",
                "Core skills catalog",
                "API Room starter workflows",
                "Basic approvals and resource controls",
                "Personal Memory Palace"
            ],
            [
                "Single user",
                "Local-first workloads",
                "Community support"
            ],
            "Upgrade when work becomes daily, recurring, or business-critical.",
            false),
        new(
            "builder",
            "Builder",
            PricingPlanKind.Builder,
            "One-person startups, freelancers, makers, and serious learners.",
            "Price not final",
            "Move from experiments to a real product or operating rhythm.",
            [
                "More monthly agent runs",
                "Persistent workspaces and evidence",
                "Release cockpit",
                "Business rooms",
                "Provider and API integration setup"
            ],
            [
                "Small team use",
                "Standard automation limits",
                "Owner-managed credentials"
            ],
            "Upgrade when more people, approvals, or integrations depend on Concierge.",
            false),
        new(
            "operator",
            "Operator",
            PricingPlanKind.Operator,
            "Small businesses and teams running real operations.",
            "Price not final",
            "Coordinate work across product, finance, HR, legal, support, growth, and delivery.",
            [
                "Team seats",
                "Approval workflows",
                "Audit and evidence bundles",
                "GitHub, Xero, social, commerce, and API rooms",
                "Advanced resource control and workload recovery"
            ],
            [
                "Team usage bands",
                "Shared policy defaults",
                "Standard support"
            ],
            "Move to Scale when compliance, private deployment, or custom policy packs are needed.",
            false),
        new(
            "scale",
            "Scale",
            PricingPlanKind.Scale,
            "Established companies, regulated teams, schools, nonprofits, and public-sector programs.",
            "Contact us",
            "Private, governed, and tailored Concierge for serious teams.",
            [
                "Policy packs",
                "Private or cloud workers",
                "SSO and admin controls",
                "Custom compliance evidence",
                "Priority support and onboarding"
            ],
            [
                "Custom limits",
                "Custom deployment",
                "Commercial agreement required"
            ],
            "This tier starts with a conversation because deployment shape matters.",
            true)
    ];

    private static readonly IReadOnlyList<PricingAddOn> AddOns =
    [
        new("cloud-workers", "Cloud workers", "Run larger or longer tasks away from the user's laptop.", "Usage or capacity based", true),
        new("compliance-pack", "Compliance packs", "Prebuilt evidence, policy, approval, and audit templates for regulated work.", "Per workspace or organization", true),
        new("premium-integrations", "Premium integrations", "Advanced connectors for finance, commerce, social, logistics, and private APIs.", "Per connector family", true),
        new("human-support", "Human support", "Setup help, migration assistance, and priority response.", "Support package", true),
        new("education-access", "Education and nonprofit access", "Discounted or sponsored access for schools, learning programs, and nonprofits.", "Application based", true)
    ];

    private static readonly IReadOnlyList<PricingPath> Paths =
    [
        new(PricingAudience.Personal, "Personal", "Learn, experiment, build locally, and keep control of your machine.", ["free", "builder"]),
        new(PricingAudience.Startup, "Startup", "Start with an idea, build the product, and grow into operations.", ["free", "builder", "operator"]),
        new(PricingAudience.Business, "Business", "Run daily work across teams, approvals, integrations, and evidence.", ["operator", "scale"]),
        new(PricingAudience.Enterprise, "Enterprise", "Private governance, policy, compliance, SSO, and deployment choices.", ["scale"]),
        new(PricingAudience.Education, "Education", "Make business and technology learnable without gatekeeping.", ["free", "builder", "scale"]),
        new(PricingAudience.Nonprofit, "Nonprofit", "Help mission-driven teams get operational power without enterprise friction.", ["free", "operator", "scale"])
    ];

    public PricingSnapshot GetSnapshot()
    {
        return new PricingSnapshot(
            "Pricing follows the user's maturity: start free, grow into building, then operating, then scale when governance matters.",
            "Amounts are intentionally undecided; the structure is built so final prices can be changed without changing the product model.",
            UsageBands,
            Paths,
            Plans,
            AddOns);
    }
}
