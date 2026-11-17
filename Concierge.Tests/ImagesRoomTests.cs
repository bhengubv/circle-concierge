using Bunit;
using Concierge.Shared;
using Concierge.Shared.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Forms;

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

    private sealed class Editor : IImageEditingRuntime
    {
        public string Id => "editor";
        public string EngineLabel => "An image editor";
        public bool IsReady => true;
        public string StatusMessage => "Ready";
        public ImageEditRequest? Edited { get; private set; }
        public ImageGenerationRequest? Generated { get; private set; }

        public Task<IReadOnlyList<ImageArtifact>> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Generated = request;
            return Task.FromResult<IReadOnlyList<ImageArtifact>>([
                new(Id, request.Prompt, "image/png", null, [1, 2, 3], DateTimeOffset.UtcNow),
            ]);
        }

        public Task<IReadOnlyList<ImageArtifact>> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
        {
            Edited = request;
            return Task.FromResult<IReadOnlyList<ImageArtifact>>([
                new(Id, request.Prompt, "image/png", null, [4, 5, 6], DateTimeOffset.UtcNow),
            ]);
        }
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

    [Fact]
    public void A_person_can_create_and_see_the_result_in_the_shared_room()
    {
        var editor = new Editor();
        var room = Room(editor);
        room.Find("textarea[aria-label='What to draw']").Input("A blue concert poster");
        room.Find("button.btn-primary").Click();

        Assert.Equal("A blue concert poster", editor.Generated?.Prompt);
        Assert.Contains("data:image/png;base64,AQID", room.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_person_can_upload_and_edit_a_photo_in_the_same_room()
    {
        var editor = new Editor();
        var room = Room(editor);
        room.FindAll("button.seg-opt").Single(button => button.TextContent == "Edit a photo").Click();
        room.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "source.png", contentType: "image/png"));
        room.Find("textarea[aria-label='What to change']").Input("Make it blue");
        room.Find("button.btn-primary").Click();

        Assert.Equal("Make it blue", editor.Edited?.Prompt);
        Assert.Equal([1, 2, 3], Assert.Single(editor.Edited!.References).Bytes);
        Assert.Contains("data:image/png;base64,BAUG", room.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_edit_action_waits_for_a_source_photo()
    {
        var room = Room(new Editor());
        room.FindAll("button.seg-opt").Single(button => button.TextContent == "Edit a photo").Click();
        room.Find("textarea[aria-label='What to change']").Input("Make it blue");

        Assert.True(room.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.Contains("Choose a photo to edit", room.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_provider_response_is_explained_without_claiming_an_image_was_made()
    {
        var room = Room(new Maker(ready: true, why: "Ready"));
        room.Find("textarea[aria-label='What to draw']").Input("A concert poster");
        room.Find("button.btn-primary").Click();

        Assert.Contains("No image was returned", room.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Got 1 image", room.Markup, StringComparison.Ordinal);
    }
}
