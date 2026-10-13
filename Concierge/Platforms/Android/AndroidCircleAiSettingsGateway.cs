using Android.Content;
using CircleAI.Linking;
using Concierge.Shared.Devices;

namespace Concierge.Platforms.Android;

public sealed class AndroidCircleAiSettingsGateway(AndroidCircleAiSharedService service) : ICircleAiSettingsGateway, ICircleAiVoiceSettingsLink
{
    private const string ControlsPackage = "com.bhengubv.circleai.hybrid";

    public async Task<CircleAiSettingsReport> ReadAsync(CancellationToken cancellationToken)
    {
        var sections = new List<CircleAiSettingsSection>();
        sections.Add(new("language", "Language and replies",
            "Choose your input language and whether replies follow the speaker or use one language. Open Circle AI, then Settings → Language. These preferences are not writable through this service link.", [], true));
        sections.Add(new("voice", "Voice and wake",
            "Choose a wake phrase and foreground or screen-off listening in Circle AI → Settings → Language / Phone. Turning listening off means speaking requires a tap. Permission and running status below are separate from your saved preference.",
            await Facts(LinkVerb.ResidentStatus, cancellationToken), true));
        sections.Add(new("readiness", "Readiness and capabilities",
            "These are reports from the service on this device. Supported does not mean ready or currently running.",
            [.. await Facts(LinkVerb.SetupReadiness, cancellationToken), .. await Facts(LinkVerb.Abilities, cancellationToken)]));
        sections.Add(new("permissions", "Permissions and setup",
            "Microphone access is needed to hear you; notifications show background listening. Turning either off can prevent listening or hide its notification. Android controls these permissions.",
            await Facts(LinkVerb.Permissions, cancellationToken)));
        sections.Add(new("storage", "Storage and housekeeping",
            "Service storage is separate from Concierge. Set scratch retention and cache limits in Circle AI → Settings → Phone → This phone. Shorter limits remove older temporary audio; removing reusable resources may require downloading them again. Concierge does not delete the service's files.",
            await Facts(LinkVerb.Footprint, cancellationToken), true));
        sections.Add(new("borrowing", "Trusted-device assistance",
            "Configure Borrowing in Circle AI → Settings → Phone. Off means Circle AI cannot borrow another device's resources through that setting. Questions can contain personal information: choose only a device you trust. This link does not report your borrowing preference.", [], true));
        var context = global::Android.App.Application.Context;
        var canOpen = context.PackageManager!.GetLaunchIntentForPackage(ControlsPackage) is not null;
        return new("Checked Circle AI on this device. Any unavailable report is labelled below.", sections, canOpen, true);
    }

    private async Task<IReadOnlyList<CircleAiSettingFact>> Facts(LinkVerb verb, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await service.ExecuteVerbAsync(new LinkVerbRequest(verb), cancellationToken);
            if (!reply.Ok) return [new("Service report", "Unavailable", reply.Error)];
            return CircleAiSettingsRows.Project(verb.ToString(), reply.Rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) { return [new("Service report", "Unavailable", e.Message)]; }
    }

    public Task OpenCircleAiAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        var context = global::Android.App.Application.Context;
        using var intent = context.PackageManager!.GetLaunchIntentForPackage(ControlsPackage)
            ?? throw new InvalidOperationException("Circle AI controls are not installed.");
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    });

    public Task<bool> RequestVoiceConnectionAsync(CancellationToken cancellationToken)
        => MainThread.InvokeOnMainThreadAsync(() =>
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity
                ?? throw new InvalidOperationException("Open Concierge before connecting voice settings.");
            return activity.RequestCircleAiLinkAsync(cancellationToken, LinkScope.Chat | LinkScope.Voice);
        });

    public Task OpenPermissionsAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        var context = global::Android.App.Application.Context;
        using var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
            global::Android.Net.Uri.Parse("package:" + LinkIpc.HostPackage));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    });
}
