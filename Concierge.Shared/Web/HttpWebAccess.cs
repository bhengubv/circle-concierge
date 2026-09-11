using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Concierge.Shared.Web;

/// <summary>
/// Fetching a URL, carefully.
///
/// Three things this does that a bare HttpClient would not, all of them because
/// the caller is a language model rather than a person:
///
/// A model can be talked into fetching anything, including addresses only this
/// machine can reach — a router at 192.168.1.1, a metadata endpoint at
/// 169.254.169.254, a service on localhost. Every one of those is refused
/// before the request is made, and refused again after redirects, because a
/// public URL can redirect to a private one.
///
/// It stops reading at a cap. A model asking for a page has no idea whether it
/// is four kilobytes or four hundred megabytes, and neither does the caller
/// until it arrives.
///
/// And it returns text, not markup. Handing a model raw HTML spends most of the
/// context window on attributes.
/// </summary>
public sealed class HttpWebAccess : IWebAccess
{
    /// <summary>
    /// Enough for an article, short of a context window. A page that needs more
    /// than this is one the model should be asked about more narrowly.
    /// </summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>Redirects are followed by hand so each hop can be checked.</summary>
    private const int MaxRedirects = 5;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;

    /// <summary>
    /// One client, reused. A client per call exhausts sockets under any load —
    /// the same mistake the voice path made and had fixed.
    /// </summary>
    public HttpWebAccess(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            // Followed by hand instead: a public URL is allowed to redirect to
            // a private one, and the guard has to run on every hop.
            AllowAutoRedirect = false
        })
        {
            Timeout = Timeout
        };

        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "Concierge/1.0 (+local assistant)");
        }
    }

    public async Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        var current = url;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri))
            {
                return Failed(url, $"'{current}' is not a URL.");
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return Failed(url, $"Only http and https are allowed, not '{uri.Scheme}'.");
            }

            if (IsPrivate(uri.Host))
            {
                return Failed(url, $"'{uri.Host}' is on this machine or this network, and is not reachable this way.");
            }

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                      .ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed(url, "The site did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                return Failed(url, $"Could not reach it: {ex.Message}");
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
                {
                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location.ToString()
                        : new Uri(uri, response.Headers.Location).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Failed(url, $"The site answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!IsReadable(mediaType))
                {
                    return Failed(url, $"That is {mediaType}, which is not text this can read.");
                }

                var (body, truncated, read) = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);

                var isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
                return new WebFetchResult(
                    Success: true,
                    Url: uri.ToString(),
                    Title: isHtml ? TitleOf(body) : null,
                    Text: isHtml ? ToText(body) : body.Trim(),
                    BytesRead: read,
                    Truncated: truncated);
            }
        }

        return Failed(url, $"Gave up after {MaxRedirects} redirects.");
    }

    /// <summary>
    /// The same walk as <see cref="FetchAsync"/>, keeping the bytes.
    ///
    /// Deliberately a sibling rather than a second implementation: scheme,
    /// private-address check, redirect limit and timeout are the same lines, so
    /// there is no second idea of what this program may reach. The differences
    /// are the two that matter for a file — what media type is acceptable, and a
    /// cap the caller sets, because a page and a track are not the same size of
    /// thing.
    ///
    /// A file over the cap is **refused, not truncated**. Half a page is still
    /// readable and worth having; half an audio file is a broken file that would
    /// be embedded in somebody's design and fail much later, somewhere that does
    /// not mention downloading.
    /// </summary>
    public async Task<WebBytesResult> FetchBytesAsync(
        string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var current = url;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri))
            {
                return NoBytes(url, $"'{current}' is not a URL.");
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return NoBytes(url, $"Only http and https are allowed, not '{uri.Scheme}'.");
            }

            // Checked on every hop, not only the first. A redirect to 169.254.169.254
            // is the whole trick, and a guard that runs once at the start does not
            // see it.
            if (IsPrivate(uri.Host))
            {
                return NoBytes(url, $"'{uri.Host}' is on this machine or this network, and is not reachable this way.");
            }

            HttpResponseMessage response;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                      .ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return NoBytes(url, "The site did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                return NoBytes(url, $"Could not reach it: {ex.Message}");
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
                {
                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location.ToString()
                        : new Uri(uri, response.Headers.Location).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return NoBytes(url, $"The site answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

                if (!mediaType.StartsWith(expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    return NoBytes(
                        url,
                        mediaType.Length == 0
                            ? "The site did not say what that file is."
                            : $"That is {mediaType}, not {expectedType.TrimEnd('/')}.");
                }

                // Refused before a byte is read where the server says how big it
                // is, so an enormous file costs nothing at all.
                if (response.Content.Headers.ContentLength is > 0 and var told && told > maxBytes)
                {
                    return NoBytes(url, $"That is {told / 1024 / 1024}MB, and the limit is {maxBytes / 1024 / 1024}MB.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[8192];
                var collected = new MemoryStream();
                int chunk;

                while ((chunk = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    collected.Write(buffer, 0, chunk);

                    // And again while reading, because Content-Length is something
                    // the other end chose to tell us and may simply be absent.
                    if (collected.Length > maxBytes)
                    {
                        return NoBytes(url, $"That is larger than the {maxBytes / 1024 / 1024}MB limit.");
                    }
                }

                return new WebBytesResult(true, uri.ToString(), mediaType, collected.ToArray(), null);
            }
        }

        return NoBytes(url, $"Gave up after {MaxRedirects} redirects.");
    }

    private static WebBytesResult NoBytes(string url, string problem)
        => new(false, url, string.Empty, [], problem);

    private static bool IsRedirect(HttpStatusCode code)
        => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;

    private static bool IsReadable(string mediaType)
        => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string Body, bool Truncated, int Read)> ReadCappedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[8192];
        var collected = new MemoryStream();
        int chunk;

        while ((chunk = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var room = MaxBytes - (int)collected.Length;
            if (room <= 0)
            {
                return (Decode(collected), true, (int)collected.Length);
            }

            collected.Write(buffer, 0, Math.Min(chunk, room));
        }

        return (Decode(collected), false, (int)collected.Length);
    }

    private static string Decode(MemoryStream stream)
        => Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);

    // ── Addresses a model must not be able to reach ───────────────────────

    /// <summary>
    /// Loopback, link-local, and the private ranges — plus the cloud metadata
    /// address, which is link-local anyway but is worth naming because it is
    /// the one that leaks credentials.
    ///
    /// Hostnames that are not literal addresses are resolved first: "localtest.me"
    /// and friends point at 127.0.0.1 and would otherwise walk straight past a
    /// string check.
    /// </summary>
    internal static bool IsPrivate(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsPrivate(literal);
        }

        try
        {
            var resolved = Dns.GetHostAddresses(host);
            return resolved.Length == 0 || resolved.Any(IsPrivate);
        }
        catch (SocketException)
        {
            // Cannot resolve it, so it cannot be fetched either. Refusing here
            // gives a clearer message than a connection error later.
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return true;
            }

            // Unique local addresses, fc00::/7.
            var v6 = address.GetAddressBytes();
            return (v6[0] & 0xFE) == 0xFC;
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            10 => true,                                  // 10.0.0.0/8
            127 => true,                                 // loopback
            169 when b[1] == 254 => true,                // link-local, incl. 169.254.169.254
            172 when b[1] >= 16 && b[1] <= 31 => true,   // 172.16.0.0/12
            192 when b[1] == 168 => true,                // 192.168.0.0/16
            0 => true,
            _ => false
        };
    }

    // ── Markup to something worth reading ─────────────────────────────────

    private static readonly Regex DroppedBlocks = new(
        @"<(script|style|noscript|svg|head)\b[^>]*>.*?</\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Blanks = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex TitleTag = new(
        @"<title\b[^>]*>(.*?)</title>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string? TitleOf(string html)
    {
        var match = TitleTag.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var title = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    /// <summary>
    /// Written here rather than pulled in as a dependency: it is forty lines,
    /// and a parser would be a new package on a project that keeps its licence
    /// surface deliberately small.
    /// </summary>
    internal static string ToText(string html)
    {
        var text = DroppedBlocks.Replace(html, " ");

        // Block-level tags become line breaks so paragraphs survive.
        text = Regex.Replace(text, @"<(br|/p|/div|/li|/h[1-6]|/tr)\s*/?>", "\n",
                             RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li\b[^>]*>", "\n- ", RegexOptions.IgnoreCase);

        text = Tags.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        text = Spaces.Replace(text, " ");
        text = string.Join('\n', text.Split('\n').Select(line => line.Trim()));
        text = Blanks.Replace(text, "\n\n");

        return text.Trim();
    }

    private static WebFetchResult Failed(string url, string why)
        => new(false, url, null, string.Empty, 0, false, why);
}
