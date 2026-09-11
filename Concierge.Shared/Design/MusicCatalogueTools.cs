using System.Text.Json.Nodes;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Design;

/// <summary>
/// Asking the world's catalogues about a recording.
///
/// **Antra's identification half, and it needs no account.** Matching the exact recording
/// rather than the right title, choosing between the clean and explicit versions, and listing
/// an artist's discography are all lookups — and MusicBrainz, Deezer's public search and
/// Apple's iTunes search are open to anybody.
///
/// Each of these leaves the device, so each asks, with what is being looked up on the card.
/// None of them changes anything here, and none of them downloads any music.
/// </summary>
public sealed class MusicCatalogueToolSource : IAgentToolSource
{
    private readonly IWebAccess? _web;
    private readonly IToolApprovalService? _approval;
    private readonly MusicLibrary _library;

    public MusicCatalogueToolSource(
        IWebAccess? web = null, IToolApprovalService? approval = null, MusicLibrary? library = null)
    {
        _web = web;
        _approval = approval;
        _library = library ?? new MusicLibrary();
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools
        => _web is null || _approval is null
            ? []
            : [
                new FindTheRecording(_web, _approval),
                new WhichVersions(_web, _approval),
                new WhatTheyReleased(_web, _approval),
                .. MediaLook.Possible
                    ? new IAgentTool[] { new WhatIsThisFile(_web, _approval, _library) }
                    : [],
            ];

    private static string Said(JsonNode? arguments, string key)
        => arguments?[key] is JsonValue value && value.TryGetValue<string>(out var written)
            ? written.Trim()
            : string.Empty;

    private static async Task<bool> AllowedAsync(
        IToolApprovalService approval, string name, string what, CancellationToken cancellationToken)
        => await approval.RequestAsync(
            new ToolApprovalRequest(
                name,
                what,
                ConciergeToolRisk.Low,
                "It asks the public music catalogues. Nothing is downloaded and nothing changes here."),
            cancellationToken).ConfigureAwait(false) == ToolApprovalDecision.Allowed;

    private static string Describe(Recording recording)
        => $"{recording.Artist} — {recording.Title}"
           + (recording.Album.Length > 0 ? $" ({recording.Album})" : string.Empty)
           + (recording.Seconds > 0 ? $", {Math.Round(recording.Seconds)}s" : string.Empty)
           + (recording.Isrc.Length > 0 ? $", ISRC {recording.Isrc}" : ", no ISRC given")
           + recording.Explicit switch
           {
               true => ", explicit",
               false => ", clean",
               null => ", explicitness not said",
           }
           + $" [{recording.Source}]";

    /// <summary>Looking one up.</summary>
    private sealed class FindTheRecording(IWebAccess web, IToolApprovalService approval) : IAgentTool
    {
        public string Name => "music_find";

        public string Description =>
            "Look a recording up in the public music catalogues: who made it, what it is on, "
            + "how long it runs, and its ISRC — the number that identifies it exactly.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["what"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Artist and title — \"Nina Simone Sinnerman\".",
                },
            },
            ["required"] = new JsonArray("what"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var what = Said(arguments, "what");

            if (what.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Say what to look up.");
            }

            if (!await AllowedAsync(approval, Name, $"Look up: {what}", cancellationToken).ConfigureAwait(false))
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was looked up.");
            }

            var found = await new MusicCatalogue(web).FindAsync(what, cancellationToken).ConfigureAwait(false);

            return found.Count == 0
                ? new AgentToolResult(true, $"No catalogue had anything for \"{what}\".")
                : new AgentToolResult(
                    true,
                    $"{found.Count} recordings:" + Environment.NewLine
                    + string.Join(Environment.NewLine, found.Select(Describe)));
        }
    }

    /// <summary>Clean or explicit, side by side.</summary>
    private sealed class WhichVersions(IWebAccess web, IToolApprovalService approval) : IAgentTool
    {
        public string Name => "music_versions";

        public string Description =>
            "Show the clean and explicit versions of a recording side by side, so one can be "
            + "chosen deliberately rather than by accident.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["what"] = new JsonObject { ["type"] = "string", ["description"] = "Artist and title." },
            },
            ["required"] = new JsonArray("what"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var what = Said(arguments, "what");

            if (what.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Say what to look up.");
            }

            if (!await AllowedAsync(approval, Name, $"Look up versions of: {what}", cancellationToken)
                    .ConfigureAwait(false))
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was looked up.");
            }

            var versions = await new MusicCatalogue(web).VersionsAsync(what, cancellationToken)
                .ConfigureAwait(false);

            if (versions.Count == 0)
            {
                return new AgentToolResult(true, $"No catalogue had anything for \"{what}\".");
            }

            // Said rather than implied: a catalogue that is silent about explicitness is not
            // evidence that a version is clean, and treating it as one is how somebody plays
            // the wrong thing to a room.
            return new AgentToolResult(
                true,
                "Versions, and where a catalogue said nothing about explicitness that is not the "
                + "same as clean:" + Environment.NewLine
                + string.Join(Environment.NewLine, versions.Select(Describe)));
        }
    }

    /// <summary>Everything an artist released.</summary>
    private sealed class WhatTheyReleased(IWebAccess web, IToolApprovalService approval) : IAgentTool
    {
        public string Name => "music_discography";

        public string Description =>
            "List what an artist released, oldest first, from MusicBrainz.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["artist"] = new JsonObject { ["type"] = "string", ["description"] = "Who." },
            },
            ["required"] = new JsonArray("artist"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var artist = Said(arguments, "artist");

            if (artist.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Say which artist.");
            }

            if (!await AllowedAsync(approval, Name, $"List what {artist} released", cancellationToken)
                    .ConfigureAwait(false))
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was looked up.");
            }

            var releases = await new MusicCatalogue(web).DiscographyAsync(artist, cancellationToken)
                .ConfigureAwait(false);

            return releases.Count == 0
                ? new AgentToolResult(true, $"MusicBrainz had nothing for {artist}.")
                : new AgentToolResult(
                    true,
                    $"{releases.Count} releases by {artist}, oldest first:" + Environment.NewLine
                    + string.Join(
                        Environment.NewLine,
                        releases.Select(release =>
                            $"{(release.Year > 0 ? release.Year.ToString() : "----")} — {release.Title} "
                            + $"({release.Kind})")));
        }
    }

    /// <summary>
    /// What a file on the disk actually is.
    ///
    /// **A title is not an identity.** "Sinnerman" is a studio take, a live take, a remix and
    /// forty compilations, and Antra's whole insight is that the ISRC settles it. This reads
    /// the file's own tags and its real length, asks the catalogues, and says how confident
    /// the answer is rather than presenting a guess as a fact.
    /// </summary>
    private sealed class WhatIsThisFile(
        IWebAccess web, IToolApprovalService approval, MusicLibrary library) : IAgentTool
    {
        public string Name => "music_identify";

        public string Description =>
            "Work out exactly which recording a file on this machine holds — the take, not just "
            + "the title — using its tags, its length and the public catalogues.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file." },
            },
            ["required"] = new JsonArray("path"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            _ = library;

            var path = Said(arguments, "path");

            if (path.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Give the path of the file.");
            }

            if (!File.Exists(path))
            {
                return new AgentToolResult(false, string.Empty, $"There is no file at {path}.");
            }

            var look = new MediaLook();
            var tags = await look.TagsAsync(path, cancellationToken).ConfigureAwait(false);

            if (tags.Artist.Length == 0 && tags.Title.Length == 0)
            {
                return new AgentToolResult(
                    false,
                    string.Empty,
                    $"{Path.GetFileName(path)} says nothing about itself, so there is nothing to look "
                    + "up. Nothing here guesses an artist from a filename.");
            }

            if (!await AllowedAsync(
                    approval, Name, $"Look up: {tags.Artist} — {tags.Title}", cancellationToken)
                .ConfigureAwait(false))
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was looked up.");
            }

            var facts = await look.FactsAsync(path, cancellationToken).ConfigureAwait(false);

            var (match, exact, why) = await new MusicCatalogue(web)
                .IdentifyAsync(tags, facts.Ok ? facts.Seconds : 0, cancellationToken)
                .ConfigureAwait(false);

            if (match is null)
            {
                return new AgentToolResult(true, why);
            }

            return new AgentToolResult(
                true,
                $"{Path.GetFileName(path)} is {Describe(match)}.{Environment.NewLine}"
                + $"{(exact ? "Exact" : "Not exact")}: {why}");
        }
    }
}

/// <summary>Registering them.</summary>
public static class MusicCatalogueRegistration
{
    /// <summary>
    /// Adds the catalogue lookups where there is a way to reach the network and a way to ask.
    /// </summary>
    public static IServiceCollection AddConciergeMusicCatalogue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, MusicCatalogueToolSource>(sp =>
                new MusicCatalogueToolSource(
                    sp.GetService<IWebAccess>(),
                    sp.GetService<IToolApprovalService>())));

        return services;
    }
}
