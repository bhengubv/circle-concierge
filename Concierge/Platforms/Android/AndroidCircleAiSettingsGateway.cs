using Android.Content;
using CircleAI.Linking;
using CircleAI.Assistant.Lifecycle;
using Concierge.Shared.Devices;

namespace Concierge.Platforms.Android;

public sealed class AndroidCircleAiSettingsGateway(AndroidCircleAiSharedService service) : ICircleAiSettingsGateway, ICircleAiVoiceSettingsLink, ICircleAiServiceSetup, ICircleAiSettingsCacheControl
{
    public void InvalidateSettings() => service.InvalidateReads();

    public async Task StartSetupAsync(CancellationToken cancellationToken)
    {
        var approved = await service.RequestFullAccessAsync(cancellationToken);
        service.InvalidateReads();
        if (!approved) throw new InvalidOperationException("Circle AI setup was not approved. No download was requested.");
        var reply = await service.ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.SetupStart), cancellationToken);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "Circle AI could not start setup.");
    }

    public Task CancelSetupAsync(CancellationToken cancellationToken) => service.CancelSetupAsync(cancellationToken);

    public async Task<CircleAiSetupProgress> ReadSetupProgressAsync(CancellationToken cancellationToken)
    {
        var live = await service.ReadLiveReadinessAsync(cancellationToken);
        var chat = CircleAiLiveReadiness.Chat(live, "");
        var fetch = live.Components.FirstOrDefault(c => c.Component == ReadinessComponents.Fetch);
        // A usable text model does not mean voice resources or background upgrades are finished.
        if (fetch is not null && fetch.Phase is BrainPhase.Loading or BrainPhase.Paused or BrainPhase.Failed)
            return new(false, chat.Explanation, fetch.ModelId, CircleAiLiveReadiness.Fraction(fetch.Progress),
                fetch.Phase == BrainPhase.Paused ? "Paused · " + fetch.Pause : fetch.Phase.ToString(),
                fetch.Phase == BrainPhase.Loading);
        if (fetch?.Phase == BrainPhase.Ready)
            return new(true, chat.Explanation, fetch.ModelId, null, "Done");
        var reply = await service.ExecuteVerbAsync(new LinkVerbRequest(LinkVerb.SetupProgress), cancellationToken);
        if (!reply.Ok) return new(false, chat.Explanation + " Setup progress: " + reply.Error, null, null, null);
        var row = reply.Rows.FirstOrDefault();
        if (row is null) return new(false, chat.Explanation, null, null, null);
        if (row.Count < 6) throw new InvalidOperationException("Circle AI returned an invalid setup progress report.");
        var index = LinkSetupRows.Number(row, 0, -1);
        var count = LinkSetupRows.Number(row, 1, 0);
        var remaining = LinkSetupRows.Fraction(row, 4, -1);
        return new(row[5] == "Done", chat.Explanation, row[2], CircleAiSetupProgress.ParseFraction(row[3]), row[5],
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
            "Background listener state, wake phrase, and phrase quality come from Circle AI. Android microphone and notification grants are reported separately below.",
            await Facts(LinkVerb.ResidentStatus, cancellationToken)));
        IReadOnlyList<CircleAiSettingFact> liveFacts;
        try
        {
            liveFacts = CircleAiLiveReadiness.Facts(await service.ReadLiveReadinessAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) { liveFacts = [new("Text conversations", "Not confirmed", e.Message)]; }
        sections.Add(new("readiness", "Readiness and capabilities",
            "Live status from Circle AI. Each feature is checked separately. Downloaded files and supported features do not prove a model is loaded or ready.", liveFacts));
        try
        {
            var manifest = await service.InspectAsync(cancellationToken);
            var manifestFacts = manifest.Capabilities.Select(capability => new CircleAiSettingFact(
                capability.Id, capability.ImplementationStatus, capability.Summary)).ToArray();
            sections.Add(new("manifest", "Circle AI capability manifest",
                "Capabilities reported by the installed service. Concierge uses these reports as the source of truth; a listed capability is not proof its resources are ready.",
                manifestFacts.Length == 0 ? [new("Capability manifest", "Not reported", manifest.Status)] : manifestFacts));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            sections.Add(new("manifest", "Circle AI capability manifest",
                "Capabilities reported by the installed service. A missing report is not treated as an available feature.",
                [new("Capability manifest", "Unavailable", error.Message)]));
        }
        sections.Add(new("resources", "Resource plan and downloaded files",
            "Circle AI selects suitable models automatically. These are its capability and file reports, not settings or proof of readiness. A blocked resource plan needs attention in Circle AI, not a model choice from you.",
            [.. await Facts(LinkVerb.Abilities, cancellationToken), .. await Facts(LinkVerb.SetupPlan, cancellationToken),
             .. await Facts(LinkVerb.SetupCensus, cancellationToken)]));
        sections.Add(new("permissions", "Permissions and setup",
            "Microphone access is needed to hear you; notifications show background listening. Turning either off can prevent listening or hide its notification. Android controls these permissions.",
            await Facts(LinkVerb.Permissions, cancellationToken)));
        sections.Add(new("storage", "Storage and housekeeping",
            "Service storage is separate from Concierge. The installed link reports storage but does not expose scratch retention or cache-limit setters. Removing reusable resources may require downloading them again. Concierge does not delete the service's files.",
            await Facts(LinkVerb.Footprint, cancellationToken)));
        sections.Add(new("borrowing", "Trusted-device assistance",
            "Circle AI owns its borrowing preference. The installed link does not expose that preference for Concierge to read or change. Concierge's manual transfer to Desktop is a separate action you control.", []));
        sections.Add(new("health", "Service health and decisions",
            "Live reports from Circle AI itself. A missing report is shown as unavailable, never treated as a healthy result.",
            [.. await HealthFacts(cancellationToken), .. await DecisionFacts(cancellationToken)]));
        sections.Add(new("activity", "Requests sent to Circle AI",
            "Inspect accepted and completed service requests. Circle AI is the source of their state; Concierge never repeats them when reviewing history.", []));
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

    public async Task<bool> RequestVoiceConnectionAsync(CancellationToken cancellationToken)
    {
        service.InvalidateReads();
        try { return await service.RequestFullAccessAsync(cancellationToken); }
        finally { service.InvalidateReads(); }
    }

    private async Task<IReadOnlyList<CircleAiSettingFact>> HealthFacts(CancellationToken cancellationToken)
    {
        try
        {
            var facts = await service.ReadHealthAsync(cancellationToken);
            return facts.Count == 0 ? [new("Health report", "No health rows", "Circle AI returned an empty health report.")] :
                facts.Select(fact => new CircleAiSettingFact(fact.Key, fact.Value)).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return [new("Health report", "Unavailable", error.Message)]; }
    }

    private async Task<IReadOnlyList<CircleAiSettingFact>> DecisionFacts(CancellationToken cancellationToken)
    {
        try
        {
            var verdict = await service.ReadLastDecisionAsync(cancellationToken);
            return verdict is null ? [new("Last decision", "None reported")] :
                [new("Last decision", verdict.Text, $"Sequence {verdict.Sequence}")];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return [new("Last decision", "Unavailable", error.Message)]; }
    }

    public Task OpenPermissionsAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        service.InvalidateReads();
        var context = global::Android.App.Application.Context;
        using var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
            global::Android.Net.Uri.Parse("package:" + LinkIpc.HostPackage));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    });
}
