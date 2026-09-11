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

    /// <summary>
    /// Every room stays reachable, and adding Beyond Code does not push the
    /// others out — the guarantee this has always protected.
    ///
    /// Where navigation lives has moved. There is no NavMenu.razor any more:
    /// the tab bar and the ⋯ menu are gone and the workspace sidebar is the
    /// only way to a room, so that is what this reads. Two of the seven names
    /// it used to check are no longer routes at all — Settings and Skills are
    /// panels over the workspace — so they are asserted as the controls that
    /// open them instead, which is the same guarantee against the thing that
    /// now provides it.
    /// </summary>
    [Fact]
    public void Navigation_exposes_beyond_code_without_hiding_core_rooms()
    {
        var root = FindWorkspaceRoot();
        // The workspace split into recipes: Pages/Chat.razor picks one, the
        // desktop recipe is the markup, and the room list lives with the rest
        // of the behaviour in WorkspaceBase so every recipe reaches the same
        // rooms.
        var workspace = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Workspace", "Desktop", "Workspace.razor"));
        var links = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Workspace", "WorkspaceBase.cs"));

        Assert.Contains("Beyond Code", links, StringComparison.Ordinal);
        Assert.Contains("\"beyond\"", links, StringComparison.Ordinal);
        Assert.Contains("\"engineering\"", links, StringComparison.Ordinal);
        Assert.Contains("\"product\"", links, StringComparison.Ordinal);
        Assert.Contains("\"pricing\"", links, StringComparison.Ordinal);

        // The rooms group renders those, and the queue has its own way in.
        Assert.Contains("a class=\"ws-room\"", workspace, StringComparison.Ordinal);
        Assert.Contains("href=\"approvals\"", workspace, StringComparison.Ordinal);

        // Settings and Skills are panels now, not destinations.
        Assert.Contains("<Settings", workspace, StringComparison.Ordinal);
        Assert.Contains("<Skills", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void Business_apis_page_surfaces_scalar_api_room()
    {
        var root = FindWorkspaceRoot();
        var page = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Pages", "BusinessApis.razor"));

        Assert.Contains("@inject IScalarApiService", page, StringComparison.Ordinal);
        // Was "API Room with Scalar-style workflows", the old lede — which invited somebody
        // to read a reference, try a call and keep the result, none of which the room can do.
        Assert.Contains("None of it is built yet", page, StringComparison.Ordinal);
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
