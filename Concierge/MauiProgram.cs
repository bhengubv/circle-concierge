using CircleAI.Core;
using Concierge.Ai;
using Concierge.Shared.Rpc.Mcp;
using Concierge.Ai.Isolated;
using MudBlazor.Services;
using Fluxor;
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
using Concierge.Shared.Away;
using Concierge.Shared.Butler;
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
#if ANDROID
using Concierge.Platforms.Android;
#endif

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

#if ANDROID
		// The encoder, named before anything is registered.
		//
		// This has to happen here rather than anywhere later, and the ordering is the
		// whole of it: AddConciergeTools decides at registration time whether there is
		// an encoder, and registers design_save, the three media tools and the five
		// music ones only if there is. That decision is never revisited, so an encoder
		// announced after the container is built announces itself to nobody.
		//
		// Android is the one head where ffmpeg is a library rather than a program.
		// Everywhere else the default already starts a process and this is not run.
		Concierge.Shared.Design.Encoders.Runner =
			new Concierge.Platforms.Android.FFmpegKitEncoder();
#endif

		var builder = MauiApp.CreateBuilder();
#if WINDOWS
        builder.Services.AddSingleton<Concierge.Platforms.Windows.DesktopSummon>();
        builder.Services.AddSingleton<IDesktopSummon>(sp => sp.GetRequiredService<Concierge.Platforms.Windows.DesktopSummon>());
#endif

#if WINDOWS && DEBUG
		// Development-only unauthenticated LAN bridge for proving phone-to-desktop
		// hand-off. Release needs paired TLS before this can be enabled there.
		builder.Services.AddSingleton<DesktopAwayHost>();
#endif
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddFluxor(options => options.ScanAssemblies(
			typeof(Concierge.Shared.Components.State.RunningConversationsState).Assembly));
#if ANDROID
		builder.Services.AddSingleton<Concierge.Shared.Components.Workspace.IMicrophonePermissionGate,
			AndroidMicrophonePermissionGate>();
		Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper
			.AppendToMapping("ConciergeMicrophoneCapture", (handler, _) =>
			{
				var webView = handler.PlatformView;
				if (webView.WebChromeClient is not ConciergeMicrophoneWebChromeClient)
					webView.SetWebChromeClient(new ConciergeMicrophoneWebChromeClient(webView.WebChromeClient));
			});
#endif
		// MudBlazor — registers IDialogService, ISnackbar, IScrollManager, etc.
		// Needed by the flight-deck Dashboard's snackbar feedback + dialogs.
		builder.Services.AddMudServices();
		builder.Services.AddSingleton<IConciergeSecretStore>(_ => new LocalSecretStore());
		builder.Services.AddSingleton<IButlerSessionTokenStore, Concierge.Butler.MauiButlerSessionTokenStore>();
		builder.Services.AddHttpClient<ButlerAccountSession>(client =>
		{
			var authApi = builder.Configuration["AppState:AuthApiUrl"] ?? "https://authapi.thegeeknetwork.co.za";
			client.BaseAddress = new Uri(authApi);
			client.Timeout = TimeSpan.FromSeconds(20);
		});
		builder.Services.AddSingleton<IButlerAccountSession>(sp => sp.GetRequiredService<ButlerAccountSession>());
		builder.Services.AddHttpClient<ButlerWorkRunApiClient>(client =>
		{
			var butlerApi = builder.Configuration["App:ButlerApiBaseUrl"] ?? "https://butlerapi.thegeeknetwork.co.za";
			client.BaseAddress = new Uri(butlerApi);
			client.Timeout = TimeSpan.FromSeconds(30);
		});
		builder.Services.AddSingleton<IButlerWorkRunApi>(sp => sp.GetRequiredService<ButlerWorkRunApiClient>());
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
			.AddConciergeTranscription()
			.AddConciergeCodingTools()
			.AddConciergeMusicLibrary()
			.AddConciergeStockFootage()
			.AddConciergeImageTools()
			.AddConciergeMusicCatalogue()
			.AddMauiDeviceCapabilities()
			// Establish the base AI registrations, then replace the chat runtime
			// and its loader with the platform boundary below. Windows and Android
			// consume standalone Circle AI services, which own model preparation.
			// Other MAUI heads retain the isolated child-process adapter.
			.AddConciergeAi(new CircleAiChatOptions
			{
				AllowAutomaticDownload = false,
				StageEligibleModelsSmallestFirst = true,
#if ANDROID
				ResumeAcceptedDownloadOnStartup = false,
				NativeLibDir = Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir,
#endif
			})
#if WINDOWS
			.AddConciergeAiWindowsService()
#elif ANDROID
			.AddConciergeAiSharedServiceChat()
#else
			.AddConciergeAiIsolated()
#endif
			.AddConciergeMesh()
			.AddConciergeMedia()
			// Parental controls / content-filter pipeline. Wraps IChatRuntime
			// registered above; pass-through when Strictness = Off.
			.AddConciergeSafety();


		// Resolve the saved key on the first search without blocking tool construction
		// or the Blazor dispatcher. As before, a changed key takes effect on restart.
		builder.Services.AddSingleton<Concierge.Shared.Web.IWebSearch>(sp =>
			new Concierge.Shared.Web.SecretBackedWebSearch(
				sp.GetRequiredService<IConciergeSecretStore>()));

		builder.Services.AddSingleton<InteractiveToolApprovalService>();
		builder.Services.AddSingleton<IToolApprovalService>(sp =>
			new AuditingToolApprovalService(
				sp.GetRequiredService<InteractiveToolApprovalService>(),
				sp.GetRequiredService<IToolApprovalAuditLog>(),
				sp.GetRequiredService<IConversationStore>()));
		builder.Services.AddConciergeRuntime();
		// A device that is not here can reach the making. Registering it opens nothing;
		// only a head with an endpoint can actually be reached.
		builder.Services.AddConciergeAway();
		builder.Services.AddSingleton<Concierge.Away.AwayClient>(_ =>
		{
			var client = new Concierge.Away.AwayClient { DeviceLabel = "phone" };
#if ANDROID
			client.DiscoveryLease = Concierge.Platforms.Android.WifiMulticastLease.Acquire;
			client.DeviceName = Android.Provider.Settings.Global.GetString(Android.App.Application.Context.ContentResolver, "device_name") ?? DeviceInfo.Current.Name;
			client.DeviceId = Android.Provider.Settings.Secure.GetString(Android.App.Application.Context.ContentResolver, Android.Provider.Settings.Secure.AndroidId) ?? client.DeviceName;
			client.DevicePlatform = DeviceInfo.Current.Model.Contains("Circle", StringComparison.OrdinalIgnoreCase) ? "CircleOS" : "AndroidOS";
#elif WINDOWS
			client.PointAt("http://127.0.0.1:5198");
#endif
			return client;
		});
		builder.Services.AddSingleton<Concierge.Shared.Planning.IProjectAgentRuntimeSelector,
			Concierge.Away.AwayProjectAgentRuntimeSelector>();
		builder.Services.AddConciergeState(Path.Combine(FileSystem.AppDataDirectory, "state"));

		// Work that runs end to end, keeping its place on disk so an interrupted run
		// carries on rather than starting again.
		builder.Services.AddConciergeRoutines(Path.Combine(FileSystem.AppDataDirectory, "state"));

		// Podcasts, which need no account at all: a feed is published for anybody to read.
		builder.Services.AddConciergePodcasts(Path.Combine(FileSystem.AppDataDirectory, "state"));

		// Looking after a library on a schedule. Off until somebody turns it on, and it
		// only ever looks — a scheduled task runs with nobody there to ask.
		builder.Services.AddConciergeUpkeep(Path.Combine(FileSystem.AppDataDirectory, "state"));

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
		// Speech on the device, beside the cloud one. Both sit behind IVoiceRuntime; the
		// local one reports what it can do, which is nothing until its model files are here.
		builder.Services.AddConciergeLocalVoice();
		builder.Services.AddConciergeMediaCloudDefaults();

		// Replace the NullDeviceContext registered by AddConciergeAi with the MAUI-aware one.
		builder.Services.RemoveAll<IDeviceContext>();
		builder.Services.AddSingleton<IDeviceContext, MauiDeviceContext>();

#if ANDROID
		builder.Services.AddSingleton<Concierge.Platforms.Android.AndroidCircleAiSharedService>();
		builder.Services.AddSingleton<ICircleAiSharedService>(sp => sp.GetRequiredService<Concierge.Platforms.Android.AndroidCircleAiSharedService>());
		builder.Services.AddSingleton<ICircleAiServiceChatLink>(sp => sp.GetRequiredService<Concierge.Platforms.Android.AndroidCircleAiSharedService>());
		builder.Services.AddSingleton<ICircleAiServiceKnowledgeLink>(sp => sp.GetRequiredService<Concierge.Platforms.Android.AndroidCircleAiSharedService>());
		builder.Services.AddSingleton<ICircleAiServiceControls>(sp => sp.GetRequiredService<Concierge.Platforms.Android.AndroidCircleAiSharedService>());
		builder.Services.AddSingleton<ICircleAiServiceAudioLink>(sp => sp.GetRequiredService<Concierge.Platforms.Android.AndroidCircleAiSharedService>());
		builder.Services.AddSingleton<Concierge.Platforms.Android.CircleAiServiceVoiceRuntime>();
		builder.Services.AddSingleton<IVoiceRuntime>(sp => sp.GetRequiredService<Concierge.Platforms.Android.CircleAiServiceVoiceRuntime>());
		builder.Services.AddHostedService<Concierge.Platforms.Android.AndroidCircleAiServiceVoiceProbe>();
		builder.Services.AddSingleton<ICircleAiSettingsGateway, Concierge.Platforms.Android.AndroidCircleAiSettingsGateway>();
        // The shared service owns preparation; do not schedule Concierge's legacy model jobs.
#endif

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
