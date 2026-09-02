using Concierge.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Tests;

/// <summary>
/// That the engine never fetches a model without being asked.
/// </summary>
/// <remarks>
/// The registry's desktop-tier bundle is 22.8 GB — measured, not estimated. Loading used to
/// start that download the moment the app opened: on a P30 Lite over wifi that is the better
/// part of an hour, and on mobile data it is somebody's month. It also hung the test suite,
/// which is how it was found — the runner reported "test host process crashed" and the real
/// cause was a unit test quietly pulling twenty-two gigabytes.
/// </remarks>
public sealed class ModelDownloadConsentTests : IDisposable
{
    private readonly string _modelsDirectory =
        Path.Combine(Path.GetTempPath(), $"concierge-consent-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_modelsDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory, and it may never have been created.
        }
    }

    [Fact]
    public async Task Loading_does_not_download_a_model_that_is_not_there()
    {
        // The assertion is the clock: a download of this size cannot finish in seconds, so
        // returning quickly is proof none was started.
        var runtime = NewRuntime();

        var started = DateTimeOffset.UtcNow;
        await runtime.LoadAsync(CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"load took {elapsed} — something was fetched");
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Loading_leaves_nothing_on_disk_when_it_declines_to_fetch()
    {
        var runtime = NewRuntime();

        await runtime.LoadAsync(CancellationToken.None);

        var downloaded = Directory.Exists(_modelsDirectory)
            ? Directory.GetFiles(_modelsDirectory, "*", SearchOption.AllDirectories).Length
            : 0;
        Assert.Equal(0, downloaded);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task The_engine_is_not_ready_while_the_model_is_missing()
    {
        var runtime = NewRuntime();

        await runtime.LoadAsync(CancellationToken.None);

        Assert.False(runtime.IsReady);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task The_person_is_told_what_the_download_would_cost()
    {
        // "Not ready" is not enough to decide on. The size is the whole decision.
        var runtime = NewRuntime();

        await runtime.LoadAsync(CancellationToken.None);

        Assert.Contains("GB", runtime.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("download", runtime.StatusMessage, StringComparison.OrdinalIgnoreCase);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task What_was_selected_is_reported_so_a_host_can_offer_it()
    {
        var runtime = NewRuntime();

        await runtime.LoadAsync(CancellationToken.None);

        Assert.NotNull(runtime.Selected);
        Assert.True(runtime.Selected!.RequiresDownload);
        Assert.True(runtime.Selected.EstimatedBytes > 0);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Streaming_without_a_model_says_so_rather_than_hanging()
    {
        var runtime = NewRuntime();
        await runtime.LoadAsync(CancellationToken.None);

        var chunks = new List<string>();
        await foreach (var chunk in runtime.StreamAsync([new Shared.Chat.ChatTurn("user", "hello")]))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Contains("download", chunks[0], StringComparison.OrdinalIgnoreCase);
        await runtime.DisposeAsync();
    }


    private CircleAiChatRuntime NewRuntime() => new(
        NullLogger<CircleAiChatRuntime>.Instance,
        new CircleAiChatOptions
        {
            ModelsDirectory = _modelsDirectory,
            // The default, stated here because it is the point of these tests.
            AllowAutomaticDownload = false,
        });
}
