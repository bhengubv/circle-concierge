using Concierge.Shared;

namespace Concierge.Tests;

public sealed class ProductSurfaceContractTests
{
    [Fact]
    public void Beyond_page_surfaces_every_capability_lane()
    {
        var root = FindWorkspaceRoot();
        var page = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Pages", "Beyond.razor"));
        var capabilities = new BeyondClaudeService().GetCapabilities();

        Assert.Contains("@page \"/beyond\"", page, StringComparison.Ordinal);
        Assert.All(capabilities, capability =>
        {
            Assert.Contains("capability.PlainEnglishName", page, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(capability.PlainEnglishName));
        });
    }

    [Fact]
    public void Navigation_exposes_beyond_code_without_hiding_core_rooms()
    {
        var root = FindWorkspaceRoot();
        var nav = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Layout", "NavMenu.razor"));

        Assert.Contains("href=\"beyond\"", nav, StringComparison.Ordinal);
        Assert.Contains("Beyond Code", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"engineering\"", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"product\"", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"settings\"", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"approvals\"", nav, StringComparison.Ordinal);
        Assert.Contains("href=\"pricing\"", nav, StringComparison.Ordinal);
    }

    [Fact]
    public void Business_apis_page_surfaces_scalar_api_room()
    {
        var root = FindWorkspaceRoot();
        var page = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Pages", "BusinessApis.razor"));

        Assert.Contains("@inject IScalarApiService", page, StringComparison.Ordinal);
        Assert.Contains("API Room with Scalar-style workflows", page, StringComparison.Ordinal);
        Assert.Contains("scalar.Capabilities", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Concierge_snapshot_has_no_duplicate_provider_or_skill_ids()
    {
        var snapshot = new ConciergeStateService().GetSnapshot();

        Assert.Equal(snapshot.Providers.Count, snapshot.Providers.Select(provider => provider.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(snapshot.Skills.Count, snapshot.Skills.Select(skill => skill.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(snapshot.Providers, provider =>
            Assert.True(
                provider.OfficialApi.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || provider.AccessKind == ProviderAccessKind.OfficialPartnerApi,
                $"{provider.Name} needs a documented official access path."));
        Assert.Contains(snapshot.Providers, provider => provider.Id == "meta" && provider.AccessKind == ProviderAccessKind.OfficialPartnerApi);
    }

    [Fact]
    public void Production_tasks_are_ordered_and_have_substance()
    {
        var tasks = new ConciergeStateService().GetProductionTasks();

        Assert.Equal(tasks.Select(task => task.Priority).Order().ToList(), tasks.Select(task => task.Priority).ToList());
        Assert.Equal(tasks.Count, tasks.Select(task => task.Priority).Distinct().Count());
        Assert.All(tasks, task =>
        {
            Assert.Equal(HardeningStatus.Ready, task.Status);
            Assert.True(task.Summary.Length >= 30, $"{task.Title} needs a useful summary.");
        });
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
