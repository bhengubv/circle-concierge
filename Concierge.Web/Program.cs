using Concierge.Web.Components;
using Concierge.Web.Hosting;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Diagrams;
using Concierge.Ai;
using Concierge.Mesh;
using Concierge.Media;
using CircleAI.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddConciergeCore()
    .AddConciergeChat()
    .AddConciergeDiagrams()
    .AddConciergeAi()
    .AddConciergeMesh()
    .AddConciergeMedia();
// Replace the NullDeviceContext registered by AddConciergeAi with the request-aware one.
builder.Services.RemoveAll<IDeviceContext>();
builder.Services.AddSingleton<IDeviceContext, HttpContextDeviceContext>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// HTTPS termination happens at the reverse proxy (ARR / ingress). Do not redirect here
// because it breaks health checks and forwarded-proto handling behind the proxy.
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(Concierge.Shared.Components.ComponentAssemblyMarker).Assembly,
        typeof(Concierge.Web.Client.ClientAssemblyMarker).Assembly);

app.Run();
