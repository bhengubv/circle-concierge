using CircleAI.Core;
using Concierge.Ai;
using Concierge.Chat.Cloud;
using Concierge.Diagrams.Design;
using Concierge.Hosting;
using Concierge.Media;
using Concierge.Mesh;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Diagrams;
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
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services
			.AddConciergeCore()
			.AddConciergeChat()
			.AddConciergeDiagrams()
			.AddConciergeMetrics()
			.AddConciergeTools()
			.AddConciergeAi()
			.AddConciergeMesh()
			.AddConciergeMedia();

		// BYO API-key cloud runtimes + cloud design adapters. The factories read from MAUI's
		// IConfiguration when present (env vars / appsettings.json bundled as MauiAsset);
		// missing keys leave the runtime in the "needs key" state without breaking startup.
		builder.Services.AddOpenAiChat(sp => sp.GetService<IConfiguration>()?.GetSection("OpenAI").Get<OpenAiChatOptions>() ?? new OpenAiChatOptions());
		builder.Services.AddAnthropicChat(sp => sp.GetService<IConfiguration>()?.GetSection("Anthropic").Get<AnthropicChatOptions>() ?? new AnthropicChatOptions());
		builder.Services.AddGeminiChat(sp => sp.GetService<IConfiguration>()?.GetSection("Gemini").Get<GeminiChatOptions>() ?? new GeminiChatOptions());
		builder.Services.AddConciergePenPotDiagrams(sp => sp.GetService<IConfiguration>()?.GetSection("PenPot").Get<PenPotApiOptions>() ?? new PenPotApiOptions());
		builder.Services.AddConciergeFigmaDiagrams(sp => sp.GetService<IConfiguration>()?.GetSection("Figma").Get<FigmaApiOptions>() ?? new FigmaApiOptions());

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
