namespace Concierge.Tests;

/// <summary>
/// Three recipes, one behaviour.
///
/// Concierge renders desktop/web, handheld and wearable as separate component
/// sets rather than one tree bent by breakpoints. A watch is not a small
/// desktop: hiding desktop parts until a watch fits leaves an empty screen
/// rather than a designed one, and — proven in this codebase — a change made
/// for one form factor reaches the others. The scrim added for phones was a
/// grid child at every width and silently pushed the desktop sidebar into the
/// second column.
///
/// What holds them together is that none of them owns any behaviour. These
/// tests defend that split at the level a component test cannot see: which
/// files exist, and what is allowed to be in them.
/// </summary>
public sealed class RecipeTests
{
    private static string Root()
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

        throw new InvalidOperationException("Could not locate Concierge.slnx.");
    }

    private static string RecipePath(string recipe)
        => Path.Combine(Root(), "Concierge.Shared.Components", "Workspace", recipe, "Workspace.razor");

    private static readonly string[] Recipes = ["Desktop", "Handheld", "Wearable"];

    [Fact]
    public void Every_recipe_exists_and_inherits_the_shared_workspace()
    {
        foreach (var recipe in Recipes)
        {
            var markup = File.ReadAllText(RecipePath(recipe));
            Assert.Contains("@inherits Concierge.Shared.Components.Workspace.WorkspaceBase", markup, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The whole point of the split. A recipe is markup; the moment one grows
    /// its own send loop or its own conversation list, three form factors can
    /// disagree about what the product does.
    ///
    /// An @code block is allowed for presentation belonging to one shape only —
    /// the watch trims the last reply, because a watch reads one answer rather
    /// than a transcript. What is not allowed is reaching past the view to the
    /// services.
    /// </summary>
    [Fact]
    public void No_recipe_injects_a_service_of_its_own()
    {
        foreach (var recipe in Recipes)
        {
            var markup = File.ReadAllText(RecipePath(recipe));

            Assert.DoesNotContain("@inject", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("[Inject]", markup, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A recipe is reachable only through the switch. If one carried an @page
    /// directive it would be a route of its own, and a phone could be sent to
    /// the desktop recipe by URL.
    /// </summary>
    [Fact]
    public void No_recipe_claims_a_route()
    {
        foreach (var recipe in Recipes)
        {
            Assert.DoesNotContain("@page", File.ReadAllText(RecipePath(recipe)), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The behaviour lives in exactly one place, and it is not small — this is
    /// the file that would otherwise have been copied three times.
    /// </summary>
    [Fact]
    public void The_shared_workspace_holds_the_behaviour_and_no_markup()
    {
        var basePath = Path.Combine(Root(), "Concierge.Shared.Components", "Workspace", "WorkspaceBase.cs");
        var source = File.ReadAllText(basePath);

        Assert.Contains("abstract class WorkspaceBase", source, StringComparison.Ordinal);
        Assert.Contains("Task SendAsync()", source, StringComparison.Ordinal);
        Assert.Contains("ToggleMicAsync", source, StringComparison.Ordinal);

        // Every service the markup used to inject for itself.
        Assert.Contains("IConversationStore Store", source, StringComparison.Ordinal);
        Assert.Contains("ILlmRuntimeService LlmRuntime", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The desktop recipe has no drawer and the handheld one has no permanent
    /// column. This is the assertion that fails if somebody "simplifies" the
    /// two back into a single tree with media queries.
    /// </summary>
    [Fact]
    public void Desktop_carries_no_drawer_and_handheld_does()
    {
        var desktop = File.ReadAllText(RecipePath("Desktop"));
        var handheld = File.ReadAllText(RecipePath("Handheld"));

        Assert.DoesNotContain("ws-menu", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("ws-scrim", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("_drawerOpen", desktop, StringComparison.Ordinal);

        Assert.Contains("ws-menu", handheld, StringComparison.Ordinal);
        Assert.Contains("ws-scrim", handheld, StringComparison.Ordinal);
    }

    /// <summary>
    /// The watch is deliberately not a small workspace. None of these belong on
    /// a 200px screen, and their absence is the design rather than an omission —
    /// the phone in the same pocket is where that work happens.
    /// </summary>
    [Fact]
    public void The_watch_offers_a_decision_and_a_microphone_and_nothing_else()
    {
        var wearable = File.ReadAllText(RecipePath("Wearable"));

        Assert.Contains("wear-mic", wearable, StringComparison.Ordinal);
        Assert.Contains("Allow", wearable, StringComparison.Ordinal);
        Assert.Contains("Deny", wearable, StringComparison.Ordinal);

        foreach (var absent in new[] { "ws-side", "ws-head", "comp-box", "ws-room", "sheet", "chip" })
        {
            Assert.False(
                wearable.Contains(absent, StringComparison.Ordinal),
                $"the watch recipe must not carry {absent}");
        }
    }

    /// <summary>
    /// The switch renders nothing until the viewport has been measured.
    /// Rendering a guess and correcting it remounts the workspace, which
    /// re-runs OnInitializedAsync and reloads the thread.
    /// </summary>
    [Fact]
    public void The_switch_waits_rather_than_guessing()
    {
        var switchPath = Path.Combine(Root(), "Concierge.Shared.Components", "Pages", "Chat.razor");
        var markup = File.ReadAllText(switchPath);

        Assert.Contains("ff-wait", markup, StringComparison.Ordinal);
        Assert.Contains("conciergeFormFactor.watch", markup, StringComparison.Ordinal);

        foreach (var recipe in Recipes)
        {
            Assert.Contains($"Workspace.{recipe}.Workspace", markup, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Form factor is read from the viewport, not the OS. The same Android head
    /// runs on a phone and on a Wear OS watch, and a desktop window is dragged
    /// through every range while the app is open — asking the platform would
    /// put a watch on the phone recipe and would never notice a resize.
    /// </summary>
    [Fact]
    public void Form_factor_is_measured_from_the_viewport()
    {
        var js = File.ReadAllText(Path.Combine(Root(), "Concierge.Shared.Components", "wwwroot", "concierge-formfactor.js"));

        Assert.Contains("window.innerWidth", js, StringComparison.Ordinal);
        Assert.Contains("resize", js, StringComparison.Ordinal);

        // The handheld boundary is the one the drawer already used.
        Assert.Contains("759.98", js, StringComparison.Ordinal);
        Assert.Contains("319.98", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both hosts load the script. The MAUI host and the web host have separate
    /// shells, and a recipe switch that works in only one of them does not work.
    /// </summary>
    [Fact]
    public void Both_hosts_load_the_form_factor_script()
    {
        var maui = File.ReadAllText(Path.Combine(Root(), "Concierge", "wwwroot", "index.html"));
        var web = File.ReadAllText(Path.Combine(Root(), "Concierge.Web", "Components", "App.razor"));

        Assert.Contains("concierge-formfactor.js", maui, StringComparison.Ordinal);
        Assert.Contains("concierge-formfactor.js", web, StringComparison.Ordinal);
    }
}
