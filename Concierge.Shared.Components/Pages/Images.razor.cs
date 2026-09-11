// Code-behind for Images. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Concierge.Shared.Media;

namespace Concierge.Shared.Components.Pages;

public partial class Images
{

    private List<IImageRuntime> _orderedRuntimes = new();
    private string? _selectedRuntimeId;
    private string _prompt = string.Empty;
    private string _negative = string.Empty;
    private int _size = 1024;
    private int _count = 1;
    private bool _busy;
    private string _statusMessage = string.Empty;
    private List<ImageArtifact> _artifacts = new();

    protected override void OnInitialized()
    {
        _orderedRuntimes = Runtimes
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.IsReady)
            .ThenBy(r => r.EngineLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _selectedRuntimeId = _orderedRuntimes.FirstOrDefault(r => r.IsReady)?.Id ?? _orderedRuntimes.FirstOrDefault()?.Id;
    }

    private bool CanGenerate => !_busy
        && !string.IsNullOrWhiteSpace(_prompt)
        && Chosen is { IsReady: true };

    private IImageRuntime? Chosen
        => _orderedRuntimes.FirstOrDefault(runtime => runtime.Id == _selectedRuntimeId);

    /// <summary>
    /// Why the button is off, in the words of whatever is actually missing.
    ///
    /// **It used to say "Pick a runtime + describe an image" forever**, including to somebody
    /// who had picked one and described an image and was staring at a dead button. The thing
    /// missing was an API key, and the screen never said so — it told them to do again the
    /// two things they had already done.
    ///
    /// The runtime knows why it is not ready and says so in a sentence. This hands that
    /// sentence on rather than inventing a second explanation beside it.
    /// </summary>
    private string WhyNot
    {
        get
        {
            // Nothing installed at all is answered by the room itself, above the section
            // this line lives in — so a sentence for it here would be words nobody can ever
            // see, which is the same defect in miniature.
            if (Chosen is null)
            {
                return "Pick something to make it with.";
            }

            if (!Chosen.IsReady)
            {
                return Chosen.StatusMessage;
            }

            return string.IsNullOrWhiteSpace(_prompt)
                ? "Describe the picture you want."
                : string.Empty;
        }
    }

    private void OnRuntimeChanged(ChangeEventArgs args)
    {
        _selectedRuntimeId = args.Value?.ToString();
    }

    private async Task GenerateAsync()
    {
        if (!CanGenerate)
        {
            return;
        }

        var runtime = _orderedRuntimes.First(r => r.Id == _selectedRuntimeId);
        _busy = true;
        _statusMessage = $"Calling {runtime.EngineLabel}…";
        try
        {
            var request = new ImageGenerationRequest(
                Prompt: _prompt.Trim(),
                NegativePrompt: string.IsNullOrWhiteSpace(_negative) ? null : _negative.Trim(),
                Size: _size,
                Count: _count);
            var produced = await runtime.GenerateAsync(request);
            // Newest-first so the page doesn't bury fresh results.
            _artifacts = produced.Concat(_artifacts).ToList();
            _statusMessage = produced.Count > 0
                ? $"Got {produced.Count} image(s)."
                : "Runtime returned nothing. Check the engine status pill above.";
        }
        catch (Exception ex)
        {
            _statusMessage = $"Generation failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }
}
