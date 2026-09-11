using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Design;

/// <summary>
/// Looking inside a media file, as things a model can do.
///
/// All three only read, so none of them asks — the same rule `read_file` follows.
/// Absent entirely on a machine with no encoder, like every other capability that
/// cannot do its job here, rather than offered and always failing.
/// </summary>
public sealed class MediaLookToolSource : IAgentToolSource
{
    private readonly MediaLook _look;
    private readonly CapturedImages? _captured;

    /// <param name="look">The encoder.</param>
    /// <param name="captured">
    /// Where a picture waits for the next turn. Null on a head that keeps none,
    /// and the filmstrip is then not offered — a tool that produces a picture
    /// nothing can carry is a tool that reports success and shows nobody
    /// anything.
    /// </param>
    public MediaLookToolSource(MediaLook look, CapturedImages? captured = null)
    {
        _look = look ?? throw new ArgumentNullException(nameof(look));
        _captured = captured;
    }

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools
        => !MediaLook.Possible
            ? []
            : [
                new WhatIsInIt(_look),
                new IsItSilent(_look),
                .. _captured is null ? Array.Empty<IAgentTool>() : [new ShowMeFrames(_look, _captured)],
            ];

    /// <summary>How long it is and what is in it.</summary>
    private sealed class WhatIsInIt(MediaLook look) : IAgentTool
    {
        public string Name => "media_facts";

        public string Description =>
            "Read what is inside an audio or video file: how long it runs and what streams it holds. "
            + "Use this before saying anything about a file you have not looked at.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file." },
            },
            ["required"] = new JsonArray("path"),
        };

        public bool IsReadOnly => true;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var path = arguments?["path"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                return new AgentToolResult(false, string.Empty, "Give the path of the file.");
            }

            var facts = await look.FactsAsync(path, cancellationToken).ConfigureAwait(false);

            return facts.Ok
                ? new AgentToolResult(
                    true,
                    $"{Math.Round(facts.Seconds, 2)} seconds.{Environment.NewLine}{facts.Description}")
                : new AgentToolResult(false, string.Empty, facts.Problem);
        }
    }

    /// <summary>
    /// Whether there is anything to hear.
    ///
    /// Its own tool rather than a line in the facts, because it is a question with
    /// a yes-or-no answer that somebody actually asks — a track that downloaded
    /// wrong or a shot recorded with the microphone muted looks entirely normal in
    /// a stream listing.
    /// </summary>
    private sealed class IsItSilent(MediaLook look) : IAgentTool
    {
        public string Name => "media_is_silent";

        public string Description =>
            "Check whether an audio or video file actually has sound in it, or is silence.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file." },
            },
            ["required"] = new JsonArray("path"),
        };

        public bool IsReadOnly => true;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var path = arguments?["path"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                return new AgentToolResult(false, string.Empty, "Give the path of the file.");
            }

            var (silent, loudness, problem) = await look.SilentAsync(path, cancellationToken).ConfigureAwait(false);

            return problem is not null
                ? new AgentToolResult(false, string.Empty, problem)
                : new AgentToolResult(
                    true,
                    silent
                        ? $"Silent — average loudness {Math.Round(loudness, 1)}dB."
                        : $"There is sound — average loudness {Math.Round(loudness, 1)}dB.");
        }
    }

    /// <summary>
    /// Frames from across a video, as one picture on the next turn.
    ///
    /// It hands the picture to <see cref="CapturedImages"/> rather than returning
    /// it, because a tool result is a string: the same route a person attaching a
    /// file already uses, and the same one `screenshot` uses.
    /// </summary>
    private sealed class ShowMeFrames(MediaLook look, CapturedImages captured) : IAgentTool
    {
        public string Name => "media_frames";

        public string Description =>
            "Take frames from across a video and look at them as one strip of pictures. "
            + "Use this to see what is actually in a video rather than guessing from its name.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The video." },
                ["frames"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "How many, spread evenly. Six by default.",
                },
            },
            ["required"] = new JsonArray("path"),
        };

        public bool IsReadOnly => true;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var path = arguments?["path"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(path))
            {
                return new AgentToolResult(false, string.Empty, "Give the path of the video.");
            }

            // Bounded rather than trusted. A model asking for two hundred frames
            // gets a strip too wide to read and a context window spent on it.
            var across = Math.Clamp(arguments?["frames"]?.GetValue<int>() ?? 6, 1, 12);

            var (png, problem) = await look.FilmstripAsync(path, across, cancellationToken).ConfigureAwait(false);

            if (png is null)
            {
                return new AgentToolResult(false, string.Empty, problem);
            }

            captured.Add(new ChatImage($"{Path.GetFileNameWithoutExtension(path)}-frames.png", "image/png", png));

            return new AgentToolResult(
                true, $"{across} frames from {Path.GetFileName(path)}, waiting on the next turn.");
        }
    }
}

/// <summary>Registering the three.</summary>
public static class MediaLookRegistration
{
    /// <summary>
    /// Adds the media inspection tools where there is an encoder to run them.
    ///
    /// Registered by type through TryAddEnumerable for the same reason the device
    /// capabilities are: it deduplicates on the implementation type, and a factory
    /// descriptor gives it nothing to compare.
    /// </summary>
    public static IServiceCollection AddConciergeMediaLook(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!MediaLook.Possible)
        {
            // No encoder, no tools, and nothing said to the model about them.
            return services;
        }

        services.TryAddSingleton<MediaLook>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, MediaLookToolSource>());

        return services;
    }
}
