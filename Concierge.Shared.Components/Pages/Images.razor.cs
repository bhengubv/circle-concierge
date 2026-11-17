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
    private bool _editing;
    private List<ImageReference> _references = [];
    private List<string> _referenceNames = [];
    private int _size = 1024;
    private int _count = 1;
    private bool _busy;
    private string _statusMessage = string.Empty;
    private List<ImageArtifact> _artifacts = new();
    private int _resultVersion;

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
        && Chosen is { IsReady: true }
        && (!_editing || (Chosen is IImageEditingRuntime && _references.Count > 0));

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

            if (_editing && Chosen is not IImageEditingRuntime)
                return "Choose an image maker that can edit photos.";

            if (_editing && _references.Count == 0)
                return "Choose a photo to edit.";

            return string.IsNullOrWhiteSpace(_prompt)
                ? "Describe the picture you want."
                : string.Empty;
        }
    }

    private void SetMode(bool editing)
    {
        _editing = editing;
        _statusMessage = string.Empty;
        if (editing && Chosen is not IImageEditingRuntime)
            _selectedRuntimeId = _orderedRuntimes.OfType<IImageEditingRuntime>()
                .FirstOrDefault(runtime => runtime.IsReady)?.Id
                ?? _orderedRuntimes.OfType<IImageEditingRuntime>().FirstOrDefault()?.Id;
    }

    private async Task OnReferencesSelected(InputFileChangeEventArgs args)
    {
        _references = [];
        _referenceNames = [];
        try
        {
            var files = args.GetMultipleFiles(4);
            foreach (var file in files)
            {
                if (file.ContentType is not ("image/png" or "image/jpeg" or "image/webp" or "image/heic" or "image/heif"))
                    throw new InvalidOperationException("Choose PNG, JPEG, WebP or HEIC/HEIF photos.");
                // A smaller per-photo limit keeps a mobile WebView from holding hundreds of MB.
                using var stream = file.OpenReadStream(16 * 1024 * 1024);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                _references.Add(new ImageReference(file.ContentType, buffer.ToArray()));
                _referenceNames.Add(file.Name);
            }
            _statusMessage = $"{_references.Count} photo(s) ready to edit.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            _references = [];
            _referenceNames = [];
            _statusMessage = ex.Message;
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
        _statusMessage = _editing ? "Editing your photo…" : "Making your picture…";
        try
        {
            var request = new ImageGenerationRequest(
                Prompt: _prompt.Trim(),
                NegativePrompt: string.IsNullOrWhiteSpace(_negative) ? null : _negative.Trim(),
                Size: _size,
                Count: _count);
            var produced = _editing
                ? await ((IImageEditingRuntime)runtime).EditAsync(new ImageEditRequest(
                    request.Prompt, _references.ToArray(), request.Size, request.Count))
                : await runtime.GenerateAsync(request);
            // Newest-first so the page doesn't bury fresh results.
            _artifacts = produced.Concat(_artifacts).ToList();
            if (produced.Count > 0) _resultVersion++;
            _statusMessage = produced.Count > 0
                ? $"Got {produced.Count} image(s)."
                : "No image was returned. Try again, or check the image maker's account and connection.";
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
