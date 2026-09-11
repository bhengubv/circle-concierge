using Concierge.Shared.Design;
using Concierge.CodeMode;
using System.Threading.RateLimiting;
using Concierge.Web.Components;
using Concierge.Web.Hosting;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Safety;
using Concierge.Shared.Diagrams;
using Concierge.Shared.Media;
using Concierge.Shared.Settings;
using Concierge.Shared.Skills;
using Concierge.Shared.Telemetry;
using Concierge.Shared.Tools;
using Concierge.Ai;
using Concierge.Chat.Cloud;
using Concierge.Diagrams.Design;
using Concierge.Mesh;
using Concierge.Media;
using Concierge.Media.Cloud;
using CircleAI.Core;
using Microsoft.AspNetCore.RateLimiting;
using MudBlazor.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Layer locally-saved secrets onto configuration BEFORE building the service provider.
// Values entered via the Settings → API keys UI land in %LocalAppData%/Concierge/secrets.json
// and override appsettings.json (env vars still win — standard ASP.NET Core ordering).
var secretStore = new LocalSecretStore();
var savedSecrets = await secretStore.LoadAsync();
if (savedSecrets.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(savedSecrets!);
}
builder.Services.AddSingleton<IConciergeSecretStore>(secretStore);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
// MudBlazor — same registration as the MAUI host. Web + MAUI share the
// Razor component library, so MudServices must be wired in both hosts.
builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddConciergeCore()
    .AddLocalSkillSources()
    .AddConciergeChat()
    .AddConciergeDiagrams()
    .AddConciergeMetrics()
    .AddConciergeTools()
    .AddConciergeAi()
    .AddConciergeMesh()
    .AddConciergeMedia()
    .AddConciergeDesignTools()
    // Looking inside a media file, where there is an encoder to look with.
    .AddConciergeMediaLook()
    .AddConciergeTranscription()
    .AddConciergeCodingTools()
    .AddConciergeMusicLibrary()
    .AddConciergeStockFootage()
    // Parental controls / content-filter pipeline. Wraps the IChatRuntime
    // registered above so every chat call routes through the filter when
    // Family Mode is on. Off-mode is a zero-cost pass-through.
    .AddConciergeSafety();
builder.Services.AddSingleton<InteractiveToolApprovalService>();
builder.Services.AddSingleton<IToolApprovalService>(sp =>
    new AuditingToolApprovalService(
        sp.GetRequiredService<InteractiveToolApprovalService>(),
        sp.GetRequiredService<IToolApprovalAuditLog>()));
builder.Services.AddConciergeRuntime();
builder.Services.AddConciergeState(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Concierge", "web"));
// Work that runs end to end, keeping its place on disk.
builder.Services.AddConciergeRoutines(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Concierge", "web"));
builder.Services.AddConciergeCodeMode(AppContext.BaseDirectory);
builder.Services.AddSingleton<PrometheusMetricSnapshot>();

// Rate limiting protects every endpoint from runaway clients (and from a misbehaving
// streaming-chat reconnect loop). The chat-stream policy is deliberately conservative —
// 60 starts per minute per client IP — because each call holds an open SSE socket for
// the lifetime of a response. The global policy is looser for plain page loads.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    options.AddPolicy("chat-stream", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

// BYO API key cloud chat runtimes. Each is wired regardless of whether a key is present —
// the runtime's IsReady property gates actual calls and the chat UI shows a "needs key"
// status pill for ones without configuration. This way the dropdown lists every provider
// so the user can see what is wireable, not just what is wired.
builder.Services.AddOpenAiChat(sp => sp.GetRequiredService<IConfiguration>().GetSection("OpenAI").Get<OpenAiChatOptions>() ?? new OpenAiChatOptions());
builder.Services.AddAnthropicChat(sp => sp.GetRequiredService<IConfiguration>().GetSection("Anthropic").Get<AnthropicChatOptions>() ?? new AnthropicChatOptions());
builder.Services.AddGeminiChat(sp => sp.GetRequiredService<IConfiguration>().GetSection("Gemini").Get<GeminiChatOptions>() ?? new GeminiChatOptions());

// Same pattern for the design-tool adapters — they only become useful once the user has
// configured an access token, but registering them up-front makes them discoverable.
builder.Services.AddConciergePenPotDiagrams(sp => sp.GetRequiredService<IConfiguration>().GetSection("PenPot").Get<PenPotApiOptions>() ?? new PenPotApiOptions());
builder.Services.AddConciergeFigmaDiagrams(sp => sp.GetRequiredService<IConfiguration>().GetSection("Figma").Get<FigmaApiOptions>() ?? new FigmaApiOptions());

// Image + voice. The OpenAI key is shared across chat / images / voice — the options
// classes are separate so each provider can be turned on / off independently if a user
// only wants TTS but not DALL-E (or vice versa).
builder.Services.AddOpenAiImages(sp => sp.GetRequiredService<IConfiguration>().GetSection("OpenAIImages").Get<OpenAiImageOptions>() ?? new OpenAiImageOptions());
builder.Services.AddStabilityImages(sp => sp.GetRequiredService<IConfiguration>().GetSection("Stability").Get<StabilityImageOptions>() ?? new StabilityImageOptions());
builder.Services.AddOpenAiVoice(sp => sp.GetRequiredService<IConfiguration>().GetSection("OpenAIVoice").Get<OpenAiVoiceOptions>() ?? new OpenAiVoiceOptions());
// Speech on the device, beside the cloud one.
builder.Services.AddConciergeLocalVoice();
builder.Services.AddConciergeMediaCloudDefaults();

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
app.UseRateLimiter();

// Optional API-key gate. Reads CONCIERGE_API_KEY env var first (so production deploys
// can flip auth on without touching appsettings), then Auth:ApiKey from configuration.
// Empty / unset means the middleware is a passthrough (current dev behaviour preserved).
var apiKeyOptions = new ApiKeyAuthOptions
{
    ConfiguredKey = Environment.GetEnvironmentVariable("CONCIERGE_API_KEY")
        ?? app.Configuration["Auth:ApiKey"],
};
app.UseConciergeApiKeyAuth(apiKeyOptions);

app.MapConciergeHealth();
app.MapConciergeVoice();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(Concierge.Shared.Components.ComponentAssemblyMarker).Assembly,
        typeof(Concierge.Web.Client.ClientAssemblyMarker).Assembly);

app.Run();
