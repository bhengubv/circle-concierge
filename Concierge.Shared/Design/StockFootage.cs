using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Design;

/// <summary>One clip somebody may use.</summary>
/// <param name="Title">What it is called.</param>
/// <param name="Url">The file itself.</param>
/// <param name="Page">The page it came from, which is where the licence is written.</param>
/// <param name="Licence">Its licence, in the archive's own words.</param>
/// <param name="By">Who made it, which most free licences require to be said.</param>
/// <param name="Seconds">How long it runs.</param>
/// <param name="Bytes">How big it is.</param>
/// <param name="MediaType">What kind of file it is.</param>
public sealed record StockClip(
    string Title, string Url, string Page, string Licence, string By, double Seconds, long Bytes, string MediaType);

/// <summary>
/// Footage anybody may actually use, from an archive that asks for nothing.
///
/// OpenMontage ships stock footage providers. Every one people reach for first — Pexels,
/// Pixabay, Storyblocks — wants an account and a key, which is one of the four things this
/// repository has recorded as missing. **Wikimedia Commons wants neither**, and everything in
/// it carries a licence written down beside it.
///
/// **A clip with no free licence is never offered.** This repository is held to
/// MIT/Apache/BSD/OFL/PD for code and the same care belongs here: a clip somebody puts in a
/// film and then cannot show is worse than no clip at all. Anything marked non-commercial or
/// no-derivatives is dropped, rather than returned with a warning nobody reads.
///
/// **And who made it comes back with it.** Nearly every free licence here requires
/// attribution, and a search result that omits the author is a search result that quietly
/// sets somebody up to breach it.
/// </summary>
public sealed class StockFootage
{
    private readonly IWebAccess _web;

    /// <summary>Enough for the answer, small enough that a runaway reply cannot fill memory.</summary>
    private const int MostBytes = 512 * 1024;

    public StockFootage(IWebAccess web) => _web = web ?? throw new ArgumentNullException(nameof(web));

    /// <summary>Where it looks, said out loud so nobody has to guess.</summary>
    public const string Archive = "Wikimedia Commons";

    /// <summary>
    /// Licences that let somebody use the clip and change it.
    ///
    /// Matched on the machine-readable name rather than the prose one, because "Creative
    /// Commons Attribution-NonCommercial" and "Creative Commons Attribution" differ by one
    /// word in prose and by a whole clause in what they permit.
    /// </summary>
    internal static bool Free(string licence)
    {
        var said = licence.Trim().ToLowerInvariant();

        if (said.Length == 0)
        {
            return false;
        }

        // The two that look free and are not. Checked first, because "cc-by-nc-4.0" starts
        // with "cc-by" and would otherwise pass.
        if (said.Contains("-nc", StringComparison.Ordinal) || said.Contains("-nd", StringComparison.Ordinal))
        {
            return false;
        }

        return said.StartsWith("cc-by", StringComparison.Ordinal)
            || said.StartsWith("cc-zero", StringComparison.Ordinal)
            || said.StartsWith("cc0", StringComparison.Ordinal)
            || said.StartsWith("pd", StringComparison.Ordinal)
            || said.Contains("public domain", StringComparison.Ordinal);
    }

    public async Task<(IReadOnlyList<StockClip> Clips, string? Problem)> SearchAsync(
        string words, int most = 8, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(words))
        {
            return ([], "Say what the footage should be of.");
        }

        var url =
            "https://commons.wikimedia.org/w/api.php?action=query&format=json"
            + "&generator=search&gsrnamespace=6&gsrlimit=" + Math.Clamp(most, 1, 20)
            + "&gsrsearch=" + Uri.EscapeDataString("filetype:video " + words.Trim())
            + "&prop=imageinfo&iiprop=url|mime|size|extmetadata"
            + "&iiextmetadatafilter=LicenseShortName|License|Artist";

        var got = await _web.FetchBytesAsync(url, "application/json", MostBytes, cancellationToken)
            .ConfigureAwait(false);

        if (!got.Success)
        {
            return ([], got.Problem ?? "The archive could not be reached.");
        }

        try
        {
            return (Read(got.Bytes), null);
        }
        catch (JsonException)
        {
            // An archive that changes its answer costs the search, not the app. Answered
            // rather than thrown, because every other way this fails comes back as a sentence.
            return ([], "The archive answered with something this could not read.");
        }
    }

    private static IReadOnlyList<StockClip> Read(byte[] json)
    {
        var root = JsonNode.Parse(json);

        if (root?["query"]?["pages"] is not JsonObject pages)
        {
            return [];
        }

        var clips = new List<StockClip>();

        foreach (var (_, page) in pages)
        {
            if (page?["imageinfo"] is not JsonArray infos || infos.Count == 0 || infos[0] is not JsonObject info)
            {
                continue;
            }

            var licence = Meta(info, "License");

            if (!Free(licence))
            {
                continue;
            }

            var url = info["url"]?.GetValue<string>() ?? string.Empty;

            if (url.Length == 0)
            {
                continue;
            }

            clips.Add(new StockClip(
                Title: page["title"]?.GetValue<string>() ?? "A clip",
                Url: url,
                Page: info["descriptionurl"]?.GetValue<string>() ?? string.Empty,
                Licence: Meta(info, "LicenseShortName") is { Length: > 0 } pretty ? pretty : licence,
                By: Plain(Meta(info, "Artist")),
                Seconds: info["duration"] is JsonValue length && length.TryGetValue<double>(out var seconds)
                    ? seconds
                    : 0,
                Bytes: info["size"]?.GetValue<long>() ?? 0,
                MediaType: info["mime"]?.GetValue<string>() ?? string.Empty));
        }

        return clips;
    }

    private static string Meta(JsonObject info, string name)
        => info["extmetadata"]?[name]?["value"]?.GetValue<string>() ?? string.Empty;

    /// <summary>
    /// The author's name without the markup around it. The archive returns an anchor tag,
    /// and a credit reading &lt;a href="//commons..."&gt;Someone&lt;/a&gt; on a film is worse
    /// than no credit.
    /// </summary>
    internal static string Plain(string html)
        => Regex.Replace(html, "<.*?>", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1))
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Trim();
}
