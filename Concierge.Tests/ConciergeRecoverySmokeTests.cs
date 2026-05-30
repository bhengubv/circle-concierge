using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ConciergeRecoverySmokeTests
{
    [Fact]
    public void Core_service_exposes_recovered_product_spine()
    {
        var services = new ServiceCollection()
            .AddConciergeCore()
            .BuildServiceProvider();

        var snapshot = services.GetRequiredService<IConciergeStateService>().GetSnapshot();

        Assert.Equal(10, snapshot.Providers.Count);
        Assert.True(snapshot.Skills.Count >= 69);
        Assert.Equal(15, snapshot.ProductionTasks.Count);
        Assert.All(snapshot.ProductionTasks, task => Assert.Equal(HardeningStatus.Ready, task.Status));
        Assert.Contains(snapshot.Providers, provider => provider.Name.Contains("Claude", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Skills, skill => skill.Id == "css-js-animation");
        Assert.NotNull(services.GetService<IAgentHarnessService>());
        Assert.NotNull(services.GetService<IBeyondClaudeService>());
    }

    [Fact]
    public void Diagnostics_keep_owner_gates_separate_from_code_readiness()
    {
        var service = new ConciergeStateService();
        var snapshot = service.GetSnapshot();

        Assert.Contains(snapshot.Diagnostics, check => check.Id == "dotnet" && check.Status == HardeningStatus.Ready);
        Assert.Contains(snapshot.ProductionGates, gate => gate.Id == "source-control" && gate.Status == HardeningStatus.Blocked);
        Assert.Contains(snapshot.ProductionGates, gate => gate.Id == "owner-credentials" && gate.Status == HardeningStatus.NeedsOwner);
    }

    [Theory]
    [InlineData("Home.razor", "@page \"/\"")]
    [InlineData("Skills.razor", "@page \"/skills\"")]
    [InlineData("Settings.razor", "@page \"/settings\"")]
    [InlineData("Approvals.razor", "@page \"/approvals\"")]
    [InlineData("Release.razor", "@page \"/release\"")]
    [InlineData("Roadmap.razor", "@page \"/roadmap\"")]
    [InlineData("BusinessApis.razor", "@page \"/business-apis\"")]
    [InlineData("Beyond.razor", "@page \"/beyond\"")]
    [InlineData("Pricing.razor", "@page \"/pricing\"")]
    public void Rebuilt_web_routes_exist(string fileName, string routeDirective)
    {
        var root = FindWorkspaceRoot();
        var path = Path.Combine(root, "Concierge.Shared.Components", "Pages", fileName);

        Assert.True(File.Exists(path), $"{fileName} should exist.");
        Assert.Contains(routeDirective, File.ReadAllText(path), StringComparison.Ordinal);
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Concierge.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate Concierge.slnx from test output directory.");
    }
}
