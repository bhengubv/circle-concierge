using System.Text.Json;

namespace Concierge.Shared.Design;

/// <summary>One design that was made, reduced to the axes worth varying.</summary>
/// <param name="Look">Which look it wore.</param>
/// <param name="Making">What was being made.</param>
/// <param name="At">When.</param>
public sealed record DesignMade(string Look, string Making, DateTimeOffset At);

/// <summary>What has been made lately.</summary>
public interface IDesignLog
{
    /// <summary>Records one.</summary>
    Task RecordAsync(string look, DesignMedium making, CancellationToken cancellationToken = default);

    /// <summary>The most recent, newest first.</summary>
    Task<IReadOnlyList<DesignMade>> RecentAsync(int count = 5, CancellationToken cancellationToken = default);
}

/// <summary>
/// A short memory of what has been made, so the next one differs.
///
/// The failure this exists to prevent is specific and it is not hypothetical: a
/// model driving a canvas will pick the same look every time. Not because the look
/// is right, but because it is the first in the list and the safest-sounding word.
/// Six looks and one of them used forever is the same product as one look, and
/// nobody notices until every page anybody made looks identical.
///
/// Taken from hallmark, which writes macrostructure, theme and enrichment to
/// `.hallmark/log.json` on every build and requires the next to differ from the
/// last three to five on named axes. It is the only mechanism in that whole corpus
/// aimed at the model's *tendency* rather than its output, which is why it carries
/// over even though almost nothing else about that skill applies here.
///
/// Two deliberate limits.
///
/// **It informs, it does not enforce.** The recent list is handed to the model with
/// the reason; nothing rejects a repeat. A canvas that refused to wear Calm twice
/// would be arguing with somebody who asked for Calm twice, and the person asking
/// is right. Variety is a default, not a rule.
///
/// **It is short on purpose.** Five entries, and the file is truncated to keep it
/// that way. A long history would let a model reason about trends nobody asked it
/// to notice, and this is meant to answer one question: what did the last few look
/// like?
/// </summary>
public sealed class FileDesignLog : IDesignLog
{
    /// <summary>How many are kept. Beyond this the oldest fall off.</summary>
    public const int Keep = 5;

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public FileDesignLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Where it is written, so somebody can read or delete it.</summary>
    public string Path => _path;

    public async Task RecordAsync(
        string look, DesignMedium making, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(look))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var entries = await ReadAsync(cancellationToken).ConfigureAwait(false);

            entries.Insert(0, new DesignMade(look, making.ToString(), DateTimeOffset.UtcNow));

            if (entries.Count > Keep)
            {
                entries.RemoveRange(Keep, entries.Count - Keep);
            }

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                _path, JsonSerializer.Serialize(entries, Json), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must never cost somebody a design. The
            // consequence of losing it is that the next look is picked without
            // knowing the last few, which is exactly where this started.
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DesignMade>> RecentAsync(
        int count = Keep, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var entries = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return entries.Take(Math.Max(0, count)).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<DesignMade>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var written = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize<List<DesignMade>>(written, Json) ?? [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable log is an empty one. It is a memory aid, and refusing
            // to open a canvas because the memory aid is corrupt would be letting
            // the least important thing here break the most important one.
            return [];
        }
    }
}
