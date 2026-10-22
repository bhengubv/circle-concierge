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
        var waiting = brain.Phase is BrainPhase.Installed or BrainPhase.Loading
            || brain.Phase == BrainPhase.NoModel && fetch?.Phase is BrainPhase.Loading or BrainPhase.Paused;
        var explanation = brain.Phase switch
        {
            BrainPhase.Ready => "Text conversations are ready on this device. Other features have separate readiness below.",
            BrainPhase.Installed => "The text model is downloaded. Circle AI will load it when you send a question.",
            BrainPhase.Loading => "Circle AI is loading the text model. Your question waits here until it can answer.",
            BrainPhase.NoModel => "The text model is not ready yet. Your draft is kept while Circle AI prepares its resources.",
            BrainPhase.Paused => "Circle AI preparation is paused.",
            BrainPhase.Failed => "Circle AI could not prepare the text model.",
            _ => "Circle AI has not confirmed text readiness. Your draft is kept."
        };
        if (!string.IsNullOrWhiteSpace(brain.Detail) && brain.Phase != BrainPhase.Ready)
            explanation += " " + brain.Detail;
        if (fetch is not null && fetch.Phase != BrainPhase.Unknown && !string.IsNullOrWhiteSpace(fetch.Detail))
            explanation += " Downloads: " + fetch.Detail;
        return new(brain.Phase == BrainPhase.Ready, brain.Phase.ToString(), explanation, version,
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
            var value = component is null ? "Not reported" : component.Phase switch
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
        return facts;
    }

    public static double? Fraction(double? value) => value is double n && double.IsFinite(n) && n is >= 0 and <= 1 ? n : null;
}
