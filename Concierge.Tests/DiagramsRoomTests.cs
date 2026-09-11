using Bunit;
using Concierge.Shared;
using Concierge.Shared.Diagrams;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The Diagrams room, which promises "watch it draw".
///
/// **Two things were wrong on it and both are this repository's signature defect: a screen
/// asserting something untrue.**
///
/// It said **"0 parts"** under every diagram anybody typed, because the mermaid runtime hands
/// back an empty element list on purpose — it counts declared lines instead. A number that is
/// always nought is the approvals badge that always said two.
///
/// And it drew nothing. The page rendered the source into a `&lt;pre&gt;` under a comment
/// saying "rendered by the same mechanism the thread uses" — which it was not. The thread
/// uses `MermaidBlock`, which calls the renderer. Sixth comment found in this repository
/// describing behaviour that did not exist.
/// </summary>
public sealed class DiagramsRoomTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Pages.Diagrams> Room()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        Concierge.Shared.Diagrams.ConciergeDiagramsServiceCollectionExtensions.AddConciergeDiagrams(Services);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Concierge.Shared.Components.Pages.Diagrams>();
    }

    /// <summary>
    /// The page draws the diagram through the same component the thread uses, rather than
    /// printing the source and calling that drawing.
    /// </summary>
    [Fact]
    public void It_draws_the_diagram_rather_than_printing_it()
    {
        var room = Room();

        // MermaidBlock's own host, which is what the renderer fills.
        Assert.NotEmpty(room.FindAll(".diagram-host"));
    }

    /// <summary>
    /// A count the runtime never fills is not a count. The mermaid runtime says what it knows
    /// — how many lines were declared — and that is what the row shows.
    /// </summary>
    [Fact]
    public void It_never_says_nought_parts()
    {
        var room = Room();

        Assert.DoesNotContain("0 parts", room.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void And_says_what_the_runtime_actually_knows_instead()
    {
        var room = Room();

        Assert.Contains("declared lines", room.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime's own contract, pinned here because the page was reading the wrong field
    /// from it: elements are empty by design and the summary carries the answer.
    /// </summary>
    [Fact]
    public void The_mermaid_runtime_counts_lines_rather_than_elements()
    {
        var made = new MermaidDiagramRuntime().FromSource(
            "flowchart TD\n  A[One] --> B[Two]\n  B --> C[Three]");

        Assert.NotNull(made);
        Assert.Empty(made!.Elements);
        Assert.Contains("declared lines", made.Summary, StringComparison.Ordinal);
    }
}
