using System.Text.Json;

namespace Concierge.Shared.Diagnostics;

/// <summary>What kind of problem is being reported.</summary>
public enum FeedbackKind
{
    /// <summary>The assistant answered badly.</summary>
    BadAnswer = 0,

    /// <summary>Something broke.</summary>
    Crash = 1,

    /// <summary>The user wants something that is not there.</summary>
    Request = 2,
}

/// <summary>One thing a user told us, kept until it can be delivered.</summary>
/// <param name="Id">Stable identity so a delivered report can be marked without ambiguity.</param>
/// <param name="Message">What the user said.</param>
/// <param name="Kind">What sort of problem it is.</param>
/// <param name="ConversationId">The conversation it came from, when there was one.</param>
/// <param name="ReportedAt">When it was raised.</param>
/// <param name="SentAt">When it was delivered, or null while it is still waiting.</param>
public sealed record FeedbackReport(
    Guid Id,
    string Message,
    FeedbackKind Kind,
    Guid? ConversationId,
    DateTimeOffset ReportedAt,
    DateTimeOffset? SentAt);

/// <summary>
/// Where a user says something is wrong, from inside the app.
/// </summary>
/// <remarks>
/// This has to work with no network. On a device you cannot reach, in a place with no
/// signal, a report captured locally and delivered days later is the only bug report that
/// will ever arrive — and it is worth more than any crash log, because it says what the
/// person expected.
/// </remarks>
public interface IFeedbackChannel
{
    /// <summary>Record something the user told us.</summary>
    Task<FeedbackReport> ReportAsync(
        string message,
        FeedbackKind kind,
        Guid? conversationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reports not yet delivered, oldest first.</summary>
    Task<IReadOnlyList<FeedbackReport>> PendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Every report, delivered or not, oldest first.</summary>
    Task<IReadOnlyList<FeedbackReport>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>Mark reports delivered. They are kept, not removed.</summary>
    Task MarkSentAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);
}

/// <summary>Feedback kept in a JSON-lines file on the device.</summary>
public sealed class FileFeedbackChannel : IFeedbackChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public FileFeedbackChannel(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public async Task<FeedbackReport> ReportAsync(
        string message,
        FeedbackKind kind,
        Guid? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var report = new FeedbackReport(
            Guid.NewGuid(),
            message.Trim(),
            kind,
            conversationId,
            DateTimeOffset.UtcNow,
            SentAt: null);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reports = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            reports.Add(report);
            await WriteUnlockedAsync(reports, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return report;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeedbackReport>> PendingAsync(CancellationToken cancellationToken = default)
        => (await AllAsync(cancellationToken).ConfigureAwait(false)).Where(report => report.SentAt is null).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeedbackReport>> AllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkSentAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var delivered = ids.ToHashSet();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reports = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < reports.Count; index++)
            {
                if (delivered.Contains(reports[index].Id) && reports[index].SentAt is null)
                {
                    // Kept rather than deleted: a delivered complaint is still evidence, and
                    // the user may raise the same thing again if nothing changed.
                    reports[index] = reports[index] with { SentAt = DateTimeOffset.UtcNow };
                }
            }

            await WriteUnlockedAsync(reports, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<FeedbackReport>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var reports = new List<FeedbackReport>();
        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var report = JsonSerializer.Deserialize<FeedbackReport>(line, JsonOptions);
                if (report is not null)
                {
                    reports.Add(report);
                }
            }
            catch (JsonException)
            {
                // One unreadable line loses one report, not the file. A half-written last
                // line after a crash must not cost the user everything they reported.
            }
        }

        return reports;
    }

    private async Task WriteUnlockedAsync(List<FeedbackReport> reports, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var lines = reports.Select(report => JsonSerializer.Serialize(report, JsonOptions));
        await File.WriteAllLinesAsync(_path, lines, cancellationToken).ConfigureAwait(false);
    }
}
