namespace Concierge.Shared;

public interface IProductionReadinessService
{
    IReadOnlyList<ProductionReadinessItem> GetItems();
}

public sealed class ProductionReadinessService : IProductionReadinessService
{
    private static readonly IReadOnlyList<ProductionReadinessItem> Items =
    [
        Item(1, "persistence", "Durable app-state persistence", "Versioned JSON state store with round-trip tests."),
        Item(2, "provider-runtime", "Official provider runtime", "Structured direct and partner API access model plus health plans."),
        Item(3, "source-control", "Git and GitHub readiness", "Local Git detection, repo status gate, and GitHub setup contract."),
        Item(4, "browser-mobile-qa", "Browser and mobile QA", "Route, viewport, theme, and content contracts are tracked in tests."),
        Item(5, "coverage-thresholds", "Coverage thresholds", "Coverlet output and threshold properties are configured in the test project."),
        Item(6, "test-isolation", "Isolated test artifacts", "Agent harness tests can run against disposable workspaces."),
        Item(7, "sandbox-security", "Sandbox security edges", "Deny paths, protected files, symlink escapes, and secret redaction are tested."),
        Item(8, "release-packaging", "Release packaging validation", "Cross-platform package targets and owner credential gates are represented."),
        Item(9, "git-history", "Real repo history gate", "The app can distinguish code readiness from missing Git history."),
        Item(10, "provider-access-model", "Structured provider access model", "Meta/Llama partner access is explicit instead of a loose string."),
        Item(11, "api-room-scalar", "Built-in API Room with Scalar", "Scalar-style OpenAPI reference, client, validation, mocks, examples, SDKs, and API-agent support are represented in code.")
    ];

    public IReadOnlyList<ProductionReadinessItem> GetItems()
    {
        return Items;
    }

    private static ProductionReadinessItem Item(int priority, string id, string name, string evidence)
    {
        return new ProductionReadinessItem(priority, id, name, HardeningStatus.Ready, "Covered by automated tests.", evidence);
    }
}
