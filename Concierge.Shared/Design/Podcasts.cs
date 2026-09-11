using System.Text;
using System.Xml.Linq;
using Concierge.Shared.Web;

namespace Concierge.Shared.Design;

/// <summary>One episode of a show.</summary>
/// <param name="Title">What it is called.</param>
/// <param name="When">When it came out, or null when the feed did not say.</param>
/// <param name="Seconds">How long it runs, or nought.</param>
/// <param name="Url">The audio file itself.</param>
/// <param name="MediaType">What kind of file it is, as the feed says.</param>
/// <param name="About">The episode's own description, shortened.</param>
public sealed record Episode(
    string Title, DateTimeOffset? When, double Seconds, string Url, string MediaType, string About);

/// <summary>A show, and what is in it.</summary>
/// <param name="Title">What the show is called.</param>
/// <param name="About">What it is about, shortened.</param>
/// <param name="Feed">Where it was read from.</param>
/// <param name="Episodes">Newest first.</param>
public sealed record Show(string Title, string About, string Feed, IReadOnlyList<Episode> Episodes);

/// <summary>
/// Podcasts, which are the one part of Antra's list that works end to end with nothing.
///
/// A podcast feed is RSS on somebody's own server, published so that anybody may read it and
/// download the audio — that is what the format is *for*. No account, no key, no service to
/// ask permission of, and nothing to work around. So following a show, listing its episodes
/// and keeping one is a complete feature rather than a half of one waiting on credentials.
///
/// **Read with a real XML parser rather than by pattern-matching.** A feed is somebody else's
/// output and they are full of namespaces, CDATA, entities and mistakes; a regular expression
/// over XML works on the first ten feeds and fails on the eleventh in a way nobody can debug.
/// </summary>
public sealed class Podcasts
{
    private readonly IWebAccess _web;

    /// <summary>Enough for a long feed. Some shows have a thousand episodes in one file.</summary>
    private const int MostBytes = 8 * 1024 * 1024;

    /// <summary>The iTunes namespace, which is where the length and the description live.</summary>
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    public Podcasts(IWebAccess web) => _web = web ?? throw new ArgumentNullException(nameof(web));

    /// <summary>
    /// A show, read from its feed.
    ///
    /// Tried as XML and then as text, because servers disagree about what to call RSS —
    /// application/rss+xml, text/xml, application/xml and occasionally text/plain are all in
    /// the wild, and refusing on the label would refuse perfectly ordinary feeds.
    /// </summary>
    public async Task<(Show? Show, string? Problem)> ReadAsync(
        string feed, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feed))
        {
            return (null, "Give the address of the feed.");
        }

        var got = await _web.FetchBytesAsync(feed, "application/", MostBytes, cancellationToken)
            .ConfigureAwait(false);

        if (!got.Success)
        {
            got = await _web.FetchBytesAsync(feed, "text/", MostBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!got.Success)
        {
            return (null, got.Problem ?? "That feed could not be read.");
        }

        try
        {
            return (Parse(Encoding.UTF8.GetString(got.Bytes), got.Url), null);
        }
        catch (System.Xml.XmlException problem)
        {
            // Somebody else's output, so this is ordinary rather than exceptional. The address
            // is in the message because the commonest cause is a page that is not a feed.
            return (null, $"{feed} is not a feed this could read: {problem.Message}");
        }
    }

    internal static Show Parse(string xml, string from)
    {
        var document = XDocument.Parse(xml);
        var channel = document.Root?.Element("channel");

        if (channel is null)
        {
            throw new System.Xml.XmlException("There is no channel in it, so it is not a podcast feed.");
        }

        var episodes = new List<Episode>();

        foreach (var item in channel.Elements("item"))
        {
            var audio = item.Elements("enclosure")
                .FirstOrDefault(enclosure =>
                    (enclosure.Attribute("type")?.Value ?? string.Empty)
                    .StartsWith("audio", StringComparison.OrdinalIgnoreCase));

            // An item with no audio on it is a blog post in a podcast feed, which happens.
            // Listing it would offer somebody an episode that cannot be played.
            if (audio is null)
            {
                continue;
            }

            episodes.Add(new Episode(
                Words(item.Element("title")?.Value) is { Length: > 0 } title ? title : "An episode",
                Moment(item.Element("pubDate")?.Value),
                Length(item.Element(Itunes + "duration")?.Value),
                audio.Attribute("url")?.Value ?? string.Empty,
                audio.Attribute("type")?.Value ?? "audio/mpeg",
                Shortened(item.Element(Itunes + "summary")?.Value ?? item.Element("description")?.Value)));
        }

        return new Show(
            Words(channel.Element("title")?.Value) is { Length: > 0 } name ? name : "A show",
            Shortened(channel.Element(Itunes + "summary")?.Value ?? channel.Element("description")?.Value),
            from,
            // Newest first, which is how anybody opens a podcast. Feeds are usually in that
            // order already and are not always, so it is done here rather than trusted.
            [.. episodes
                .Where(episode => episode.Url.Length > 0)
                .OrderByDescending(episode => episode.When ?? DateTimeOffset.MinValue)]);
    }

    private static string Words(string? value) => (value ?? string.Empty).Trim();

    /// <summary>
    /// A description short enough to read in a list. Feeds carry whole HTML pages in here, and
    /// a model handed forty of those has spent its context on somebody's advertising.
    /// </summary>
    private static string Shortened(string? value)
    {
        var flat = System.Text.RegularExpressions.Regex.Replace(
            Words(value), "<.*?>", " ", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

        flat = string.Join(' ', flat.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return flat.Length <= 240 ? flat : flat[..240].TrimEnd() + "…";
    }

    private static DateTimeOffset? Moment(string? value)
        => DateTimeOffset.TryParse(
            Words(value), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var when)
            ? when
            : null;

    /// <summary>
    /// How long an episode runs, written any of the three ways feeds write it: seconds,
    /// minutes and seconds, or hours, minutes and seconds.
    /// </summary>
    internal static double Length(string? value)
    {
        var said = Words(value);

        if (said.Length == 0)
        {
            return 0;
        }

        var parts = said.Split(':');

        if (parts.Length == 1)
        {
            return double.TryParse(parts[0], out var seconds) ? seconds : 0;
        }

        double total = 0;

        foreach (var part in parts)
        {
            if (!double.TryParse(part, out var number))
            {
                return 0;
            }

            total = (total * 60) + number;
        }

        return total;
    }
}

/// <summary>
/// The shows somebody follows.
///
/// A file rather than a service, for the same reason the shapes catalogue is one: it is a list
/// of addresses, it belongs to whoever wrote it, and nothing about it needs code.
/// </summary>
public sealed class PodcastFollows
{
    private readonly string? _path;

    public PodcastFollows(string? path = null) => _path = path;

    /// <summary>Where the list is kept, or null when nowhere is.</summary>
    public string? Path => _path;

    /// <summary>The feeds being followed.</summary>
    public IReadOnlyList<string> All()
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>Follows one. Following the same show twice is following it once.</summary>
    public bool Follow(string feed)
    {
        if (string.IsNullOrWhiteSpace(_path) || string.IsNullOrWhiteSpace(feed))
        {
            return false;
        }

        var following = All().ToList();

        if (following.Any(one => one.Equals(feed, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        following.Add(feed.Trim());

        return Write(following);
    }

    /// <summary>Stops following one.</summary>
    public bool Unfollow(string feed)
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            return false;
        }

        var following = All()
            .Where(one => !one.Equals(feed?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Write(following);
    }

    private bool Write(IReadOnlyList<string> following)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path!)!);

            // Beside and moved into place, the way every other list here is written: a save
            // interrupted by the app closing must not leave half a file where the list was.
            var beside = _path + ".writing";

            File.WriteAllText(beside, System.Text.Json.JsonSerializer.Serialize(following));
            File.Move(beside, _path!, overwrite: true);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
