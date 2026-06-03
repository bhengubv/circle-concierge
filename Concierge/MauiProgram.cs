using CircleAI.Core;
using Concierge.Ai;
using Concierge.Hosting;
using Concierge.Media;
using Concierge.Mesh;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Diagrams;
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
			.AddConciergeAi()
			.AddConciergeMesh()
			.AddConciergeMedia();
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
