// Code-behind for ApiKeyEditor. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Concierge.Shared.Settings;

namespace Concierge.Shared.Components.Components;

public partial class ApiKeyEditor
{

    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _saved = new Dictionary<string, string>(StringComparer.Ordinal);
    private bool _saving;
    private string _statusMessage = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        _saved = await SecretStore.LoadAsync();
        _statusMessage = _saved.Count == 0
            ? "No keys saved yet."
            : $"{_saved.Count} key(s) saved. Values are hidden — re-enter to overwrite.";
    }

    private bool IsConfigured(string key) => _saved.ContainsKey(key) && !string.IsNullOrEmpty(_saved[key]);

    private void OnFieldChanged(string key, string value)
    {
        _drafts[key] = value;
    }

    private async Task SaveAsync()
    {
        if (_drafts.Count == 0)
        {
            return;
        }

        _saving = true;
        try
        {
            await SecretStore.SaveAsync(_drafts);
            _saved = await SecretStore.LoadAsync();
            _drafts.Clear();
            _statusMessage = "Saved. Restart Concierge to apply the new keys.";
        }
        catch (Exception ex)
        {
            _statusMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            _saving = false;
        }
    }
}
