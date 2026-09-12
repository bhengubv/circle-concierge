using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A photograph in a room.
///
/// **Both renderers drew a picture in a room as a text label.** The CSS one wrote the alt
/// text flat on the floor; the engine sent it over as kind "sign" and did the same. So
/// paperclipping a floor plan into a room produced the words "holiday.png" lying on the
/// ground and no picture at all — and nothing said so.
///
/// `design_plan` has drawn exactly this properly the whole time. It needed a picture to work
/// on, which only the paperclip can supply, and only a model could ask for it. Item 17's
/// "trace over a photo" was ticked and unreachable by a person.
/// </summary>
public sealed class TracingOverAPhotoTests
{
    private const string APicture =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static DesignDocument ARoomWithAPlan()
        => DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(
                DesignNodeKind.Solid,
                null,
                ("shape", "plan"),
                ("text", "The plan"),
                ("src", APicture),
                ("width", "400"),
                ("depth", "400")));

    /// <summary>
    /// The fallback renderer draws the picture, not a box with a caption on it. This is the
    /// one that matters most: it is what a machine with no WebGL shows, and the whole
    /// ordering argument for this surface is that the fallback is the one that has to work.
    /// </summary>
    [Fact]
    public void A_plan_lies_flat_with_the_picture_on_it()
    {
        var html = DesignMediums.Render(ARoomWithAPlan());

        Assert.Contains("class=\"plan", html, StringComparison.Ordinal);
        Assert.Contains("background-image", html, StringComparison.Ordinal);
        Assert.Contains(APicture, html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Flat, rather than standing up in front of the walls somebody is drawing over it. A
    /// solid where a tracing sheet should be is not merely uglier — it hides the work.
    /// </summary>
    [Fact]
    public void And_it_is_laid_down_rather_than_stood_up()
    {
        var html = DesignMediums.Render(ARoomWithAPlan());

        Assert.Contains("rotateX(90deg)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The plan</span>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source that is not a picture this understands is refused the same way every other
    /// picture on this surface is: a model can write any string into a src, and a file://
    /// address would pull something off the machine into a document that may be shared.
    /// </summary>
    [Fact]
    public void And_a_source_that_is_not_safe_draws_no_picture()
    {
        var dodgy = DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(
                DesignNodeKind.Solid,
                null,
                ("shape", "plan"),
                ("src", "file:///C:/Users/someone/secrets.png"),
                ("width", "400"),
                ("depth", "400")));

        var html = DesignMediums.Render(dodgy);

        Assert.DoesNotContain("secrets.png", html, StringComparison.Ordinal);
        Assert.Contains("class=\"plan", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary solid is untouched — the new branch must not capture everything with a
    /// shape on it.
    /// </summary>
    [Fact]
    public void And_an_ordinary_block_is_still_a_block()
    {
        var room = DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(DesignNodeKind.Solid, null, ("shape", "sphere"), ("text", "A ball")));

        var html = DesignMediums.Render(room);

        Assert.Contains("class=\"solid", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"plan", html, StringComparison.Ordinal);
    }
}
