using System.Text.Json.Nodes;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Concierge.Shared;

namespace Concierge.Shared.Design;

/// <summary>
/// Podcasts, as things a model can do.
///
/// Following a show, reading what is in it, and keeping an episode. All three leave the
/// device, so all three ask — and what is being fetched is on the card, because "check my
/// podcasts" is not something anybody can decide about and an address is.
///
/// **Keeping an episode writes a file and nothing else.** It does not tidy, rename, convert or
/// delete; a downloaded episode can go straight into a running order with the sound tools
/// that already exist.
/// </summary>
public sealed class PodcastToolSource : IAgentToolSource
{
    private readonly IWebAccess? _web;
    private readonly IToolApprovalService? _approval;
    private readonly PodcastFollows _follows;
    private readonly string _into;

    public PodcastToolSource(
        IWebAccess? web = null,
        IToolApprovalService? approval = null,
        PodcastFollows? follows = null,
        string? into = null)
    {
        _web = web;
        _approval = approval;
        _follows = follows ?? new PodcastFollows();
        _into = string.IsNullOrWhiteSpace(into)
            ? Path.Combine(WhereThingsGo.Music, "Podcasts")
            : into;
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools
        => _web is null || _approval is null
            ? []
            : [
                new FollowAShow(_follows),
                new WhatIsInIt(_web, _approval),
                new KeepAnEpisode(_web, _approval, _into),
            ];

    private static string Said(JsonNode? arguments, string key)
        => arguments?[key] is JsonValue value && value.TryGetValue<string>(out var written)
            ? written.Trim()
            : string.Empty;

    /// <summary>
    /// The shows somebody follows. Writes a list of addresses on this machine and nothing
    /// else, so it does not ask — it is the same act as writing a note to yourself.
    /// </summary>
    private sealed class FollowAShow(PodcastFollows follows) : IAgentTool
    {
        public string Name => "podcast_follow";

        public string Description =>
            "Follow a podcast by its feed address, stop following one, or list what is being "
            + "followed. Leave everything out to see the list.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["feed"] = new JsonObject { ["type"] = "string", ["description"] = "The feed's address." },
                ["stop"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "True to stop following that one.",
                },
            },
        };

        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var feed = Said(arguments, "feed");

            if (feed.Length == 0)
            {
                var following = follows.All();

                return Task.FromResult(new AgentToolResult(
                    true,
                    following.Count == 0
                        ? "No shows are being followed yet."
                        : "Following:" + Environment.NewLine + string.Join(Environment.NewLine, following)));
            }

            if (!feed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AgentToolResult(
                    false, string.Empty, "A feed is an http or https address."));
            }

            var stopping = arguments?["stop"] is JsonValue value
                && value.TryGetValue<bool>(out var stop) && stop;

            if (stopping)
            {
                return Task.FromResult(follows.Unfollow(feed)
                    ? new AgentToolResult(true, $"No longer following {feed}.")
                    : new AgentToolResult(false, string.Empty, "That list could not be written."));
            }

            return Task.FromResult(follows.Follow(feed)
                ? new AgentToolResult(true, $"Following {feed}.")
                : new AgentToolResult(
                    false, string.Empty, "This head has nowhere to keep a list of shows."));
        }
    }

    /// <summary>What is in a show.</summary>
    private sealed class WhatIsInIt(IWebAccess web, IToolApprovalService approval) : IAgentTool
    {
        public string Name => "podcast_episodes";

        public string Description =>
            "Read a podcast feed and list its episodes, newest first, with how long each runs "
            + "and where the audio is.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["feed"] = new JsonObject { ["type"] = "string", ["description"] = "The feed's address." },
                ["most"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "How many to list. Ten by default.",
                },
            },
            ["required"] = new JsonArray("feed"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var feed = Said(arguments, "feed");

            if (feed.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Give the feed's address.");
            }

            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name, feed, ConciergeToolRisk.Low, "It reads that feed. Nothing is downloaded."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was read.");
            }

            var (show, problem) = await new Podcasts(web).ReadAsync(feed, cancellationToken)
                .ConfigureAwait(false);

            if (show is null)
            {
                return new AgentToolResult(false, string.Empty, problem ?? "That feed could not be read.");
            }

            // Bounded rather than trusted: a show with a thousand episodes handed over whole
            // is a context window spent on a list nobody asked to see all of.
            var most = arguments?["most"] is JsonValue value
                ? value.TryGetValue<int>(out var whole) ? whole
                    : value.TryGetValue<double>(out var number) ? (int)number
                    : 10
                : 10;

            var lines = show.Episodes.Take(Math.Clamp(most, 1, 50)).Select(episode =>
                $"{episode.When:yyyy-MM-dd} — {episode.Title}"
                + (episode.Seconds > 0 ? $" ({Math.Round(episode.Seconds / 60)} min)" : string.Empty)
                + $" {episode.Url}");

            return new AgentToolResult(
                true,
                $"{show.Title} — {show.Episodes.Count} episodes.{Environment.NewLine}{show.About}"
                + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }
    }

    /// <summary>Keeping one.</summary>
    private sealed class KeepAnEpisode(IWebAccess web, IToolApprovalService approval, string into) : IAgentTool
    {
        /// <summary>A long episode is a few hundred megabytes; an audiobook is not an episode.</summary>
        private const int MostBytes = 500 * 1024 * 1024;

        public string Name => "podcast_keep";

        public string Description =>
            "Download one podcast episode so it can be listened to or put in a running order. "
            + "The person is asked first.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string", ["description"] = "The episode's audio." },
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What to call the file. The episode's title, usually.",
                },
            },
            ["required"] = new JsonArray("url"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var url = Said(arguments, "url");

            if (url.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Give the episode's address.");
            }

            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name, url, ConciergeToolRisk.Medium, $"It downloads that episode into {into}."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so nothing was downloaded.");
            }

            var got = await web.FetchBytesAsync(url, "audio/", MostBytes, cancellationToken)
                .ConfigureAwait(false);

            if (!got.Success)
            {
                return new AgentToolResult(false, string.Empty, got.Problem ?? "It could not be downloaded.");
            }

            try
            {
                Directory.CreateDirectory(into);

                var name = Said(arguments, "name");
                // Named from the episode's title when there is one, because a folder of
                // files called episode.mp3, episode (2).mp3 is a folder nobody can use.
                var called = (name.Length > 0 ? MusicLibrary.Safe(name) : "episode") + Ending(got.MediaType);

                var path = Concierge.Shared.Media.ImageWords.Free(Path.Combine(into, called));

                await File.WriteAllBytesAsync(path, got.Bytes, cancellationToken).ConfigureAwait(false);

                return new AgentToolResult(
                    true, $"{path} — {got.Bytes.Length / 1024 / 1024}MB.");
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

        private static string Ending(string mediaType) => mediaType switch
        {
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
            "audio/ogg" or "audio/opus" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            _ => ".mp3",
        };
    }
}

/// <summary>Registering them.</summary>
public static class PodcastRegistration
{
    public static IServiceCollection AddConciergePodcasts(this IServiceCollection services, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ => new PodcastFollows(
            string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "podcasts.json")));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, PodcastToolSource>(sp =>
            new PodcastToolSource(
                sp.GetService<IWebAccess>(),
                sp.GetService<IToolApprovalService>(),
                sp.GetService<PodcastFollows>())));

        return services;
    }
}
