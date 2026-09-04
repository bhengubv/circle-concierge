// Code-behind for Diagrams. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Concierge.Diagrams.Design;
using Concierge.Shared.Diagrams;

namespace Concierge.Shared.Components.Pages;

public partial class Diagrams
{

    private List<IDiagramRuntime> _orderedRuntimes = new();
    private MermaidDiagramRuntime _mermaid = default!;
    private string _source = """
flowchart TD
    A[User asks Concierge] --> B{Mermaid block in reply?}
    B -- yes --> C[Render diagram inline]
    B -- no  --> D[Render plain text]
    C --> E[Persist to SQLite]
    D --> E
""";
    private DiagramArtifact? _mermaidArtifact;

    private string _fetchSource = "penpot";
    private string _fetchId = string.Empty;
    private bool _fetching;
    private string _fetchStatus = "Pick a source + paste a file id.";
    private DiagramArtifact? _fetchedArtifact;

    private bool CanFetch => !_fetching
        && !string.IsNullOrWhiteSpace(_fetchId)
        && _orderedRuntimes.FirstOrDefault(r => r.Id == _fetchSource)?.IsReady == true;

    protected override void OnInitialized()
    {
        _orderedRuntimes = Runtimes
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.IsReady)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _mermaid = _orderedRuntimes.OfType<MermaidDiagramRuntime>().FirstOrDefault()
            ?? new MermaidDiagramRuntime();
        Recompute();
    }

    protected override void OnParametersSet() => Recompute();

    private void Recompute()
    {
        _mermaidArtifact = string.IsNullOrWhiteSpace(_source) ? null : _mermaid.FromSource(_source);
    }

    private async Task FetchAsync()
    {
        if (!CanFetch)
        {
            return;
        }

        _fetching = true;
        _fetchStatus = $"Fetching from {_fetchSource}…";
        try
        {
            if (_fetchSource == "penpot")
            {
                if (!Guid.TryParse(_fetchId.Trim(), out var fileId))
                {
                    _fetchStatus = "PenPot expects a UUID file id.";
                    return;
                }
                var client = Services.GetService(typeof(PenPotApiClient)) as PenPotApiClient;
                var runtime = Services.GetService(typeof(PenPotDiagramRuntime)) as PenPotDiagramRuntime;
                if (client is null || runtime is null)
                {
                    _fetchStatus = "PenPot adapter is not registered in this host.";
                    return;
                }
                var file = await client.GetFileAsync(fileId);
                _fetchedArtifact = runtime.Translate(file);
                _fetchStatus = $"Fetched '{file.Name ?? "(untitled)"}'.";
            }
            else
            {
                var client = Services.GetService(typeof(FigmaApiClient)) as FigmaApiClient;
                var runtime = Services.GetService(typeof(FigmaDiagramRuntime)) as FigmaDiagramRuntime;
                if (client is null || runtime is null)
                {
                    _fetchStatus = "Figma adapter is not registered in this host.";
                    return;
                }
                var file = await client.GetFileAsync(_fetchId.Trim());
                _fetchedArtifact = runtime.Translate(file);
                _fetchStatus = $"Fetched '{file.Name ?? "(untitled)"}'.";
            }
        }
        catch (Exception ex)
        {
            _fetchStatus = $"Fetch failed: {ex.Message}";
        }
        finally
        {
            _fetching = false;
        }
    }
}
