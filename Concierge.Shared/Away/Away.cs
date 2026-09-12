namespace Concierge.Shared.Away;

/// <summary>
/// What a device knows about where it is when somebody speaks into it.
///
/// Every field but the first two is nullable and stays null unless the sensor actually
/// answered. **A blank is a blank** — the board renderer's rule, and the reason it exists:
/// an invented number is slop the moment it is invented, and a location of 0,0 is worse
/// than no location because something downstream will believe it.
/// </summary>
/// <param name="At">When it was said, on the device that heard it — not when it arrived.</param>
/// <param name="Device">What heard it, in plain words: "watch", "phone".</param>
/// <param name="Motion">"still", "walking", "running", "driving" — whatever the platform reports.</param>
public sealed record AwayContext(
    DateTimeOffset At,
    string Device,
    double? Latitude = null,
    double? Longitude = null,
    string? Motion = null,
    int? HeartRate = null,
    double? AmbientLux = null)
{
    /// <summary>
    /// The situation as one line for a model to read, or null when nothing is known beyond
    /// the device and the time.
    ///
    /// Written out rather than handed over as fields because it rides in the system prompt,
    /// where a sentence costs less than a JSON blob and is read more reliably by a small
    /// model.
    /// </summary>
    public string? AsSaid()
    {
        var parts = new List<string> { $"Said from a {Device}", $"at {At:HH:mm} on {At:d MMMM}" };

        if (Motion is { Length: > 0 })
        {
            parts.Add(Motion);
        }

        if (Latitude is { } lat && Longitude is { } lon)
        {
            parts.Add($"near {lat:0.###}, {lon:0.###}");
        }

        if (AmbientLux is { } lux)
        {
            parts.Add(lux < 50 ? "in the dark" : lux > 5000 ? "outdoors in daylight" : "indoors");
        }

        if (HeartRate is { } bpm)
        {
            parts.Add($"heart rate {bpm}");
        }

        return string.Join(", ", parts) + ".";
    }
}

/// <summary>A sentence from a device that is not here.</summary>
public sealed record AwaySaid(string Text, AwayContext Context);

/// <summary>What came of it.</summary>
/// <param name="Understood">Whether anything changed.</param>
/// <param name="What">What changed, in the words the moments strip uses — "Added a title".</param>
/// <param name="Reply">
/// What to say back. On a face this is the whole answer, so it has to be a sentence rather
/// than a status.
/// </param>
public sealed record AwayAnswer(bool Understood, string What, string? Reply);

/// <summary>Something waiting on a person, small enough to decide on a wrist.</summary>
public sealed record AwayAsk(Guid Id, string Tool, string Summary, string Risk, DateTimeOffset AskedAt);

/// <summary>
/// What a device that is not here can do.
///
/// **Deliberately says nothing about how it travels.** HTTP today; the mesh at Stage 3,
/// carrying the same records. A watch that spoke HTTP directly would have to be rewritten
/// when the transport changes, and the whole point of the watch is that it does not care
/// which stage answers it.
/// </summary>
public interface IAway
{
    /// <summary>Say something, and hear what came of it.</summary>
    Task<AwayAnswer> SayAsync(AwaySaid said, CancellationToken cancellationToken = default);

    /// <summary>What is waiting on a person, oldest first.</summary>
    Task<IReadOnlyList<AwayAsk>> WaitingAsync(CancellationToken cancellationToken = default);

    /// <summary>Answer one of them.</summary>
    Task<bool> AnswerAsync(Guid id, bool allowed, CancellationToken cancellationToken = default);
}
