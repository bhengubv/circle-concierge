using Bunit;
using Concierge.Shared.Chat;
using Concierge.Shared.Components.Components;

namespace Concierge.Tests;

public sealed class DownloadOfferSummaryTests : BunitContext
{
    [Fact]
    public void Staged_offer_distinguishes_total_download_from_selected_model_size()
    {
        var offer = new PendingModelDownload(
            "Qwen3-14B-MNN",
            Bytes(8.8),
            FitsThisDevice: true)
        {
            StagingBytes = Bytes(30.7),
            StagedModelCount = 5,
        };

        var cut = Render<DownloadOfferSummary>(parameters => parameters
            .Add(component => component.Download, offer)
            .Add(component => component.DeviceName, "phone"));

        Assert.Contains("30.7 GB across 5 compatible models, smallest first", cut.Markup);
        Assert.Contains("selected model is about 8.8 GB", cut.Markup);
        Assert.Contains("answer as soon as the first model is ready", cut.Markup);
    }

    [Fact]
    public void Ready_local_chat_is_not_described_as_waiting_for_first_model()
    {
        var offer = new PendingModelDownload("Qwen3-4B-MNN", Bytes(5.1), FitsThisDevice: true)
        {
            StagingBytes = Bytes(21.5),
            StagedModelCount = 10,
        };

        var cut = Render<DownloadOfferSummary>(parameters => parameters
            .Add(component => component.Download, offer)
            .Add(component => component.LocalCanAnswer, true)
            .Add(component => component.DeviceName, "device"));

        Assert.Contains("Circle AI can already handle conversations on this device", cut.Markup);
        Assert.Contains("Prepare about 21.5 GB across 10 more compatible models", cut.Markup);
        Assert.Contains("while you keep working", cut.Markup);
        Assert.Contains("Circle AI selected an offline upgrade of about 5.1 GB", cut.Markup);
        Assert.DoesNotContain("answer as soon as the first model is ready", cut.Markup);
    }

    [Fact]
    public void Ready_desktop_copy_explains_what_is_needed_for_phone_offline_use()
    {
        var offer = new PendingModelDownload(
            "Qwen3-14B-MNN",
            Bytes(8.8),
            FitsThisDevice: true)
        {
            StagingBytes = Bytes(30.7),
            StagedModelCount = 5,
        };

        var cut = Render<DownloadOfferSummary>(parameters => parameters
            .Add(component => component.Download, offer)
            .Add(component => component.DesktopConnected, true)
            .Add(component => component.DesktopCanAnswer, true)
            .Add(component => component.DeviceName, "phone"));

        Assert.Contains("Circle AI can answer on your Desktop", cut.Markup);
        Assert.Contains("30.7 GB here to use this phone offline", cut.Markup);
        Assert.Contains("selected model is about 8.8 GB", cut.Markup);
    }

    [Fact]
    public void Connected_but_unready_desktop_is_not_described_as_ready()
    {
        var offer = new PendingModelDownload(
            "Qwen3-14B-MNN",
            Bytes(8.8),
            FitsThisDevice: true)
        {
            StagingBytes = Bytes(30.7),
            StagedModelCount = 5,
        };

        var cut = Render<DownloadOfferSummary>(parameters => parameters
            .Add(component => component.Download, offer)
            .Add(component => component.DesktopConnected, true)
            .Add(component => component.DesktopCanAnswer, false)
            .Add(component => component.DeviceName, "phone"));

        Assert.Contains("Desktop is connected, but Circle AI is not ready there yet", cut.Markup);
        Assert.Contains("30.7 GB on this phone across 5 compatible models, smallest first", cut.Markup);
        Assert.DoesNotContain("Circle AI can answer on your Desktop", cut.Markup);
        Assert.DoesNotContain("use this phone offline", cut.Markup);
    }

    private static long Bytes(double gigabytes)
        => (long)(gigabytes * 1024 * 1024 * 1024);
}
