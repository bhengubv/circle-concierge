using System.Text.Json.Nodes;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Media;

/// <summary>
/// Making a picture, as something the assistant can actually do.
///
/// The seam for this has existed for months with two providers behind it, and the only thing
/// that could reach it was the composer: a person typing "draw a fox" got a picture, and a
/// model asked to put one on a page could not. So the capability was there and unreachable
/// from the harness — the ninth instance of this repository's signature defect, and the one
/// that left open-design's "image generation" line with no answer.
///
/// **Whoever makes the picture is somebody else's problem**, which is the point of the seam:
/// CircleAI when it ships one, a cloud provider when there is a key, a local generator if one
/// ever arrives. This is the wiring, and it is absent while nothing is ready rather than
/// offered and always failing.
///
/// **It asks, and the prompt is on the card.** Every generator that exists today leaves the
/// device and costs money per picture, which is the same argument the one design tool that
/// fetches a track already makes: going back is free on a canvas and it is not free on the
/// internet. When something generates on the device, that decision is worth revisiting rather
/// than inheriting.
/// </summary>
public sealed class ImageToolSource : IAgentToolSource
{
    private readonly IReadOnlyList<IImageRuntime> _runtimes;
    private readonly IToolApprovalService? _approval;
    private readonly IWebAccess? _web;
    private readonly string _into;

    /// <param name="runtimes">Whatever can make a picture.</param>
    /// <param name="approval">Asking first. Without it nothing here is offered.</param>
    /// <param name="web">
    /// For fetching a picture a provider hosts rather than hands over. Null on a head with no
    /// web access, and a provider that answers with an address then comes back as the address.
    /// </param>
    /// <param name="into">Where a picture is written. Pictures, by default.</param>
    public ImageToolSource(
        IEnumerable<IImageRuntime>? runtimes = null,
        IToolApprovalService? approval = null,
        IWebAccess? web = null,
        string? into = null)
    {
        _runtimes = runtimes?.ToList() ?? [];
        _approval = approval;
        _web = web;
        _into = string.IsNullOrWhiteSpace(into)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Concierge")
            : into;
    }

    /// <summary>
    /// Whichever can make a picture now — on the device first, for the reason a recording is
    /// transcribed on the device first: a prompt is somebody's idea, and sending it to a
    /// company they have never heard of should not be what happens when they say nothing.
    /// </summary>
    public IImageRuntime? Maker
        => _runtimes
            .Where(runtime => runtime.IsReady && !string.Equals(runtime.Id, "null", StringComparison.Ordinal))
            .OrderByDescending(runtime => runtime.Id.Contains("circleai", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools
        => Maker is { } maker && _approval is { } approval
            ? [new MakeAPicture(maker, approval, _web, _into)]
            : [];

    /// <summary>
    /// A picture from a prompt, written where somebody can find it.
    ///
    /// To a file rather than into the conversation, because a tool result is a string and a
    /// picture nobody can open is a tool that reports success and shows nothing. The path is
    /// what makes it usable everywhere else — `design_picture` puts one on a canvas,
    /// `read_file` and the rest can see it exists.
    /// </summary>
    private sealed class MakeAPicture(
        IImageRuntime maker, IToolApprovalService approval, IWebAccess? web, string into) : IAgentTool
    {
        /// <summary>Enough for a large picture, bounded because it comes off the network.</summary>
        private const int MostBytes = 32 * 1024 * 1024;

        public string Name => "make_picture";

        public string Description =>
            "Make a picture from a description and save it. The person is asked first, because "
            + "it leaves the device. Use the path it returns to put the picture somewhere.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["of"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What the picture should be of, in full.",
                },
                ["not"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What it should not have in it. Optional, and ignored by some.",
                },
            },
            ["required"] = new JsonArray("of"),
        };

        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var of = ImageWords.Said(arguments, "of");

            if (of.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Say what the picture should be of.");
            }

            // The description is on the card, not a summary of it. "Make a picture" is not
            // something anybody can decide about, and the words being sent to a company are.
            var decision = await approval.RequestAsync(
                new ToolApprovalRequest(
                    Name,
                    of,
                    ConciergeToolRisk.Medium,
                    $"It sends those words to {maker.EngineLabel} and saves the picture into {into}."),
                cancellationToken).ConfigureAwait(false);

            if (decision != ToolApprovalDecision.Allowed)
            {
                return new AgentToolResult(false, string.Empty, "Not allowed, so no picture was made.");
            }

            IReadOnlyList<ImageArtifact> made;

            try
            {
                made = await maker
                    .GenerateAsync(
                        new ImageGenerationRequest(of, ImageWords.Said(arguments, "not") is { Length: > 0 } not
                            ? not
                            : null),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException problem)
            {
                return new AgentToolResult(false, string.Empty, $"That did not come back: {problem.Message}");
            }
            catch (TaskCanceledException)
            {
                return new AgentToolResult(false, string.Empty, "That took too long and was stopped.");
            }

            if (made.Count == 0 || made[0] is not { } picture)
            {
                return new AgentToolResult(false, string.Empty, "Nothing came back.");
            }

            var (bytes, problemGetting) = await ImageWords
                .BytesOf(picture, web, MostBytes, cancellationToken)
                .ConfigureAwait(false);

            if (bytes is null)
            {
                // A provider that hosts the picture rather than handing it over, on a head
                // with no way to fetch it. The address is a real answer and better than a
                // failure, because somebody can still open it.
                return picture.Url is { Length: > 0 } address
                    ? new AgentToolResult(true, $"It is at {address}. Nothing here could fetch it to save it.")
                    : new AgentToolResult(false, string.Empty, problemGetting ?? "Nothing came back.");
            }

            try
            {
                Directory.CreateDirectory(into);

                var path = ImageWords.Free(Path.Combine(into, ImageWords.NameFor(of, picture.MimeType)));

                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

                return new AgentToolResult(
                    true, $"{path} — {bytes.Length / 1024}KB from {maker.EngineLabel}.");
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
    }
}

/// <summary>
/// The small shared pieces of making a picture, so the canvas and the file both use them.
///
/// Two places doing the same thing differently is how one of them comes to carry a bug the
/// other does not — which is why the design tool reads through here rather than repeating it.
/// </summary>
public static class ImageWords
{
    public static string Said(JsonNode? arguments, string key)
        => arguments?[key] is JsonValue value && value.TryGetValue<string>(out var written)
            ? written.Trim()
            : string.Empty;

    /// <summary>
    /// The picture itself, however the provider chose to answer.
    ///
    /// Some hand over bytes and some hand over an address they host for an hour or two. The
    /// address is the worse answer for a design — it stops working — so it is fetched here
    /// when anything can fetch it, through the same guard every other fetch uses.
    /// </summary>
    public static async Task<(byte[]? Bytes, string? Problem)> BytesOf(
        ImageArtifact picture, IWebAccess? web, int mostBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(picture);

        if (picture.Bytes is { Length: > 0 } already)
        {
            return (already, null);
        }

        if (picture.Url is not { Length: > 0 } address || web is null)
        {
            return (null, "The provider gave nothing this could save.");
        }

        var got = await web.FetchBytesAsync(address, "image/", mostBytes, cancellationToken)
            .ConfigureAwait(false);

        return got.Success ? (got.Bytes, null) : (null, got.Problem);
    }

    /// <summary>
    /// A filename from the words, so a folder of pictures can be read rather than searched.
    /// `a-fox-in-the-snow.png` beats `image-8f3a1c.png` every time somebody comes back to it.
    /// </summary>
    public static string NameFor(string words, string mimeType)
    {
        var letters = new string([.. words.ToLowerInvariant()
            .Select(letter => char.IsLetterOrDigit(letter) ? letter : '-')]);

        while (letters.Contains("--", StringComparison.Ordinal))
        {
            letters = letters.Replace("--", "-", StringComparison.Ordinal);
        }

        var stem = letters.Trim('-');

        if (stem.Length > 48)
        {
            stem = stem[..48].TrimEnd('-');
        }

        var ending = mimeType switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };

        return (stem.Length == 0 ? "picture" : stem) + ending;
    }

    /// <summary>A name nothing is already using, so nothing is written over.</summary>
    public static string Free(string wanted)
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

/// <summary>Registering it.</summary>
public static class ImageToolRegistration
{
    /// <summary>
    /// Adds making a picture as something the assistant can do.
    ///
    /// Registered always: the source publishes nothing while nothing is ready, and it is asked
    /// every time the catalogue is built — so a key added this afternoon works without a
    /// restart, and a CircleAI generator that arrives later needs no change here.
    /// </summary>
    public static IServiceCollection AddConciergeImageTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, ImageToolSource>(sp =>
            new ImageToolSource(
                sp.GetServices<IImageRuntime>(),
                sp.GetService<IToolApprovalService>(),
                sp.GetService<IWebAccess>())));

        return services;
    }
}
