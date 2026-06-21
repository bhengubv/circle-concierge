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
		return new Window(new MainPage()) { Title = "Concierge" };
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
