using CircleAI.Core;
using Concierge.Ai;
using Concierge.Shared.Rpc.Mcp;
using Concierge.Ai.Isolated;
using MudBlazor.Services;
using Concierge.Chat.Cloud;
using Concierge.Shared.Design;
using Concierge.Diagrams.Design;
using Concierge.Hosting;
using Concierge.Media;
using Concierge.Media.Cloud;
using Concierge.Mesh;
using Concierge.Shared.Media;
using Concierge.Devices;
using Concierge.Shared;
using Concierge.Shared.Devices;
using Concierge.Shared.Hooks;
using Concierge.Shared.Chat;
using Concierge.Shared.Safety;
using Concierge.Shared.Diagrams;
using Concierge.Shared.Settings;
using Concierge.Shared.Skills;
using Concierge.Shared.Telemetry;
using Concierge.Shared.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Concierge;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Several Concierge.Shared services fall back to
		// Directory.GetCurrentDirectory() + ".concierge-artifacts" when
		// Environment.SpecialFolder.LocalApplicationData resolves to empty.
		// On Android, the current dir is "/" — read-only — and the fallback
		// blows up with "Read-only file system : '/.concierge-artifacts'".
		// Pin the working dir to a writable path BEFORE any DI builds, so
		// every fallback hits the per-app data sandbox instead.
		try
		{
			var appData = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
			if (!string.IsNullOrEmpty(appData) && Directory.Exists(appData))
			{
				Directory.SetCurrentDirectory(appData);
			}
		}
		catch
		{
			// Best-effort. If the platform doesn't expose AppDataDirectory
			// or rejects SetCurrentDirectory, downstream services still
			// have Environment.SpecialFolder.LocalApplicationData as a
			// first-choice path.
		}

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		// MudBlazor — registers IDialogService, ISnackbar, IScrollManager, etc.
		// Needed by the flight-deck Dashboard's snackbar feedback + dialogs.
		builder.Services.AddMudServices();
		builder.Services.AddSingleton<IConciergeSecretStore>(_ => new LocalSecretStore());
		builder.Services
			.AddConciergeCore()
			.AddLocalSkillSources()
			.AddConciergeChat()
			.AddConciergeDiagrams()
			.AddConciergeMetrics()
			.AddConciergeTools()
			// Tools published by other software, over MCP. Reads
			// %LOCALAPPDATA%/Concierge/mcp.json; with no file, nothing starts.
			.AddConciergeMcp()
			// The device itself. Every other tool works on files, the web or a
			// notebook; these reach the machine underneath. The portable two
			// hold everywhere .NET runs; the MAUI set is written once and is
			// true on Windows, Android, iOS and Mac Catalyst — and each one
			// answers for itself whether this particular device can do it, so a
			// desktop is never offered a flashlight.
			// Programs the person has registered to run in front of tool calls.
			// Reads %LOCALAPPDATA%/Concierge/hooks.json; with no file, no bridge
			// is constructed and the scheduler behaves exactly as before.
			.AddConciergeHooks()
			.AddConciergeDeviceCapabilities()
			.AddConciergeDesignTools()
			// Looking inside a media file. Registered only where there is an
			// encoder to look with, so a machine without one is never offered
			// tools that would always fail.
			.AddConciergeMediaLook()
			.AddMauiDeviceCapabilities()
			// AddConciergeAi() registers ILlmRuntimeService and the in-process
			// chat runtime; AddConciergeAiIsolated() then replaces the chat
			// runtime with one that talks to a child process.
			//
			// The generator is what faults — an access violation inside
			// mnn_llm_generate_stream_text, which is unmanaged and therefore
			// uncatchable — so that is what has been moved out. Answering now
			// costs a process boundary and a JSON line per token; a fault costs
			// the sentence rather than the application.
			.AddConciergeAi()
			.AddConciergeAiIsolated()
			.AddConciergeMesh()
			.AddConciergeMedia()
			// Parental controls / content-filter pipeline. Wraps IChatRuntime
			// registered above; pass-through when Strictness = Off.
			.AddConciergeSafety();


		// Search needs a key, and the key lives in the same store the API-key
		// editor writes to. Read once at startup, like every other runtime —
		// which is why that editor says a restart is needed.
		builder.Services.AddSingleton<Concierge.Shared.Web.IWebSearch>(sp =>
		{
			try
			{
				var secrets = sp.GetRequiredService<IConciergeSecretStore>()
					.LoadAsync().GetAwaiter().GetResult();

				return secrets.TryGetValue("Brave:ApiKey", out var key) && !string.IsNullOrWhiteSpace(key)
					? new Concierge.Shared.Web.BraveWebSearch(key)
					: new Concierge.Shared.Web.UnconfiguredWebSearch();
			}
			catch (Exception)
			{
				// An unreadable secrets file must not stop the app starting;
				// search simply reports that it is not set up.
				return new Concierge.Shared.Web.UnconfiguredWebSearch();
			}
		});

		builder.Services.AddSingleton<InteractiveToolApprovalService>();
		builder.Services.AddSingleton<IToolApprovalService>(sp =>
			new AuditingToolApprovalService(
				sp.GetRequiredService<InteractiveToolApprovalService>(),
				sp.GetRequiredService<IToolApprovalAuditLog>()));
		builder.Services.AddConciergeRuntime();
		builder.Services.AddConciergeState(Path.Combine(FileSystem.AppDataDirectory, "state"));

		// BYO API-key cloud runtimes + cloud design adapters. The factories read from MAUI's
		// IConfiguration when present (env vars / appsettings.json bundled as MauiAsset);
		// missing keys leave the runtime in the "needs key" state without breaking startup.
		builder.Services.AddOpenAiChat(sp => sp.GetService<IConfiguration>()?.GetSection("OpenAI").Get<OpenAiChatOptions>() ?? new OpenAiChatOptions());
		builder.Services.AddAnthropicChat(sp => sp.GetService<IConfiguration>()?.GetSection("Anthropic").Get<AnthropicChatOptions>() ?? new AnthropicChatOptions());
		builder.Services.AddGeminiChat(sp => sp.GetService<IConfiguration>()?.GetSection("Gemini").Get<GeminiChatOptions>() ?? new GeminiChatOptions());
		builder.Services.AddConciergePenPotDiagrams(sp => sp.GetService<IConfiguration>()?.GetSection("PenPot").Get<PenPotApiOptions>() ?? new PenPotApiOptions());
		builder.Services.AddConciergeFigmaDiagrams(sp => sp.GetService<IConfiguration>()?.GetSection("Figma").Get<FigmaApiOptions>() ?? new FigmaApiOptions());
		builder.Services.AddOpenAiImages(sp => sp.GetService<IConfiguration>()?.GetSection("OpenAIImages").Get<OpenAiImageOptions>() ?? new OpenAiImageOptions());
		builder.Services.AddStabilityImages(sp => sp.GetService<IConfiguration>()?.GetSection("Stability").Get<StabilityImageOptions>() ?? new StabilityImageOptions());
		builder.Services.AddOpenAiVoice(sp => sp.GetService<IConfiguration>()?.GetSection("OpenAIVoice").Get<OpenAiVoiceOptions>() ?? new OpenAiVoiceOptions());
		builder.Services.AddConciergeMediaCloudDefaults();

		// Replace the NullDeviceContext registered by AddConciergeAi with the MAUI-aware one.
		builder.Services.RemoveAll<IDeviceContext>();
		builder.Services.AddSingleton<IDeviceContext, MauiDeviceContext>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
