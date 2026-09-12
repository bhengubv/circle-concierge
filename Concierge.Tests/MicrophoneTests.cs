using System.Runtime.CompilerServices;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// Press, talk, press again.
///
/// **On the desktop head the microphone posted into nothing.** `/api/voice/transcribe` is
/// mapped in `Concierge.Web` and nowhere else, so the app most people run sent its recording
/// to a route that does not exist and reported *"Transcription failed (404)"* — a status code
/// from a server that was never listening. Meanwhile a runtime that transcribes on the device,
/// with no key and no network, was in the container two feet away.
///
/// This matters more than an ordinary defect because speech is meant to be the verb: the whole
/// product is somebody talking to a watch while the work happens elsewhere. The one head that
/// could already hear was the one head that could not.
/// </summary>
public sealed class MicrophoneTests : BunitContext
{
    /// <summary>Ears that return whatever they were told to.</summary>
    private sealed class Hears(string text, bool ready = true) : IVoiceRuntime
    {
        public string Id => "hears";
        public string EngineLabel => "Hears (stub)";
        public bool IsReady => ready;
        public string StatusMessage => "ready";
        public bool SupportsTranscription => true;
        public bool SupportsSynthesis => false;

        public byte[]? Heard { get; private set; }

        public async Task<TranscriptionResult> TranscribeAsync(
            Stream audio, string fileName, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await audio.CopyToAsync(copy, cancellationToken);
            Heard = copy.ToArray();

            return new TranscriptionResult(Id, text, "en", TimeSpan.FromSeconds(1));
        }

        public Task<SpeechResult> SynthesizeAsync(
            string text, string voice = "alloy", CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Quiet : IChatRuntime
    {
        public string Id => "quiet";
        public string EngineLabel => "Quiet";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> Workspace(
        IVoiceRuntime? ears)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mic-{Guid.NewGuid():N}.db");
        Services.AddLogging();
        Services.AddConciergeCore();

        Services.AddSingleton(_ => new Concierge.Shared.Skills.UserSkillFolders(
            Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}.json")));
        Services.AddSingleton<Concierge.Shared.Session.ISessionState>(
            new Concierge.Shared.Session.FileSessionState(
                Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid():N}.json")));

        Services.AddConciergeChat(dbPath);
        Services.AddSingleton<IEnumerable<IChatRuntime>>(_ => new IChatRuntime[] { new Quiet() });
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeTools(Services);
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeRuntime(Services);
        Concierge.Shared.Diagrams.ConciergeDiagramsServiceCollectionExtensions.AddConciergeDiagrams(Services);
        Services.AddSingleton<IEnumerable<IImageRuntime>>(_ => Array.Empty<IImageRuntime>());

        if (ears is not null)
        {
            Services.AddSingleton(ears);
        }

        JSInterop.Mode = JSRuntimeMode.Loose;

        // What the interop module hands back: a base64 recording.
        JSInterop.Setup<string?>("conciergeVoice.stop")
            .SetResult(Convert.ToBase64String("a recording"u8.ToArray()));

        using (var db = Services.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>().CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        var cut = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();
        cut.WaitForState(() => cut.FindAll("section.ws-group").Count > 0);

        return cut;
    }

    /// <summary>
    /// The microphone, found by what it is rather than by what it says.
    ///
    /// Its label lives in a title attribute and flips to "Stop recording" the moment it is
    /// pressed, so matching on the word "Speak" finds it once and never again — which is how
    /// the first version of this test failed on its second press.
    /// </summary>
    private static void PressTheMicrophone(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
        => cut.FindAll("button.icon-btn")
            .First(button => !button.ClassList.Contains("icon-btn-send"))
            .Click();

    /// <summary>
    /// What was said reaches the box, using the device rather than a server.
    /// </summary>
    [Fact]
    public void What_was_said_lands_in_the_composer()
    {
        var ears = new Hears("add a wall");
        var cut = Workspace(ears);

        PressTheMicrophone(cut);   // start
        PressTheMicrophone(cut);   // stop, and write it down

        cut.WaitForAssertion(
            () => Assert.Equal("add a wall", cut.Find("textarea.comp-input").GetAttribute("value")),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// And the audio actually reached the runtime — not a claim about plumbing, the bytes.
    /// </summary>
    [Fact]
    public void And_the_recording_itself_reaches_the_runtime()
    {
        var ears = new Hears("hello");
        var cut = Workspace(ears);

        PressTheMicrophone(cut);
        PressTheMicrophone(cut);

        cut.WaitForAssertion(
            () => Assert.Equal("a recording"u8.ToArray(), ears.Heard),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// **Heard, not sent.** Speech misreads words, and a surface that acted on every sentence
    /// the moment it heard one would be unusable in a room with other people in it. Correcting
    /// is the whole product, and you cannot correct something that already happened.
    /// </summary>
    [Fact]
    public void But_it_is_not_sent_on_its_own()
    {
        var cut = Workspace(new Hears("delete everything"));

        PressTheMicrophone(cut);
        PressTheMicrophone(cut);

        cut.WaitForAssertion(
            () => Assert.Equal("delete everything", cut.Find("textarea.comp-input").GetAttribute("value")),
            TimeSpan.FromSeconds(10));

        // Still sitting in the box, waiting for a person.
        Assert.Contains("Send it", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Silence is a real answer and a common one — a muted microphone, a loud room, a press
    /// that caught nothing. It is said as itself rather than as a failure, because there is
    /// nothing here for anybody to fix.
    /// </summary>
    [Fact]
    public void Silence_is_said_as_silence_rather_than_as_a_fault()
    {
        var cut = Workspace(new Hears(string.Empty));

        PressTheMicrophone(cut);
        PressTheMicrophone(cut);

        cut.WaitForAssertion(
            () => Assert.Contains("Nothing was said", cut.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// With nothing that can hear, it says that — rather than quoting a status code from a
    /// server that was never there, which is what the desktop head did every single time.
    /// </summary>
    [Fact]
    public void And_a_device_that_cannot_hear_says_so_rather_than_quoting_a_status_code()
    {
        var cut = Workspace(ears: null);

        PressTheMicrophone(cut);
        PressTheMicrophone(cut);

        cut.WaitForAssertion(
            () =>
            {
                Assert.Contains("can listen", cut.Markup, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("404", cut.Markup, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A runtime that is not ready is not ears. Half-configured speech must not be preferred
    /// over a server that would have worked.
    /// </summary>
    [Fact]
    public void And_a_runtime_that_is_not_ready_is_not_used()
    {
        var cut = Workspace(new Hears("should not be reached", ready: false));

        PressTheMicrophone(cut);
        PressTheMicrophone(cut);

        cut.WaitForAssertion(
            () => Assert.DoesNotContain("should not be reached", cut.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }
}
