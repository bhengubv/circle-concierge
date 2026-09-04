// Code-behind for About. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Reflection;

namespace Concierge.Shared.Components.Pages;

public partial class About
{

    private string _version = "1.0";
    private string _build = "dev";

    protected override void OnInitialized()
    {
        // Pull the running assembly's version + build commit if encoded
        // (Concierge.csproj stamps AssemblyInformationalVersion when set).
        try
        {
            var asm = typeof(About).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString();
            if (!string.IsNullOrWhiteSpace(info))
            {
                var parts = info.Split('+', 2);
                _version = parts[0];
                _build = parts.Length > 1 ? parts[1].Substring(0, Math.Min(7, parts[1].Length)) : "dev";
            }
        }
        catch { /* surfaces "1.0" / "dev" — never blocks the page */ }
    }
}
