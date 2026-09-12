using System.Text.Json.Nodes;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Concierge.Shared;

namespace Concierge.Shared.Design;

/// <summary>
/// Finding footage anybody may use, and bringing one clip down.
///
/// **Both ask, because both leave the device**, which is the same rule the one design tool
/// that fetches a track already follows: going back is free on a canvas and it is not free on
/// the internet — the request happened, and whoever is at the other end knows it. The words
/// being searched for are on the card, not a summary of them.
///
/// Absent entirely on a head with no web access or no way to ask, rather than offered and
/// always failing.
/// </summary>
public sealed class StockFootageToolSource : IAgentToolSource
{
    private readonly IWebAccess? _web;
    private readonly IToolApprovalService? _approval;
    private readonly string _into;

    /// <param name="web">Reaching the network, through the same guard every other fetch uses.</param>
    /// <param name="approval">Asking first.</param>
    /// <param name="into">Where a clip is put. Videos, by default.</param>
    public StockFootageToolSource(
        IWebAccess? web = null, IToolApprovalService? approval = null, string? into = null)
    {
        _web = web;
        _approval = approval;
        _into = string.IsNullOrWhiteSpace(into)
            ? Path.Combine(
                WhereThingsGo.Video, "Concierge", "Footage")
            : into;
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools
        => _web is null || _approval is null
            ? []
            : [new FindSomeFootage(_web, _approval), new BringTheClipDown(_web, _approval, _into)];

    private static string Said(JsonNode? arguments, string key)
        => arguments?[key] is JsonValue value && value.TryGetValue<string>(out var written)
            ? written.Trim()
            : string.Empty;

    /// <summary>Searching the archive.</summary>
    private sealed class FindSomeFootage(IWebAccess web, IToolApprovalService approval) : IAgentTool
    {
        public string Name => "stock_footage";

        public string Description =>
            $"Search {StockFootage.Archive} for video clips anybody may use and change. "
            + "Only freely licensed clips come back, with who made each one.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["of"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What the footage should be of — \"waves\", \"a city at night\".",
                },
            },
            ["required"] = new JsonArray("of"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var of = Said(arguments, "of");

            if (of.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Say what the footage should be of.");
            }

            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    $"Search {StockFootage.Archive} for footage of: {of}",
                    ConciergeToolRisk.Medium,
                    "It sends those words to the archive. Nothing is downloaded yet."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was searched for.");
            }

            var (clips, problem) = await new StockFootage(web)
                .SearchAsync(of, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (problem is not null)
            {
                return new AgentToolResult(false, string.Empty, problem);
            }

            if (clips.Count == 0)
            {
                // Both halves said, because "nothing found" and "everything found was
                // licensed in a way you cannot use" are different answers and only one of
                // them means try different words.
                return new AgentToolResult(
                    true,
                    $"Nothing freely licensed for \"{of}\". Either there is none, or what is "
                    + "there cannot be used or changed.");
            }

            var lines = clips.Select(clip =>
                $"{clip.Title} — {Math.Round(clip.Seconds)}s, {clip.Bytes / 1024 / 1024}MB, "
                + $"{clip.Licence}, by {(clip.By.Length > 0 ? clip.By : "somebody who did not say")}. {clip.Url}");

            return new AgentToolResult(
                true,
                $"{clips.Count} clips anybody may use and change. **Credit whoever made the one you "
                + $"use** — most of these licences require it.{Environment.NewLine}"
                + string.Join(Environment.NewLine, lines));
        }
    }

    /// <summary>
    /// Bringing one down.
    ///
    /// To a file rather than into the design, because footage is what a design points at
    /// rather than carries: a four-minute clip is hundreds of megabytes and the design is
    /// rewritten whenever anybody edits a heading.
    ///
    /// **The licence is written down beside it.** A file on a disk six months later says
    /// nothing about who made it or what may be done with it, and the moment somebody needs
    /// that is the moment they are about to publish.
    /// </summary>
    private sealed class BringTheClipDown(IWebAccess web, IToolApprovalService approval, string into) : IAgentTool
    {
        /// <summary>
        /// Big enough for a real clip, small enough that a mistake is not an afternoon. A
        /// minute of 1080p is tens of megabytes; an hour is not something anybody asked for.
        /// </summary>
        private const int MostBytes = 300 * 1024 * 1024;

        public string Name => "stock_fetch";

        public string Description =>
            "Download one clip found by stock_footage so a shot can point at it. The licence "
            + "is written down beside the file.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string", ["description"] = "The clip's address." },
                ["licence"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Its licence, as the search gave it.",
                },
                ["by"] = new JsonObject { ["type"] = "string", ["description"] = "Who made it." },
            },
            ["required"] = new JsonArray("url", "licence"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var url = Said(arguments, "url");
            var licence = Said(arguments, "licence");

            if (url.Length == 0 || licence.Length == 0)
            {
                return new AgentToolResult(
                    false, string.Empty, "Give the clip's address and the licence the search gave for it.");
            }

            // Checked here as well as at the search, because a model can write any string
            // into this and the whole promise of the search is that nothing unusable comes
            // through it. A licence nobody recognises is refused rather than trusted.
            if (!StockFootage.Free(licence) && !StockFootage.Free(Machine(licence)))
            {
                return new AgentToolResult(
                    false,
                    string.Empty,
                    $"{licence} is not a licence that lets somebody use and change the clip, so it "
                    + "was not downloaded.");
            }

            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    url,
                    ConciergeToolRisk.Medium,
                    $"It downloads that clip into {into}, with its licence ({licence}) written beside it."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was downloaded.");
            }

            var got = await web.FetchBytesAsync(url, "video/", MostBytes, cancellationToken).ConfigureAwait(false);

            if (!got.Success)
            {
                // Some perfectly ordinary video files are served as application/ogg, which is
                // not a lie and is not "video/" either. Tried once more rather than refused,
                // because refusing on a media type is refusing on a label.
                got = await web.FetchBytesAsync(url, "application/", MostBytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!got.Success)
            {
                return new AgentToolResult(false, string.Empty, got.Problem ?? "That clip could not be downloaded.");
            }

            try
            {
                Directory.CreateDirectory(into);

                var name = NameFrom(got.Url);
                var path = Free(Path.Combine(into, name));

                await File.WriteAllBytesAsync(path, got.Bytes, cancellationToken).ConfigureAwait(false);

                var by = Said(arguments, "by");

                await File.WriteAllTextAsync(
                    path + ".licence.txt",
                    $"{Path.GetFileName(path)}{Environment.NewLine}"
                    + $"From: {got.Url}{Environment.NewLine}"
                    + $"Licence: {licence}{Environment.NewLine}"
                    + $"By: {(by.Length > 0 ? by : "not stated")}{Environment.NewLine}"
                    + $"Downloaded: {DateTimeOffset.Now:yyyy-MM-dd}{Environment.NewLine}",
                    cancellationToken).ConfigureAwait(false);

                return new AgentToolResult(
                    true,
                    $"{path} — {got.Bytes.Length / 1024 / 1024}MB. The licence is beside it. "
                    + "A shot can point at it with design_add_footage.");
            }
            catch (IOException problem)
            {
                return new AgentToolResult(false, string.Empty, $"It could not be written: {problem.Message}");
            }
            catch (UnauthorizedAccessException)
            {
                return new AgentToolResult(false, string.Empty, $"Not allowed to write into {into}.");
            }
        }

        /// <summary>
        /// The machine-readable form of a licence written in prose, when somebody handed back
        /// the pretty name instead. "CC BY-SA 4.0" is what the search prints; "cc-by-sa-4.0"
        /// is what the check reads.
        /// </summary>
        private static string Machine(string licence)
            => licence.Trim().ToLowerInvariant().Replace(' ', '-');

        private static string NameFrom(string url)
        {
            var last = Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Segments.Length > 0
                ? Uri.UnescapeDataString(address.Segments[^1])
                : "clip.mp4";

            var clean = new string([.. last.Select(letter =>
                Path.GetInvalidFileNameChars().Contains(letter) ? '-' : letter)]).Trim();

            return clean.Length == 0 ? "clip.mp4" : clean;
        }

        /// <summary>A name nothing is already using, so nothing is written over.</summary>
        private static string Free(string wanted)
        {
            if (!File.Exists(wanted))
            {
                return wanted;
            }

            var folder = Path.GetDirectoryName(wanted)!;
            var stem = Path.GetFileNameWithoutExtension(wanted);
            var ending = Path.GetExtension(wanted);

            for (var number = 2; number < 1000; number++)
            {
                var next = Path.Combine(folder, $"{stem} ({number}){ending}");

                if (!File.Exists(next))
                {
                    return next;
                }
            }

            return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){ending}");
        }
    }
}

/// <summary>Registering them.</summary>
public static class StockFootageRegistration
{
    /// <summary>
    /// Adds the footage tools where there is a way to reach the network and a way to ask.
    /// Both, or neither: a tool that leaves the device without anybody agreeing to it is what
    /// must not exist.
    /// </summary>
    public static IServiceCollection AddConciergeStockFootage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Typed, so TryAddEnumerable has an implementation type to deduplicate on. A bare
        // factory throws while the container is being built.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, StockFootageToolSource>(sp =>
            new StockFootageToolSource(
                sp.GetService<IWebAccess>(),
                sp.GetService<IToolApprovalService>())));

        return services;
    }
}
