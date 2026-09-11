using System.Text.Json.Nodes;
using Concierge.Shared.Media;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Design;

/// <summary>
/// Writing out what is said in a recording, as something a model can do.
///
/// Read-only, so it does not ask — the same rule `media_facts` and `read_file` follow. It
/// reads a file and produces words; it changes nothing.
///
/// **Absent when nothing can hear.** Whichever voice runtime is registered says whether it
/// can transcribe — the local one when its whisper model is in place, a cloud one when a key
/// is set — and with none of them able to, the tool is not offered at all rather than offered
/// and always failing. A model that is told it can do a thing will try to.
///
/// It picks whatever can hear, preferring one that works on the device: a recording is about
/// as personal as a file gets, and sending it somewhere else should not be the quiet default
/// when this machine can do it.
/// </summary>
public sealed class TranscribeToolSource : IAgentToolSource
{
    private readonly IReadOnlyList<IVoiceRuntime> _voices;

    public TranscribeToolSource(IEnumerable<IVoiceRuntime> voices)
    {
        ArgumentNullException.ThrowIfNull(voices);
        _voices = voices.ToList();
    }

    /// <summary>Whichever can hear, on the device first. See <see cref="VoiceChoice"/>.</summary>
    public IVoiceRuntime? Ears => VoiceChoice.Ears(_voices);

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools
        => Ears is { } ears ? [new WriteOutWhatIsSaid(ears)] : [];

    private sealed class WriteOutWhatIsSaid(IVoiceRuntime voice) : IAgentTool
    {
        public string Name => "media_transcribe";

        public string Description =>
            "Write out what is said in an audio or video file. "
            + "Use this before saying anything about what is in a recording you have not heard.";

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
            var path = arguments?["path"] is JsonValue value && value.TryGetValue<string>(out var written)
                ? written.Trim()
                : string.Empty;

            if (path.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Give the path of the file.");
            }

            if (!File.Exists(path))
            {
                return new AgentToolResult(false, string.Empty, $"There is no file at {path}.");
            }

            try
            {
                await using var audio = File.OpenRead(path);

                var heard = await voice
                    .TranscribeAsync(audio, Path.GetFileName(path), cancellationToken)
                    .ConfigureAwait(false);

                // Nothing said is a real answer and not a failure. A recording can be
                // silence, and reporting that as an error sends whoever asked looking for a
                // broken tool instead of a muted microphone.
                return new AgentToolResult(
                    true,
                    heard.Text.Length > 0
                        ? heard.Text
                        : $"Nothing was said in {Path.GetFileName(path)} — or nothing that could be made out.");
            }
            catch (IOException problem)
            {
                return new AgentToolResult(false, string.Empty, $"That file could not be read: {problem.Message}");
            }
            catch (UnauthorizedAccessException)
            {
                return new AgentToolResult(false, string.Empty, "That file is not readable from here.");
            }
        }
    }
}

/// <summary>Registering it.</summary>
public static class TranscribeRegistration
{
    /// <summary>
    /// Adds transcription as a tool.
    ///
    /// Registered always rather than only when something can hear today: the source itself
    /// publishes nothing while nothing can, and it is asked every time the tool list is
    /// built. Deciding at start-up would leave somebody who put a whisper model in the
    /// folder afterwards with a capability that is present on disk and absent in the app,
    /// with nothing anywhere saying why.
    /// </summary>
    public static IServiceCollection AddConciergeTranscription(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, TranscribeToolSource>());

        return services;
    }
}
