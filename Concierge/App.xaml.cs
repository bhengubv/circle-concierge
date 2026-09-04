using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Concierge;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// MAUI builds a service provider but runs no host, so nothing starts the services
		// registered with AddHostedService — including the on-device model loader. Without
		// this line the engine never leaves "Engine queued for load…" and the app can never
		// answer anything. It returns immediately; loading a model takes minutes.
		ConciergeHostedServices.StartInBackground(_services);

		var window = new Window(new MainPage()) { Title = "Concierge" };

		// The bottom navigation is pinned to the bottom of the window by
		// height:100dvh. A window taller than the screen therefore hides it
		// completely. This keeps the window inside the display it is on, and
		// re-checks whenever that display changes.
		WindowFit.Apply(window);

		return window;
	}

	// ── MAUI lifecycle hooks ─────────────────────────────────────────────
	//
	// On Android these fire when the OS sends the activity to the background
	// and again when it brings it back. CircleAI's IPersistableChatRuntime
	// uses them to snapshot the active model's KV cache so the conversation
	// survives an OOM kill (Android kills our process freely while we're
	// backgrounded). Cloud runtimes don't implement the interface, so the
	// hooks are no-ops for them.
	//
	// We deliberately fire-and-forget — OnSleep must return quickly or the
	// OS reports an ANR. If the snapshot doesn't complete before the kill,
	// the next launch starts cold (still correct, just slower first token).

	protected override void OnSleep()
	{
		base.OnSleep();
		var runtime = _services.GetService<IChatRuntime>();
		if (runtime is IPersistableChatRuntime persistable
			&& !string.IsNullOrEmpty(persistable.SessionSnapshotPath))
		{
			var path = persistable.SessionSnapshotPath;
			_ = Task.Run(async () =>
			{
				try
				{
					await persistable.SaveSessionAsync(path).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_services.GetService<ILogger<App>>()?.LogWarning(ex,
						"OnSleep snapshot failed; next resume will start cold.");
				}
			});
		}
	}

	protected override void OnResume()
	{
		base.OnResume();
		var runtime = _services.GetService<IChatRuntime>();
		if (runtime is IPersistableChatRuntime persistable
			&& !string.IsNullOrEmpty(persistable.SessionSnapshotPath))
		{
			var path = persistable.SessionSnapshotPath;
			_ = Task.Run(async () =>
			{
				try
				{
					await persistable.LoadSessionAsync(path).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_services.GetService<ILogger<App>>()?.LogWarning(ex,
						"OnResume hydrate failed; conversation continues from a cold session.");
				}
			});
		}
	}
}
