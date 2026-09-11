using Bunit;
using Concierge.Shared;
using Concierge.Shared.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The Images room, when it cannot make a picture.
///
/// **Found by describing a picture and pressing the button.** The button is correctly dead —
/// no API key, nothing can draw — and the line underneath said *"Pick a runtime + describe an
/// image"*, which is what it said before anybody did anything and what it went on saying
/// afterwards. Somebody who had picked one and described an image was being told to do both
/// again, while the real answer — that provider needs a key — sat unsaid on the runtime
/// itself.
///
/// A dead control with the wrong explanation beside it is worse than a dead control, because
/// it sends somebody to fix the thing that is not broken.
/// </summary>
public sealed class ImagesRoomTests : BunitContext
{
    private sealed class Maker(bool ready, string why, string id = "openai-images") : IImageRuntime
    {
        public string Id => id;
        public string EngineLabel => "A Maker";
        public bool IsReady => ready;
        public string StatusMessage => why;

        public Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
            ImageGenerationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ImageArtifact>>([]);
    }

    private IRenderedComponent<Concierge.Shared.Components.Pages.Images> Room(params IImageRuntime[] runtimes)
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        Services.AddSingleton<IEnumerable<IImageRuntime>>(_ => runtimes);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Concierge.Shared.Components.Pages.Images>();
    }

    /// <summary>
    /// The runtime knows why it cannot draw and says so in a sentence. The room hands that
    /// sentence on rather than inventing a second explanation beside it.
    /// </summary>
    [Fact]
    public void It_says_what_is_actually_missing()
    {
        var room = Room(new Maker(ready: false, why: "OpenAI API key not configured."));

        Assert.Contains("OpenAI API key not configured.", room.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void And_stops_telling_people_to_do_what_they_have_already_done()
    {
        var room = Room(new Maker(ready: false, why: "Needs a key."));

        Assert.DoesNotContain("Pick a runtime + describe an image", room.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing installed at all is answered by the room itself rather than by the line under
    /// the button. Asserted here because the first version of this test expected a sentence
    /// of mine that nobody could ever have seen — the room answers first.
    /// </summary>
    [Fact]
    public void With_nothing_at_all_the_room_says_so_before_the_button_exists()
    {
        var room = Room();

        Assert.Contains("No image runtime is set up", room.Markup, StringComparison.Ordinal);
        Assert.Empty(room.FindAll("button.btn-primary"));
    }

    /// <summary>
    /// And where the only thing missing really is the description, it asks for that — the
    /// original message was not wrong, it was only ever right for one of three reasons.
    /// </summary>
    [Fact]
    public void And_asks_for_a_description_when_that_is_the_missing_part()
    {
        var room = Room(new Maker(ready: true, why: "Ready"));

        Assert.Contains("Describe the picture you want", room.Markup, StringComparison.Ordinal);
    }
}
