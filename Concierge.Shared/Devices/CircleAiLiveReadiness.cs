using CircleAI.Assistant.Lifecycle;
using CircleAI.Linking;

namespace Concierge.Shared.Devices;

/// <summary>Projects the service's live state, never its inventory or generic canTalk flag.</summary>
public static class CircleAiLiveReadiness
{
    public static ReadinessSnapshot Read(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var (_, _, snapshot) = LinkAskRows.ReadStatus(rows);
        if (snapshot is null || snapshot.Schema != ReadinessSnapshot.CurrentSchema
            || string.IsNullOrWhiteSpace(snapshot.BootId))
            throw new InvalidOperationException("Circle AI did not provide a supported live readiness report.");
        return snapshot;
    }

    public static CircleAiServiceChatStatus Chat(ReadinessSnapshot snapshot, string version)
    {
        var brain = snapshot.Brain;
        var fetch = snapshot.Components.FirstOrDefault(c => c.Component == ReadinessComponents.Fetch);
        var ready = snapshot.CanAnswerNow || brain.Phase == BrainPhase.Ready;
        var waiting = !ready && (snapshot.CanAnswerSoon
            || brain.Phase is BrainPhase.Installed or BrainPhase.Loading
            || brain.Phase == BrainPhase.NoModel && fetch?.Phase is BrainPhase.Loading or BrainPhase.Paused);
        var explanation = ready ? "Text conversations are ready on this device. Other features have separate readiness below."
            : snapshot.CanAnswerSoon ? "Circle AI is preparing to answer. You can ask now and it will keep the request while it gets ready."
            : brain.Phase switch
        {
            BrainPhase.Installed => "The text model is downloaded. Circle AI will load it when you send a question.",
            BrainPhase.Loading => "Circle AI is loading the text model. Your question waits here until it can answer.",
            BrainPhase.NoModel => "The text model is not ready yet. Your draft is kept while Circle AI prepares its resources.",
            BrainPhase.Paused => "Circle AI preparation is paused.",
            BrainPhase.Failed => "Circle AI could not prepare the text model.",
            _ => "Circle AI has not confirmed text readiness. Your draft is kept."
        };
        if (!string.IsNullOrWhiteSpace(brain.Detail) && !ready)
            explanation += " " + brain.Detail;
        if (fetch is not null && fetch.Phase != BrainPhase.Unknown && !string.IsNullOrWhiteSpace(fetch.Detail))
            explanation += " Downloads: " + fetch.Detail;
        return new(ready, brain.Phase.ToString(), explanation, version,
            waiting, Fraction(fetch?.Progress));
    }

    public static IReadOnlyList<CircleAiSettingFact> Facts(ReadinessSnapshot snapshot)
    {
        var facts = new List<CircleAiSettingFact>();
        foreach (var id in new[] { ReadinessComponents.Brain, ReadinessComponents.Voice,
            ReadinessComponents.Ears, ReadinessComponents.Wake, ReadinessComponents.Fetch, ReadinessComponents.Assistant })
        {
            var component = id == ReadinessComponents.Brain ? snapshot.Brain
                : snapshot.Components.FirstOrDefault(c => c.Component == id);
            var name = id switch
            {
                ReadinessComponents.Brain => "Text conversations",
                ReadinessComponents.Voice => "Spoken replies",
                ReadinessComponents.Ears => "Speech recognition",
                ReadinessComponents.Wake => "Wake word",
                ReadinessComponents.Fetch => "Resource downloads",
                _ => "Android assistant role"
            };
            var value = id == ReadinessComponents.Brain && snapshot.CanAnswerNow ? "Ready"
                : id == ReadinessComponents.Brain && snapshot.CanAnswerSoon ? "Preparing"
                : component is null ? "Not reported" : component.Phase switch
            {
                BrainPhase.Ready => "Ready",
                BrainPhase.Installed => id == ReadinessComponents.Assistant ? "Not selected" : "Downloaded; not loaded",
                BrainPhase.NoModel => "Model not ready",
                BrainPhase.Loading => component.Step == LoadStep.None ? "Preparing" : "Preparing · " + component.Step,
                BrainPhase.Paused => "Paused · " + component.Pause,
                BrainPhase.Failed => "Failed",
                _ => "Not reported"
            };
            if (Fraction(component?.Progress) is double fraction) value += $" · {fraction:P0}";
            facts.Add(new(name, value, component?.Detail ?? "Circle AI has not reported live readiness for this feature."));
        }
        // Include additional components supplied by future service builds without guessing readiness.
        foreach (var c in snapshot.Components.Where(c => c.Component is not
            (ReadinessComponents.Brain or ReadinessComponents.Voice or ReadinessComponents.Ears
             or ReadinessComponents.Wake or ReadinessComponents.Fetch or ReadinessComponents.Assistant)))
            facts.Add(new(c.Component, c.Phase.ToString(), c.Detail));
        var reportedSettings = snapshot.Settings.Select(setting => setting.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in snapshot.Settings)
            facts.Add(new(SettingName(setting.Key), setting.Value, SettingExplanation(setting.Key)));
        // Keep the two consumer-facing turn controls visible even when an older
        // service omits them. Hiding an absent setting looks like a supported,
        // enabled default when it may simply be unavailable on this build.
        foreach (var key in new[] { ReadinessSettings.TurnStreaming, ReadinessSettings.TurnAgentic })
            if (!reportedSettings.Contains(key))
                facts.Add(new(SettingName(key), "Not reported",
                    "This service build did not report the setting. Concierge cannot change it through the published link."));
        foreach (var failure in snapshot.RecentFailures.Take(5))
            facts.Add(new($"Recent failure · {failure.Kind}", failure.Detail,
                $"{failure.Phase} · {failure.Certainty} · {failure.At:g}"
                + (string.IsNullOrWhiteSpace(failure.ModelId) ? "" : $" · {failure.ModelId}")));
        return facts;
    }

    private static string SettingName(string key) => key switch
    {
        ReadinessSettings.TurnStreaming => "Stream answers",
        ReadinessSettings.TurnAgentic => "Agentic work",
        ReadinessSettings.TurnImages => "Image input",
        ReadinessSettings.AudioMaxBytes => "Maximum audio size",
        ReadinessSettings.AudioFormat => "Audio format",
        ReadinessSettings.ModelId => "Selected model",
        ReadinessSettings.MaxTokensSpoken => "Spoken answer limit",
        ReadinessSettings.MaxTokensTyped => "Typed answer limit",
        ReadinessSettings.MaxPromptChars => "Question size limit",
        ReadinessSettings.PerCallerQueued => "Requests allowed per app",
        ReadinessSettings.RetentionHours => "Request history retention",
        ReadinessSettings.TtsLanguages => "Speech languages",
        ReadinessSettings.AsrLanguages => "Recognition languages",
        ReadinessSettings.WakePhrase => "Wake phrase",
        ReadinessSettings.Listening => "Background listening",
        ReadinessSettings.CallerScopes => "Concierge access scopes",
        ReadinessSettings.LinkProtocol => "Link protocol",
        ReadinessSettings.LinkDescriptor => "Link interface",
        _ => key
    };

    private static string SettingExplanation(string key) => key switch
    {
        ReadinessSettings.TurnStreaming => "Reported by Circle AI. Concierge displays answer chunks as the service produces them.",
        ReadinessSettings.TurnAgentic => "Reported service default. Concierge still sends agentic work only when the person selects Act freely.",
        ReadinessSettings.TurnImages => "Reported by Circle AI; the published Concierge chat request currently accepts text only.",
        ReadinessSettings.ModelId => "Selected by Circle AI's resource selector; Concierge does not choose models.",
        ReadinessSettings.Listening => "Reported by Circle AI. The listener can also be started or stopped in Voice settings.",
        _ => "Reported by Circle AI. This service link exposes the value as read-only."
    };

    public static double? Fraction(double? value) => value is double n && double.IsFinite(n) && n is >= 0 and <= 1 ? n : null;
}
