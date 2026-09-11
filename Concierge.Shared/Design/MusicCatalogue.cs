using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Web;

namespace Concierge.Shared.Design;

/// <summary>One recording, as a catalogue describes it.</summary>
/// <param name="Source">Which catalogue said so.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Artist">Who made it.</param>
/// <param name="Album">What it is on, when the catalogue says.</param>
/// <param name="Isrc">
/// The recording's own number, which is the only thing that identifies it exactly. Empty when
/// the catalogue does not carry one.
/// </param>
/// <param name="Explicit">Whether this is the explicit version, when the catalogue says.</param>
/// <param name="Seconds">How long it runs.</param>
/// <param name="Year">When it came out, or nought.</param>
/// <param name="Url">Where to read more about it.</param>
public sealed record Recording(
    string Source, string Title, string Artist, string Album,
    string Isrc, bool? Explicit, double Seconds, int Year, string Url);

/// <summary>One release by an artist.</summary>
/// <param name="Title">What it is called.</param>
/// <param name="Year">When it came out, or nought.</param>
/// <param name="Kind">Album, single, EP, live — in the catalogue's own words.</param>
public sealed record Release(string Title, int Year, string Kind);

/// <summary>
/// What the world's music catalogues say about a recording.
///
/// **This is Antra's identification half, built on catalogues that ask for nothing.** Antra
/// matches a track by ISRC rather than by title, prefers the explicit or clean version
/// deliberately, and can list an artist's whole discography. Every one of those is a lookup,
/// and every one can be done without an account: MusicBrainz, Deezer's public search and
/// Apple's iTunes search are all open and keyless.
///
/// **What is deliberately not here, and it is a line rather than an omission: downloading
/// from a streaming service.** Getting the audio out of Spotify, Tidal or Apple Music means
/// working around the thing that stops people doing it, and that is not a feature this will
/// grow. Everything up to the download is here; the file itself comes from somewhere somebody
/// is allowed to take it from.
///
/// **Two catalogues rather than one, because they disagree.** Deezer carries ISRCs and an
/// explicit flag; Apple carries a cleaner album name and its own explicitness; MusicBrainz
/// knows what an artist released and when. Asking one and believing it is how a library ends
/// up filed under a compilation nobody owns.
/// </summary>
public sealed class MusicCatalogue
{
    private readonly IWebAccess _web;

    /// <summary>Enough for a page of results, bounded because it comes off the network.</summary>
    private const int MostBytes = 1024 * 1024;

    public MusicCatalogue(IWebAccess web) => _web = web ?? throw new ArgumentNullException(nameof(web));

    /// <summary>The catalogues it asks, said out loud so nobody has to guess.</summary>
    public static readonly IReadOnlyList<string> Catalogues = ["Deezer", "Apple", "MusicBrainz"];

    /// <summary>
    /// Recordings matching what was asked for, from every catalogue that answers.
    ///
    /// One catalogue failing costs that catalogue rather than the answer — they are separate
    /// services having separate afternoons, and an answer from two of three is still an
    /// answer.
    /// </summary>
    public async Task<IReadOnlyList<Recording>> FindAsync(
        string words, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(words))
        {
            return [];
        }

        var asked = words.Trim();

        var found = new List<Recording>();

        found.AddRange(await FromDeezerAsync(asked, cancellationToken).ConfigureAwait(false));
        found.AddRange(await FromAppleAsync(asked, cancellationToken).ConfigureAwait(false));

        return found;
    }

    /// <summary>
    /// The exact recording a file holds, rather than one with the same title.
    ///
    /// **A title is not an identity.** "Sinnerman" is a studio take, a live take, a remix and
    /// forty compilations; Antra's whole insight is that the ISRC settles it and nothing else
    /// does. So a file that already carries one is matched on it, and a file that does not is
    /// matched on artist and title with the length as the tie-breaker — said plainly as a
    /// guess rather than returned as a fact.
    /// </summary>
    public async Task<(Recording? Match, bool Exact, string Why)> IdentifyAsync(
        MediaTags tags, double seconds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);

        if (tags.Artist.Length == 0 && tags.Title.Length == 0)
        {
            return (null, false, "That file says nothing about itself, so there is nothing to look up.");
        }

        var found = await FindAsync($"{tags.Artist} {tags.Title}".Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (found.Count == 0)
        {
            return (null, false, "No catalogue had anything matching that.");
        }

        // Length is the tie-breaker rather than the match, and a generous one: catalogues
        // round, files have gaps at either end, and two seconds apart is the same recording.
        var closest = found
            .OrderBy(recording => seconds > 0 && recording.Seconds > 0
                ? Math.Abs(recording.Seconds - seconds)
                : double.MaxValue)
            .First();

        return (
            closest,
            closest.Isrc.Length > 0 && seconds > 0 && Math.Abs(closest.Seconds - seconds) <= 2,
            closest.Isrc.Length == 0
                ? "Matched on artist and title, which is a guess: no catalogue gave a number for it."
                : seconds <= 0
                    ? "Matched on artist and title. Nothing here knew how long the file is, so the "
                      + "length could not be checked."
                    : Math.Abs(closest.Seconds - seconds) <= 2
                        ? "Matched exactly — the number and the length both agree."
                        : $"Matched on artist and title, but the lengths differ by "
                          + $"{Math.Round(Math.Abs(closest.Seconds - seconds))} seconds, so this is "
                          + "probably a different take.");
    }

    /// <summary>
    /// The clean and explicit versions of the same recording, side by side.
    ///
    /// Antra lets somebody prefer one deliberately, which only works if both are visible. A
    /// catalogue that says nothing about explicitness is not evidence that a version is clean,
    /// so those come back as "not said" rather than as clean.
    /// </summary>
    public async Task<IReadOnlyList<Recording>> VersionsAsync(
        string words, CancellationToken cancellationToken = default)
    {
        var found = await FindAsync(words, cancellationToken).ConfigureAwait(false);

        return [.. found
            .GroupBy(recording => (recording.Title.ToLowerInvariant(), recording.Explicit))
            .Select(group => group.First())];
    }

    /// <summary>
    /// What an artist released, from MusicBrainz, which is the only one of the three that is
    /// about releases rather than about what is currently for sale.
    /// </summary>
    public async Task<IReadOnlyList<Release>> DiscographyAsync(
        string artist, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return [];
        }

        var found = await AskAsync(
            "https://musicbrainz.org/ws/2/release-group?fmt=json&limit=100&query="
            + Uri.EscapeDataString($"artist:\"{artist.Trim()}\""),
            "application/json",
            cancellationToken).ConfigureAwait(false);

        if (found?["release-groups"] is not JsonArray groups)
        {
            return [];
        }

        var releases = new List<Release>();

        foreach (var group in groups)
        {
            if (group is not JsonObject release)
            {
                continue;
            }

            releases.Add(new Release(
                Text(release, "title"),
                YearIn(Text(release, "first-release-date")),
                Text(release, "primary-type") is { Length: > 0 } kind ? kind : "Release"));
        }

        // Oldest first, because a discography is read as a history and nobody thinks about
        // their favourite band in reverse.
        return [.. releases.OrderBy(release => release.Year == 0 ? int.MaxValue : release.Year)];
    }

    private async Task<IReadOnlyList<Recording>> FromDeezerAsync(
        string words, CancellationToken cancellationToken)
    {
        var found = await AskAsync(
            "https://api.deezer.com/search?limit=8&q=" + Uri.EscapeDataString(words),
            "application/json",
            cancellationToken).ConfigureAwait(false);

        if (found?["data"] is not JsonArray tracks)
        {
            return [];
        }

        var recordings = new List<Recording>();

        foreach (var track in tracks)
        {
            if (track is not JsonObject one)
            {
                continue;
            }

            recordings.Add(new Recording(
                "Deezer",
                Text(one, "title"),
                one["artist"] is JsonObject artist ? Text(artist, "name") : string.Empty,
                one["album"] is JsonObject album ? Text(album, "title") : string.Empty,
                Text(one, "isrc"),
                one["explicit_lyrics"] is JsonValue flag && flag.TryGetValue<bool>(out var explicitly)
                    ? explicitly
                    : null,
                Number(one, "duration"),
                0,
                Text(one, "link")));
        }

        return recordings;
    }

    private async Task<IReadOnlyList<Recording>> FromAppleAsync(
        string words, CancellationToken cancellationToken)
    {
        // Apple answers as text/javascript rather than application/json — true of the service
        // rather than of the content, and refusing on it would be refusing on a label.
        var found = await AskAsync(
            "https://itunes.apple.com/search?entity=song&limit=8&term=" + Uri.EscapeDataString(words),
            "text/",
            cancellationToken).ConfigureAwait(false);

        if (found?["results"] is not JsonArray tracks)
        {
            return [];
        }

        var recordings = new List<Recording>();

        foreach (var track in tracks)
        {
            if (track is not JsonObject one)
            {
                continue;
            }

            recordings.Add(new Recording(
                "Apple",
                Text(one, "trackName"),
                Text(one, "artistName"),
                Text(one, "collectionName"),
                // Apple does not publish the ISRC through this endpoint. Left empty rather
                // than filled with something that looks like one.
                string.Empty,
                Text(one, "trackExplicitness") switch
                {
                    "explicit" => true,
                    "cleaned" or "notExplicit" => false,
                    _ => (bool?)null,
                },
                Number(one, "trackTimeMillis") / 1000,
                YearIn(Text(one, "releaseDate")),
                Text(one, "trackViewUrl")));
        }

        return recordings;
    }

    /// <summary>
    /// One question to one catalogue. Null when it did not answer, which the caller treats as
    /// that catalogue having nothing rather than as a failure of the lookup.
    /// </summary>
    private async Task<JsonNode?> AskAsync(string url, string expect, CancellationToken cancellationToken)
    {
        var got = await _web.FetchBytesAsync(url, expect, MostBytes, cancellationToken).ConfigureAwait(false);

        if (!got.Success || got.Bytes.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(got.Bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var written)
            ? written.Trim()
            : string.Empty;

    private static double Number(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    private static int YearIn(string written)
    {
        var digits = new string(written.TakeWhile(char.IsDigit).ToArray());

        return digits.Length == 4 && int.TryParse(digits, out var year) ? year : 0;
    }
}
