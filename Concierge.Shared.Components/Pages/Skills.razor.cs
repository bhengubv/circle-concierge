// Code-behind for Skills. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Pages;

public partial class Skills
{

    private string _query = string.Empty;
    private string? _area;

    // @oninput rather than @onchange: the list should narrow while you type.
    // Waiting for blur on a 6689px catalogue is waiting for the wrong event.
    private void OnQueryChanged(ChangeEventArgs e)
        => _query = e.Value?.ToString() ?? string.Empty;

    // Two letters, so 69 rows have something to aim at other than a wall of
    // sentences. Drawn from the name rather than a per-skill icon set, which
    // would be 69 drawings to invent and keep true.
    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1) return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
        return $"{words[0][0]}{words[1][0]}".ToUpperInvariant();
    }
}
