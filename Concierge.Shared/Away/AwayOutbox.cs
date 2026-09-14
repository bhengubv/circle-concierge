using System.Text.Json;

namespace Concierge.Away;

/// <summary>Something said that has not been delivered yet.</summary>
/// <param name="Id">Its own name on disk, so delivering it can remove exactly it.</param>
public sealed record AwayPending(Guid Id, string Text, Situation Situation);

/// <summary>
/// What was said while there was nowhere to send it.
///
/// **Out of range is the normal case for a wrist, not the edge one.** It goes where you go —
/// a lift, a basement, a train, a walk with the phone left at home — and a sentence lost
/// because of that is the product failing at exactly the moment it claims to be useful. So
/// it is written down and delivered when the network comes back.
/// </summary>
/// <remarks>
/// **The time travels with the sentence.** It is the device's own clock at the moment of
/// speaking, kept in the file, not stamped on arrival — something said on a train and
/// delivered forty minutes later is still something said on a train, and the whole point of
/// carrying the situation is lost if the situation is the one at the other end.
///
/// One file per sentence, named by when it was said, written beside and moved into place.
/// A single appended file would be smaller and would risk the thing this exists to prevent:
/// a write interrupted by a watch going to sleep leaves a half-line, and a half-line at the
/// end of a file can take the whole queue with it.
/// </remarks>
public sealed class AwayOutbox
{
    private static readonly JsonSerializerOptions Shape = new() { WriteIndented = false };

    private readonly string _folder;

    public AwayOutbox(string? folder = null)
        => _folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Concierge",
            "outbox");

    /// <summary>Where the unsent are kept.</summary>
    public string Folder => _folder;

    /// <summary>Keep it until it can go.</summary>
    public AwayPending Keep(string text, Situation situation)
    {
        var pending = new AwayPending(Guid.NewGuid(), text, situation);

        try
        {
            Directory.CreateDirectory(_folder);

            // Named by when it was said, zero-padded, so the ordinary directory order is the
            // order the sentences were spoken. Two in the same tick are separated by the id.
            var name = $"{situation.At.UtcTicks:D19}-{pending.Id:N}.json";
            var beside = Path.Combine(_folder, name + ".writing");

            File.WriteAllText(beside, JsonSerializer.Serialize(pending, Shape));
            File.Move(beside, Path.Combine(_folder, name), overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A watch with no room left cannot keep it, and there is nothing useful to do
            // about that here. The caller is already telling somebody it did not go.
        }

        return pending;
    }

    /// <summary>
    /// What is still waiting, oldest first.
    ///
    /// A file that cannot be read is skipped rather than throwing: one bad entry must not
    /// hold up everything said after it, which is the failure mode of a single queue file
    /// and the reason this is many.
    /// </summary>
    public IReadOnlyList<AwayPending> Waiting()
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        var waiting = new List<AwayPending>();

        foreach (var file in Directory.EnumerateFiles(_folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<AwayPending>(File.ReadAllText(file)) is { } pending)
                {
                    waiting.Add(pending);
                }
            }
            catch (Exception failure) when (failure is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable. Left where it is rather than deleted — it is somebody's
                // sentence, and losing it quietly is the thing this whole class is against.
            }
        }

        return waiting;
    }

    /// <summary>It went. Forget it.</summary>
    public void Done(Guid id)
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, $"*-{id:N}.json"))
            {
                File.Delete(file);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Still on disk, so it goes again next time. Saying the same thing twice is
            // recoverable; losing it is not, and that is the trade taken deliberately.
        }
    }

    /// <summary>How many are waiting, without reading them.</summary>
    public int Count()
    {
        try
        {
            return Directory.Exists(_folder) ? Directory.EnumerateFiles(_folder, "*.json").Count() : 0;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
