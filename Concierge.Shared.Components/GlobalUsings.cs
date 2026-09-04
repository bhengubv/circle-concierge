// _Imports.razor applies to .razor files only. Now that the logic lives in
// .razor.cs code-behind, those files need the same namespaces or every
// component would repeat twenty using lines.
//
// This mirrors _Imports.razor deliberately — if a namespace is added there it
// belongs here too, and vice versa.

global using System.Net.Http;
global using System.Net.Http.Json;

global using Microsoft.AspNetCore.Components;
global using Microsoft.AspNetCore.Components.Forms;
global using Microsoft.AspNetCore.Components.Routing;
global using Microsoft.AspNetCore.Components.Web;
global using Microsoft.AspNetCore.Components.Web.Virtualization;
global using Microsoft.JSInterop;

global using Concierge.Shared;
global using Concierge.Shared.Chat;
global using Concierge.Shared.Diagrams;
global using Concierge.Shared.Media;
global using Concierge.Shared.Settings;
global using Concierge.Shared.Tools;

global using MudBlazor;
