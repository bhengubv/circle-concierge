using Concierge.Ai;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

/// <summary>
/// The local model looking at a picture.
///
/// `CircleAI.Inference` 3.3.0 carries an image channel — `ChatMessage.ImageBytes`, fed
/// through `mnn_llm_generate_with_image_stream_ex` by the vision generator — and Concierge
/// used none of it. The image channel on `ChatTurn` has existed since vision input was added
/// and only ever reached the cloud runtimes, so every picture on the device was refused with
/// "this model cannot look at pictures".
///
/// **What is claimed here is narrow on purpose.** A vision model can be loaded and looked at;
/// no vision model is on this machine, and nothing below pretends otherwise. What these check
/// is the wiring and, more importantly, that the claim of being able to see is read off the
/// model rather than off its filename.
/// </summary>
public sealed class LocalVisionTests
{
    [Theory]
    [InlineData("Kimi-VL-A3B-Instruct")]
    [InlineData("qwen2.5-vl-3b")]
    [InlineData("llava-1.5-7b")]
    [InlineData("some-vision-model")]
    public void A_vision_family_is_loaded_through_the_generator_that_can_see(string name)
        => Assert.True(CircleAiChatRuntime.LooksLikeItCanSee(Path.Combine("models", name, "model.mnn")));

    [Theory]
    [InlineData("Qwen2.5-1.5B-Instruct")]
    [InlineData("kimi-k2-chat")]
    public void And_a_text_family_is_not(string name)
        => Assert.False(CircleAiChatRuntime.LooksLikeItCanSee(Path.Combine("models", name, "model.mnn")));

    /// <summary>
    /// The name only decides which generator to build. Whether it can see is the model's own
    /// answer — `IsVisionCapable` reads `mnn_llm_get_model_type` — so a file named like a
    /// vision model that is not one is never advertised as one.
    /// </summary>
    [Fact]
    public void With_no_model_loaded_nothing_can_be_shown_to_it()
    {
        var runtime = new CircleAiChatRuntime(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CircleAiChatRuntime>.Instance,
            new CircleAiChatOptions { ModelFetcher = (_, _, _) => Task.FromResult(string.Empty) });

        Assert.Empty(runtime.SupportedImageMediaTypes);
    }

    /// <summary>
    /// It declares the capability because it can have it, and lists nothing while the model
    /// loaded cannot. So the composer has to read the list rather than only the interface —
    /// which it now does, and this is the test that says why.
    /// </summary>
    [Fact]
    public void It_declares_vision_as_something_it_can_have()
        => Assert.True(typeof(CircleAiChatRuntime).IsAssignableTo(typeof(IVisionCapableRuntime)));

    /// <summary>
    /// An empty list means it cannot see. Anything that decides on the interface alone would
    /// hand a picture to a text model and say nothing about it.
    /// </summary>
    [Fact]
    public void An_empty_list_is_how_a_runtime_says_it_cannot_see_today()
    {
        IChatRuntime runtime = new CircleAiChatRuntime(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CircleAiChatRuntime>.Instance,
            new CircleAiChatOptions { ModelFetcher = (_, _, _) => Task.FromResult(string.Empty) });

        Assert.False(runtime is IVisionCapableRuntime { SupportedImageMediaTypes.Count: > 0 });
    }
}
