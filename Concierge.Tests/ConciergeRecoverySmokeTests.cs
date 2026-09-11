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
        Assert.Contains(snapshot.ProductionGates, gate => gate.Id == "owner-credentials" && gate.Status == HardeningStatus.NeedsOwner);

        // The source-control gate used to be asserted here as Blocked, which held for as long
        // as it was a typed word and stopped holding the moment it started asking the machine:
        // this suite runs inside a repository, so the honest answer here is Ready. What the
        // gate must do is *reflect the folder*, and that is checked against both kinds of
        // folder in ReleaseGateTests rather than against whichever one the test host is in.
        Assert.Contains(snapshot.ProductionGates, gate => gate.Id == "source-control");
    }

    [Theory]
    // "/" is the workspace now, and the workspace is Chat.razor. Home.razor no
    // longer claims a route: the app is one screen, not a set of pages.
    [InlineData("Chat.razor", "@page \"/\"")]
    // Skills is no longer a route either, for the same reason and by the same
    // route: it is a picker panel over the workspace, opened from the Skills
    // group in the sidebar. That it is reachable, and that turning one on
    // reaches the composer, is asserted in WorkspaceSkillsTests against the
    // rendered UI.
    // Settings is no longer a route. It is a panel over the workspace, opened
    // from the runtime row in the sidebar — neither reference has a settings
    // screen you travel to. What must still hold is that it is reachable, and
    // that is asserted in WorkspaceSettingsTests against the rendered UI rather
    // than against a directive in a file.
    [InlineData("Approvals.razor", "@page \"/approvals\"")]
    [InlineData("Release.razor", "@page \"/release\"")]
    [InlineData("Roadmap.razor", "@page \"/roadmap\"")]
    [InlineData("BusinessApis.razor", "@page \"/business-apis\"")]
    [InlineData("Beyond.razor", "@page \"/beyond\"")]
    [InlineData("Pricing.razor", "@page \"/pricing\"")]
    // The two rooms this list had always omitted. Both were shells when it was
    // written and neither was noticed missing, which is the argument for
    // listing every room here rather than the ones that happened to break.
    [InlineData("Engineering.razor", "@page \"/engineering\"")]
    [InlineData("Product.razor", "@page \"/product\"")]
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
