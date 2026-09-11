using System.Text.Json.Nodes;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Design;

/// <summary>
/// A folder of music, as things a model can do.
///
/// Two tools that read and one that moves. The two that read do not ask, like every other
/// read-only tool here. The one that moves asks, and the card says how many files and where
/// they are going — "tidy my music" is not something anybody can make a decision about, and
/// "move 312 files into D:\Music" is.
///
/// **Nothing here deletes anything, ever, including a duplicate.** Finding the same recording
/// twice is worth saying; choosing which copy to lose is somebody's decision about their own
/// music, and a tool that made it would be right most of the time, which is the worst
/// possible score for that.
/// </summary>
public sealed class MusicLibraryToolSource : IAgentToolSource
{
    private readonly MusicLibrary _library;
    private readonly IToolApprovalService? _approval;

    public MusicLibraryToolSource(MusicLibrary? library = null, IToolApprovalService? approval = null)
    {
        _library = library ?? new MusicLibrary();
        _approval = approval;
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools
        => !MediaLook.Possible
            ? []
            : [
                new WhatIsTwiceOver(_library),
                new WhereItAllBelongs(_library),
                .. _approval is null ? Array.Empty<IAgentTool>() : [new PutItThere(_library, _approval)],
            ];

    private static string Folder(JsonNode? arguments, string key)
        => arguments?[key] is JsonValue value && value.TryGetValue<string>(out var written)
            ? written.Trim()
            : string.Empty;

    /// <summary>The same recording, more than once. Read-only.</summary>
    private sealed class WhatIsTwiceOver(MusicLibrary library) : IAgentTool
    {
        public string Name => "music_duplicates";

        public string Description =>
            "Find recordings that are in a music folder more than once, matched on who made "
            + "them and what they are called rather than on the filename.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "The music folder." },
            },
            ["required"] = new JsonArray("folder"),
        };

        public bool IsReadOnly => true;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var folder = Folder(arguments, "folder");

            if (folder.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Give the music folder.");
            }

            if (!Directory.Exists(folder))
            {
                return new AgentToolResult(false, string.Empty, $"There is no folder at {folder}.");
            }

            var tracks = await library.ReadAsync(folder, cancellationToken: cancellationToken).ConfigureAwait(false);
            var same = MusicLibrary.Duplicates(tracks);

            if (same.Count == 0)
            {
                return new AgentToolResult(
                    true, $"Nothing is in there twice — {tracks.Count} tracks read.");
            }

            var lines = same.Select(one =>
                $"{one.Artist} — {one.Title}: {string.Join(", ", one.Copies.Select(copy => copy.Path))}");

            return new AgentToolResult(
                true,
                $"{same.Count} recordings are in there more than once, largest copy first. "
                + $"Nothing has been removed.{Environment.NewLine}"
                + string.Join(Environment.NewLine, lines));
        }
    }

    /// <summary>Where everything would go. Read-only, and the thing to read before moving.</summary>
    private sealed class WhereItAllBelongs(MusicLibrary library) : IAgentTool
    {
        public string Name => "music_filing";

        public string Description =>
            "Say where each track in a folder belongs — artist, then album — without moving "
            + "anything. Read this before filing.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "The music folder." },
                ["into"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Where the library should be. The folder itself by default.",
                },
            },
            ["required"] = new JsonArray("folder"),
        };

        public bool IsReadOnly => true;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var folder = Folder(arguments, "folder");

            if (folder.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Give the music folder.");
            }

            if (!Directory.Exists(folder))
            {
                return new AgentToolResult(false, string.Empty, $"There is no folder at {folder}.");
            }

            var into = Folder(arguments, "into");
            into = into.Length == 0 ? folder : into;

            var tracks = await library.ReadAsync(folder, cancellationToken: cancellationToken).ConfigureAwait(false);
            var filings = MusicLibrary.Filings(tracks, into);

            // The ones that stay put are counted rather than listed, and said out loud: a
            // person told "18 to move" out of 400 tracks needs to know what happened to the
            // other 382 before they believe it.
            var staying = tracks.Count - filings.Count;

            if (filings.Count == 0)
            {
                return new AgentToolResult(
                    true, $"Nothing to move — {tracks.Count} tracks read, all already where they belong "
                          + "or with nothing on them to file by.");
            }

            var lines = filings.Take(40).Select(filing => $"{filing.From} → {filing.To}");

            return new AgentToolResult(
                true,
                $"{filings.Count} of {tracks.Count} tracks would move; {staying} stay where they are "
                + $"(already right, or with no artist and album on them). Nothing has moved."
                + Environment.NewLine + string.Join(Environment.NewLine, lines)
                + (filings.Count > 40 ? $"{Environment.NewLine}…and {filings.Count - 40} more." : string.Empty));
        }
    }

    /// <summary>
    /// Actually moving them.
    ///
    /// Asks, and the card says how many and where to. **Nothing is ever overwritten**: a name
    /// already taken gets a number, the same promise `design_save` makes, because the one
    /// thing a tidying tool must not do is quietly replace one recording with another that
    /// happens to share a name.
    /// </summary>
    private sealed class PutItThere(MusicLibrary library, IToolApprovalService approval) : IAgentTool
    {
        public string Name => "music_file";

        public string Description =>
            "Move the tracks in a folder into artist and album folders. The person is asked "
            + "first. Nothing is overwritten and nothing is deleted.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "The music folder." },
                ["into"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Where the library should be. The folder itself by default.",
                },
            },
            ["required"] = new JsonArray("folder"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var folder = Folder(arguments, "folder");

            if (folder.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Give the music folder.");
            }

            if (!Directory.Exists(folder))
            {
                return new AgentToolResult(false, string.Empty, $"There is no folder at {folder}.");
            }

            var into = Folder(arguments, "into");
            into = into.Length == 0 ? folder : into;

            var tracks = await library.ReadAsync(folder, cancellationToken: cancellationToken).ConfigureAwait(false);
            var filings = MusicLibrary.Filings(tracks, into);

            if (filings.Count == 0)
            {
                return new AgentToolResult(true, "Everything is already where it belongs. Nothing moved.");
            }

            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    $"Move {filings.Count} tracks into artist and album folders under {into}",
                    ConciergeToolRisk.High,
                    "It moves the files. Nothing is overwritten and nothing is deleted, and the "
                    + "folders they came from are left behind."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was moved.");
            }

            var moved = 0;
            var trouble = new List<string>();

            foreach (var filing in filings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filing.To)!);
                    File.Move(filing.From, Free(filing.To));
                    moved++;
                }
                catch (IOException problem)
                {
                    // One file that will not move costs that file. Stopping halfway through
                    // would leave a library in two states with nothing saying which is which.
                    trouble.Add($"{Path.GetFileName(filing.From)}: {problem.Message}");
                }
                catch (UnauthorizedAccessException)
                {
                    trouble.Add($"{Path.GetFileName(filing.From)}: not allowed to move it.");
                }
            }

            var said = $"Moved {moved} of {filings.Count} tracks into {into}.";

            return trouble.Count == 0
                ? new AgentToolResult(true, said)
                : new AgentToolResult(
                    true,
                    said + $" {trouble.Count} would not move:" + Environment.NewLine
                         + string.Join(Environment.NewLine, trouble.Take(20)));
        }

        /// <summary>
        /// A path nothing is already at. The same promise `design_save` makes, and for a
        /// stronger reason: two different recordings can genuinely share an artist, an album
        /// and a title, and overwriting one with the other loses music somebody cannot get
        /// back.
        /// </summary>
        internal static string Free(string wanted)
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
public static class MusicLibraryRegistration
{
    /// <summary>
    /// Adds the library tools where there is an encoder to read tags with.
    ///
    /// No encoder, no tools: reading what a file says about itself is what all three are
    /// built on, and a tidying tool with no tags to read would file everything under nothing.
    /// </summary>
    public static IServiceCollection AddConciergeMusicLibrary(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!MediaLook.Possible)
        {
            return services;
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource>(sp =>
            new MusicLibraryToolSource(
                new MusicLibrary(),
                sp.GetService<IToolApprovalService>())));

        return services;
    }
}
