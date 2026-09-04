using CircleAI.Inference;
using Concierge.Ai;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Tests;

/// <summary>
/// That a model already on the device is used rather than asked for again.
/// </summary>
/// <remarks>
/// CircleAI's selector always reports RequiresDownload: true and says so in a comment —
/// "selector cannot tell — caller checks the cache". Concierge is that caller and was not
/// checking, so the app asked for the same 21 GB on every launch, including the launch right
/// after the download finished. It would never have come up ready even once.
/// </remarks>
public sealed class ModelAlreadyOnDeviceTests
{
    [Fact]
    public async Task A_model_on_the_device_is_loaded_without_asking()
    {
        var runtime = NewRuntime(alreadyHere: true);

        await runtime.LoadAsync(CancellationToken.None);

        Assert.True(runtime.IsReady);
        Assert.Null(((IModelDownloadRequired)runtime).PendingDownload);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task A_model_on_the_device_can_answer()
    {
        var runtime = NewRuntime(alreadyHere: true);
        await runtime.LoadAsync(CancellationToken.None);

        var chunks = new List<string>();
        await foreach (var chunk in runtime.StreamAsync([new ChatTurn("user", "hello")]))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("ok", string.Concat(chunks));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task A_model_that_is_not_there_is_still_asked_about()
    {
        var runtime = NewRuntime(alreadyHere: false);

        await runtime.LoadAsync(CancellationToken.None);

        Assert.False(runtime.IsReady);
        Assert.NotNull(((IModelDownloadRequired)runtime).PendingDownload);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Not_being_able_to_tell_means_asking()
    {
        // The safe side of this question: a needless prompt costs a click, a needless 21 GB
        // download costs somebody's month.
        var runtime = NewRuntime(_ => throw new IOException("the disk is not readable"));

        await runtime.LoadAsync(CancellationToken.None);

        Assert.NotNull(((IModelDownloadRequired)runtime).PendingDownload);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Presence_is_asked_about_the_model_that_was_selected()
    {
        var asked = new List<string>();
        var runtime = NewRuntime(modelId => { asked.Add(modelId); return false; });

        await runtime.LoadAsync(CancellationToken.None);

        // Assert.Single here was written when loading asked about exactly one model. It does
        // not any more, and that is the point of the feature rather than a regression: when
        // the best fit is not on disk, every candidate is checked to find one that is, so
        // somebody with a usable model already downloaded comes up ready instead of being
        // asked to wait an hour. Fifteen cheap presence checks buy that.
        //
        // What still has to hold is the thing the name claims: the selected model is the one
        // asked about first, before the search for a fallback begins.
        Assert.Equal(runtime.Selected!.ModelId, asked[0]);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task A_stray_accept_does_not_take_a_working_engine_offline()
    {
        // There is nothing to fetch once the model is loaded, so accepting is refused — but
        // refusing is not the same as failing, and it must not report the engine as down.
        var runtime = NewRuntime(alreadyHere: true);
        await runtime.LoadAsync(CancellationToken.None);

        Assert.False(await runtime.AcceptDownloadAsync());

        Assert.True(runtime.IsReady);
        Assert.Contains("Ready", runtime.StatusMessage, StringComparison.OrdinalIgnoreCase);
        await runtime.DisposeAsync();
    }

    private static CircleAiChatRuntime NewRuntime(bool alreadyHere) => NewRuntime(_ => alreadyHere);

    private static CircleAiChatRuntime NewRuntime(Func<string, bool> presence) => new(
        NullLogger<CircleAiChatRuntime>.Instance,
        new CircleAiChatOptions
        {
            ModelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-present-{Guid.NewGuid():N}"),
            ModelPresence = presence,
            ModelFetcher = (modelId, _, _) => Task.FromResult($"/models/{modelId}"),
            GeneratorFactory = _ => new StubGenerator(),
        });

    private sealed class StubGenerator : IChatGenerator
    {
        public void Dispose()
        {
        }

        public Task<string> GenerateAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            CancellationToken ct = default)
            => Task.FromResult("ok");

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return "ok";
        }
    }
}

/// <summary>
/// That a device with a usable model on it comes up working, rather than waiting on a better one.
/// </summary>
/// <remarks>
/// Best fit is a judgement about quality. Quality is worth nothing to somebody who cannot use
/// the app at all, and on this desktop "best fit" is a 21 GB download — an hour on a good
/// connection — while a model that answers in 261 ms sits on the disk already.
/// </remarks>
public sealed class StartWithWhatIsHereTests
{
    [Fact]
    public async Task A_device_with_any_usable_model_comes_up_ready()
    {
        var runtime = NewRuntime(present: modelId => modelId.Contains("0.6B", StringComparison.Ordinal));

        await runtime.LoadAsync(CancellationToken.None);

        Assert.True(runtime.IsReady);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task It_says_which_model_it_actually_loaded()
    {
        // Not the one selection wanted. Showing the bigger name over the smaller model is how
        // somebody ends up filing a bug about quality against a model they never ran.
        var runtime = NewRuntime(present: modelId => modelId.Contains("0.6B", StringComparison.Ordinal));

        await runtime.LoadAsync(CancellationToken.None);

        Assert.Contains("0.6B", runtime.EngineLabel, StringComparison.Ordinal);
        Assert.Contains("0.6B", runtime.StatusMessage, StringComparison.Ordinal);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task The_better_model_is_still_offered()
    {
        var runtime = NewRuntime(present: modelId => modelId.Contains("0.6B", StringComparison.Ordinal));

        await runtime.LoadAsync(CancellationToken.None);

        var pending = ((IModelDownloadRequired)runtime).PendingDownload;
        Assert.NotNull(pending);
        Assert.DoesNotContain("0.6B", pending!.ModelId, StringComparison.Ordinal);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task A_device_with_nothing_on_it_still_has_to_ask()
    {
        var runtime = NewRuntime(present: _ => false);

        await runtime.LoadAsync(CancellationToken.None);

        Assert.False(runtime.IsReady);
        Assert.NotNull(((IModelDownloadRequired)runtime).PendingDownload);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task A_model_it_started_with_can_answer()
    {
        var runtime = NewRuntime(present: modelId => modelId.Contains("0.6B", StringComparison.Ordinal));
        await runtime.LoadAsync(CancellationToken.None);

        var chunks = new List<string>();
        await foreach (var chunk in runtime.StreamAsync([new ChatTurn("user", "hello")]))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("ok", string.Concat(chunks));
        await runtime.DisposeAsync();
    }

    private static CircleAiChatRuntime NewRuntime(Func<string, bool> present) => new(
        NullLogger<CircleAiChatRuntime>.Instance,
        new CircleAiChatOptions
        {
            ModelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-here-{Guid.NewGuid():N}"),
            ModelPresence = present,
            ModelFetcher = (modelId, _, _) => Task.FromResult($"/models/{modelId}"),
            GeneratorFactory = _ => new Stub(),
        });

    private sealed class Stub : IChatGenerator
    {
        public void Dispose()
        {
        }

        public Task<string> GenerateAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            CancellationToken ct = default)
            => Task.FromResult("ok");

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return "ok";
        }
    }
}
