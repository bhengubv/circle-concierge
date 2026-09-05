using System.Text.Json;
using Concierge.Chat.Cloud;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

/// <summary>
/// Pictures, on the wire.
///
/// Concierge had an image channel and nothing that could see: the model that
/// ships runs on the device and is text-only, so a turn carrying a picture was
/// told plainly that this model cannot look at one. The three cloud runtimes
/// are backed by models that can, and none of them declared it or knew how to
/// send one.
///
/// Every provider takes images differently, so what is tested here is the
/// shape. Nothing calls out — a key is the user's to supply — and that limit is
/// worth stating rather than hiding behind a mock that always agrees.
/// </summary>
public sealed class VisionContentTests
{
    private static ChatTurn WithPicture() => new("user", "What is this?", new[]
    {
        new ChatImage("photo.jpg", "image/jpeg", new byte[] { 1, 2, 3, 4 }),
    });

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// A turn with no pictures keeps its plain string content. A provider given
    /// a one-element list instead still works, but every existing conversation
    /// would change shape on the wire for no reason.
    /// </summary>
    [Fact]
    public void A_turn_without_pictures_stays_a_plain_string()
    {
        var turn = new ChatTurn("user", "just words");

        Assert.Equal("just words", VisionContent.AnthropicContent(turn));
        Assert.Equal("just words", VisionContent.OpenAiContent(turn));
    }

    [Fact]
    public void Anthropic_gets_base64_with_a_media_type()
    {
        var json = Json(VisionContent.AnthropicContent(WithPicture()));

        Assert.Contains("\"type\":\"image\"", json);
        Assert.Contains("\"media_type\":\"image/jpeg\"", json);
        Assert.Contains(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), json);
    }

    [Fact]
    public void OpenAi_gets_a_data_uri()
    {
        var json = Json(VisionContent.OpenAiContent(WithPicture()));

        Assert.Contains("\"type\":\"image_url\"", json);
        Assert.Contains("data:image/jpeg;base64,", json);
    }

    [Fact]
    public void Gemini_gets_inline_data()
    {
        var json = Json(VisionContent.GeminiParts(WithPicture()));

        Assert.Contains("inline_data", json);
        Assert.Contains("\"mime_type\":\"image/jpeg\"", json);
    }

    /// <summary>
    /// The question goes after the pictures. A model reading in order should
    /// see what it is being asked about before being asked about it.
    /// </summary>
    [Fact]
    public void The_question_comes_after_the_pictures()
    {
        var json = Json(VisionContent.AnthropicContent(WithPicture()));

        Assert.True(json.IndexOf("image", StringComparison.Ordinal)
                    < json.IndexOf("What is this?", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_pictures_all_travel()
    {
        var turn = new ChatTurn("user", "compare these", new[]
        {
            new ChatImage("a.png", "image/png", new byte[] { 1 }),
            new ChatImage("b.png", "image/png", new byte[] { 2 }),
        });

        var json = Json(VisionContent.OpenAiContent(turn));

        Assert.Equal(2, json.Split("\"type\":\"image_url\"").Length - 1);
    }

    /// <summary>
    /// Gemini takes parts even with no picture, so a text-only turn still has
    /// to produce one.
    /// </summary>
    [Fact]
    public void Gemini_still_gets_a_part_for_a_text_only_turn()
    {
        var parts = VisionContent.GeminiParts(new ChatTurn("user", "just words"));

        Assert.Single(parts);
        Assert.Contains("just words", Json(parts));
    }

    /// <summary>
    /// The three agree on these four, which is why the workspace can offer
    /// them without knowing which model will answer.
    /// </summary>
    [Fact]
    public void The_supported_formats_are_the_ones_every_provider_takes()
    {
        Assert.Contains("image/jpeg", VisionContent.Supported);
        Assert.Contains("image/png", VisionContent.Supported);
        Assert.Contains("image/gif", VisionContent.Supported);
        Assert.Contains("image/webp", VisionContent.Supported);
    }

    /// <summary>
    /// Declared, so the workspace can check rather than assume. Without this
    /// the check added with the image channel had nothing to find, and every
    /// picture was refused with "this model cannot look at pictures".
    /// </summary>
    [Fact]
    public void All_three_cloud_runtimes_say_they_can_see()
    {
        Assert.True(typeof(AnthropicChatRuntime).IsAssignableTo(typeof(IVisionCapableRuntime)));
        Assert.True(typeof(OpenAiChatRuntime).IsAssignableTo(typeof(IVisionCapableRuntime)));
        Assert.True(typeof(GeminiChatRuntime).IsAssignableTo(typeof(IVisionCapableRuntime)));
    }
}
