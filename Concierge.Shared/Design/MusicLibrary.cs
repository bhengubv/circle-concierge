namespace Concierge.Shared.Design;

/// <summary>One track on disk, with what it says about itself.</summary>
/// <param name="Path">Where it is.</param>
/// <param name="Tags">What it says about itself.</param>
/// <param name="Bytes">How big it is.</param>
public sealed record LibraryTrack(string Path, MediaTags Tags, long Bytes);

/// <summary>Two or more files that are the same recording.</summary>
/// <param name="Artist">Who made it.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Copies">Where they are, biggest first.</param>
public sealed record SameAgain(string Artist, string Title, IReadOnlyList<LibraryTrack> Copies);

/// <summary>Where a track should go, and where it is now.</summary>
/// <param name="From">Where it is.</param>
/// <param name="To">Where it belongs.</param>
public sealed record Filing(string From, string To);

/// <summary>
/// A folder of music, read rather than downloaded.
///
/// **This is the part of Antra that needs no account.** Antra is a library manager whose
/// headline is seven streaming services, and every one of those needs credentials nobody
/// here has. What it does either side of the download does not: filing tracks into artist and
/// album folders, and noticing that the same recording is already there twice. Both of those
/// are somebody's existing folder of music and the tags already in it.
///
/// Said plainly rather than implied: this is two of Antra's twenty-odd features, working on
/// files that are already on the machine. It is not its library manager and does not claim
/// to be.
///
/// **Nothing here moves or deletes anything.** It reads a folder and says what it found and
/// what it would do; moving is a separate decision, made by somebody, once. A library tool
/// that tidied first and reported afterwards is a tool nobody can safely try.
/// </summary>
public sealed class MusicLibrary
{
    private readonly MediaLook _look;

    /// <summary>The endings worth reading. Anything else in the folder is left alone.</summary>
    public static readonly IReadOnlyList<string> Kinds =
        [".mp3", ".m4a", ".flac", ".alac", ".aac", ".ogg", ".opus", ".wav", ".wma"];

    public MusicLibrary(MediaLook? look = null) => _look = look ?? new MediaLook();

    /// <summary>
    /// Everything in a folder, with what each one says about itself.
    ///
    /// Bounded rather than trusted: somebody pointing this at a drive rather than a folder
    /// should get an answer rather than a machine reading forty thousand files, and reading
    /// tags means starting the encoder once per file.
    /// </summary>
    public async Task<IReadOnlyList<LibraryTrack>> ReadAsync(
        string folder, int most = 500, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (!Directory.Exists(folder))
        {
            return [];
        }

        var found = new List<LibraryTrack>();

        foreach (var path in Files(folder).Take(Math.Clamp(most, 1, 5000)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tags = await _look.TagsAsync(path, cancellationToken).ConfigureAwait(false);

            found.Add(new LibraryTrack(path, tags, Size(path)));
        }

        return found;
    }

    private static IEnumerable<string> Files(string folder)
    {
        IEnumerable<string> everything;

        try
        {
            everything = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in everything)
        {
            if (Kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static long Size(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The same recording, more than once.
    ///
    /// Matched on who made it and what it is called, not on the filename — the same track
    /// downloaded twice is "05 Wild Is The Wind.mp3" and "Nina Simone - Wild Is The Wind.m4a",
    /// and nothing about those two strings says they are the same thing.
    ///
    /// **A track with no artist or no title is never called a duplicate.** Two untagged files
    /// would otherwise match each other, and the whole answer would be "everything untagged in
    /// your library is the same song", which is both wrong and the kind of wrong somebody acts
    /// on.
    ///
    /// Antra matches on ISRC, which is exact and is the right answer where it exists. It is
    /// not in most files, and nothing here invents one.
    /// </summary>
    public static IReadOnlyList<SameAgain> Duplicates(IEnumerable<LibraryTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        return [.. tracks
            .Where(track => track.Tags.Artist.Length > 0 && track.Tags.Title.Length > 0)
            .GroupBy(track => (Plain(track.Tags.Artist), Plain(track.Tags.Title)))
            .Where(group => group.Count() > 1)
            .Select(group => new SameAgain(
                group.First().Tags.Artist,
                group.First().Tags.Title,
                // Biggest first, because the larger file is usually the better copy and
                // whoever is deciding what to keep is going to want it named first. It is a
                // hint, not a judgement: nothing here removes anything.
                [.. group.OrderByDescending(track => track.Bytes)]))
            .OrderBy(same => same.Artist, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Where each track belongs: artist, then album, then the track.
    ///
    /// A track that does not know who made it or what it is on is **left where it is** and
    /// said so, rather than filed under "Unknown Artist" — a folder called Unknown Artist is
    /// where music goes to be lost, and the file being untidy is a smaller problem than the
    /// file being somewhere nobody will look.
    /// </summary>
    /// <param name="tracks">What was read.</param>
    /// <param name="into">The root of the library.</param>
    public static IReadOnlyList<Filing> Filings(IEnumerable<LibraryTrack> tracks, string into)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentException.ThrowIfNullOrWhiteSpace(into);

        var filings = new List<Filing>();

        foreach (var track in tracks)
        {
            if (track.Tags.Artist.Length == 0 || track.Tags.Album.Length == 0)
            {
                continue;
            }

            var name = track.Tags.Title.Length > 0
                ? Safe(track.Tags.Title) + Path.GetExtension(track.Path)
                : Path.GetFileName(track.Path);

            var to = Path.Combine(into, Safe(track.Tags.Artist), Safe(track.Tags.Album), name);

            // A file already where it belongs is not a filing. Listing it would pad the answer
            // with work that is not work, and somebody reading "312 files to move" would
            // believe their library was in a worse state than it is.
            if (!string.Equals(Path.GetFullPath(to), Path.GetFullPath(track.Path), StringComparison.OrdinalIgnoreCase))
            {
                filings.Add(new Filing(track.Path, to));
            }
        }

        return filings;
    }

    /// <summary>
    /// A name a filesystem will take.
    ///
    /// Every character Windows refuses, plus the trailing dots and spaces it silently strips —
    /// a folder called "AC/DC" is two folders, and a folder ending in a full stop is a folder
    /// that cannot be opened afterwards.
    /// </summary>
    internal static string Safe(string name)
    {
        var clean = new string([.. name.Select(letter =>
            Path.GetInvalidFileNameChars().Contains(letter) || letter is '/' or '\\' or ':' ? '-' : letter)]);

        clean = clean.Trim().TrimEnd('.', ' ');

        return clean.Length == 0 ? "Unnamed" : clean;
    }

    /// <summary>
    /// A name flattened enough to compare. Case, spacing, punctuation and a leading "the" all
    /// differ between two copies of the same track and none of them make it a different track.
    /// </summary>
    internal static string Plain(string name)
    {
        var letters = new string([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

        return letters.StartsWith("the", StringComparison.Ordinal) && letters.Length > 3
            ? letters[3..]
            : letters;
    }
}
