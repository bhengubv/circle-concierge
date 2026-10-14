using Android.Content;
using CircleAI.Linking;
using Concierge.Shared.Devices;

namespace Concierge.Platforms.Android;

public sealed class AndroidCircleAiSettingsGateway(AndroidCircleAiSharedService service) : ICircleAiSettingsGateway, ICircleAiVoiceSettingsLink, ICircleAiServiceSetup
{

    public async Task StartSetupAsync(CancellationToken cancellationToken)
    {
        var reply = await service.ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.SetupStart), cancellationToken);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "Circle AI could not start setup.");
    }

    public async Task<CircleAiSetupProgress> ReadSetupProgressAsync(CancellationToken cancellationToken)
    {
        var readiness = await service.ReadChatStatusAsync(cancellationToken);
        if (readiness.Ready) return new(true, readiness.Explanation, null, null, null);
        var reply = await service.ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.SetupProgress), cancellationToken);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "Circle AI did not report setup progress.");
        var row = reply.Rows.FirstOrDefault();
        if (row is null) return new(false, readiness.Explanation, null, null, null);
        if (row.Count < 6) throw new InvalidOperationException("Circle AI returned an invalid setup progress report.");
        var index = LinkSetupRows.Number(row, 0, -1);
        var count = LinkSetupRows.Number(row, 1, 0);
        var remaining = LinkSetupRows.Fraction(row, 4, -1);
        return new(false, readiness.Explanation, row[2], CircleAiSetupProgress.ParseFraction(row[3]), row[5],
            row[5] is "Fetching" or "Checking",
            index >= 0 && index < count && count <= int.MaxValue ? (int)index + 1 : null,
            count > 0 && count <= int.MaxValue ? (int)count : null,
            double.IsFinite(remaining) && remaining > 0 ? remaining : null);
    }

    public async Task<CircleAiSettingsReport> ReadAsync(CancellationToken cancellationToken)
    {
        var sections = new List<CircleAiSettingsSection>();
        sections.Add(new("language", "Language and replies",
            "Circle AI owns input language and reply language. The installed service link does not expose these preferences for Concierge to read or change.", []));
        sections.Add(new("voice", "Voice and wake",
            "Background listening status comes from Circle AI. Permission and running status below are separate from your saved preference. Saved listening preferences are not yet editable in this panel.",
            await Facts(LinkVerb.ResidentStatus, cancellationToken)));
        sections.Add(new("readiness", "Readiness and capabilities",
            "These are reports from the service on this device. Supported does not mean ready or currently running.",
            [.. await Facts(LinkVerb.SetupReadiness, cancellationToken), .. await Facts(LinkVerb.Abilities, cancellationToken),
             .. await Facts(LinkVerb.SetupPlan, cancellationToken), .. await Facts(LinkVerb.SetupCensus, cancellationToken)]));
        sections.Add(new("permissions", "Permissions and setup",
            "Microphone access is needed to hear you; notifications show background listening. Turning either off can prevent listening or hide its notification. Android controls these permissions.",
            await Facts(LinkVerb.Permissions, cancellationToken)));
        sections.Add(new("storage", "Storage and housekeeping",
            "Service storage is separate from Concierge. The installed link reports storage but does not expose scratch retention or cache-limit setters. Removing reusable resources may require downloading them again. Concierge does not delete the service's files.",
            await Facts(LinkVerb.Footprint, cancellationToken)));
        sections.Add(new("borrowing", "Trusted-device assistance",
            "Circle AI owns its borrowing preference. The installed link does not expose that preference for Concierge to read or change. Concierge's manual transfer to Desktop is a separate action you control.", []));
        return new("Checked Circle AI on this device. Any unavailable report is labelled below.", sections, false, true);
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

    public Task OpenCircleAiAsync() => Task.FromException(new NotSupportedException(
        "Service settings belong in Concierge; a separate controls app is not used."));

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
