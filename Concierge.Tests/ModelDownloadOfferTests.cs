using Concierge.Ai;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Tests;

/// <summary>
/// That a runtime which has declined to download on its own gives a surface enough to offer
/// the download to a person. Without this the consent gate would simply mean the app never works.
/// </summary>
public sealed class ModelDownloadOfferTests : IDisposable
{
    private readonly string _modelsDirectory =
        Path.Combine(Path.GetTempPath(), $"concierge-offer-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_modelsDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void The_on_device_runtime_can_be_asked_about_a_pending_download()
    {
        Assert.IsAssignableFrom<IModelDownloadRequired>(NewRuntime());
    }

    [Fact]
    public void Nothing_is_pending_before_loading_has_run()
    {
        Assert.Null(((IModelDownloadRequired)NewRuntime()).PendingDownload);
    }

    [Fact]
    public async Task After_loading_the_pending_download_names_the_model_and_its_size()
    {
        var runtime = NewRuntime();

        await runtime.LoadAsync(CancellationToken.None);

        var pending = ((IModelDownloadRequired)runtime).PendingDownload;
        Assert.NotNull(pending);
        Assert.False(string.IsNullOrWhiteSpace(pending!.ModelId));
        Assert.True(pending.Bytes > 0);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task The_size_is_reported_in_the_units_a_person_decides_in()
    {
        var runtime = NewRuntime();
        await runtime.LoadAsync(CancellationToken.None);

        var pending = ((IModelDownloadRequired)runtime).PendingDownload!;

        Assert.Equal(pending.Bytes / 1024d / 1024d / 1024d, pending.Gigabytes, 6);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task A_runtime_told_to_download_on_its_own_offers_nothing_to_confirm()
    {
        // Nothing to ask about when the decision was already made in configuration.
        var runtime = new CircleAiChatRuntime(
            NullLogger<CircleAiChatRuntime>.Instance,
            new CircleAiChatOptions { ModelsDirectory = _modelsDirectory, AllowAutomaticDownload = true });

        Assert.Null(((IModelDownloadRequired)runtime).PendingDownload);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Accepting_nothing_is_refused_rather_than_guessed_at()
    {
        var runtime = NewRuntime();

        Assert.False(await ((IModelDownloadRequired)runtime).AcceptDownloadAsync());
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task An_accepted_download_that_is_cancelled_stops_and_says_so()
    {
        var runtime = NewRuntime();
        await runtime.LoadAsync(CancellationToken.None);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var accepted = await ((IModelDownloadRequired)runtime).AcceptDownloadAsync(null, cancelled.Token);

        Assert.False(accepted);
        Assert.False(runtime.IsReady);
        await runtime.DisposeAsync();
    }

    private CircleAiChatRuntime NewRuntime() => new(
        NullLogger<CircleAiChatRuntime>.Instance,
        new CircleAiChatOptions { ModelsDirectory = _modelsDirectory });
}
