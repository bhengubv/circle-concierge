using Bunit;
using Concierge.Shared.Chat;
using Concierge.Shared.Components.Components;
using Microsoft.AspNetCore.Components;

namespace Concierge.Tests;

public sealed class WelcomePanelTests : BunitContext
{
    [Fact]
    public void Empty_chat_reads_groups_and_prompts_from_the_versioned_Circle_AI_catalogue()
    {
        var cut = Render<WelcomePanel>();
        var catalogue = CircleAI.Assistant.Capabilities.All;
        var defaultGroup = catalogue.Single(group => group.Title == "Work and business");

        Assert.Equal("Concierge", cut.Find(".welcome-brand").TextContent.Trim());
        Assert.Equal("Powered by Circle AI", cut.Find(".welcome-engine").TextContent.Trim());
        Assert.Equal(catalogue.Count, cut.FindAll("button.welcome-category").Count);
        Assert.Equal(defaultGroup.Items.Length, cut.FindAll("button.welcome-tile").Count);
        Assert.Equal(typeof(CircleAI.Assistant.Capabilities).Assembly.GetName().Version?.ToString(2),
            cut.Find(".welcome-panel").GetAttribute("data-catalog-version"));
        Assert.Contains("Work and business", cut.Markup);
        Assert.Contains("Learning", cut.Markup);

        var selectedCategory = cut.Find(".welcome-category[aria-pressed='true']");
        Assert.Equal("Work and business", selectedCategory.TextContent.Trim());
        Assert.All(cut.FindAll(".welcome-category[aria-pressed]"), category =>
            Assert.Contains(category.GetAttribute("aria-pressed"), new[] { "true", "false" }));
    }

    [Fact]
    public void Choosing_a_Circle_AI_suggestion_prefills_its_prompt_without_sending()
    {
        string? selected = null;
        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.OnPromptSelected, prompt => selected = prompt));

        cut.FindAll("button.welcome-category").Single(button => button.TextContent.Trim() == "Learning").Click();
        var studyHelp = CircleAI.Assistant.Capabilities.All
            .Single(group => group.Title == "Learning")
            .Items.Single(item => item.Title == "Study help");
        cut.FindAll("button.welcome-tile").Single(button => button.TextContent.Contains("Study help")).Click();

        Assert.Equal(studyHelp.Opener, selected);
    }

    [Fact]
    public void Desktop_welcome_offers_a_direct_plan_action_without_duplicate_destinations()
    {
        var requested = false;
        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.OnPlanRequested, () => requested = true));

        Assert.Empty(cut.FindAll(".welcome-work-paths"));
        cut.Find(".welcome-plan-action").Click();

        Assert.True(requested);
    }

    [Fact]
    public void Runtime_catalogue_is_rendered_as_live_readiness_not_as_a_starter_claim()
    {
        var catalog = new ChatRuntimeCapabilityCatalog(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [
                new("conversation", "Conversation", ChatRuntimeCapabilityState.Available),
                new("tool-use", "Working with tools", ChatRuntimeCapabilityState.Preparing, "Circle AI is preparing this device."),
                new("run", "Run requests", ChatRuntimeCapabilityState.ActionRequired, "The model needs to be prepared first."),
                new("visual-input", "Understanding images", ChatRuntimeCapabilityState.Unavailable, "Not available right now.")
            ],
            new(4.2, 12.5, 8, "Online", null));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, catalog));

        Assert.Equal("1", cut.Find(".welcome-capabilities").GetAttribute("data-catalog-version"));
        Assert.Equal(4, cut.FindAll(".welcome-capability").Count);
        Assert.Equal("available", cut.Find(".welcome-capability[data-state='available']").GetAttribute("data-state"));
        Assert.Contains("Preparing", cut.Markup);
        Assert.Contains("Setup needed", cut.Markup);
        Assert.Contains("Not available right now.", cut.Markup);
    }

    [Fact]
    public void Lazy_service_model_is_not_presented_as_an_already_proven_answer()
    {
        var catalog = new ChatRuntimeCapabilityCatalog(1, "circleai", "loopback schema 1",
            DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Available,
                "Circle AI will load its selected model when you ask.")], null);

        var cut = Render<WelcomePanel>(parameters => parameters.Add(panel => panel.CapabilityCatalog, catalog));
        var capability = cut.Find(".welcome-capability");
        Assert.Contains("Model selected", capability.TextContent);
        Assert.Contains("the conversation will show progress", capability.TextContent);
        Assert.DoesNotContain("Circle AI can answer on this device", capability.TextContent);
    }

    [Fact]
    public void Connected_Desktop_catalogue_is_shown_as_a_separate_device()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "This phone cannot run the available model.")],
            new(0.6, 0.1, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Preparing, "Desktop is preparing.")],
            new(24, 180, 16, "Connected", null));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true));

        var panel = cut.Find(".welcome-capabilities-remote");
        Assert.Equal("AI engine capabilities on connected Desktop", panel.GetAttribute("aria-label"));
        Assert.Contains("AI engine on connected Desktop", panel.TextContent);
        Assert.Contains("24 GB usable memory", panel.TextContent);
        Assert.Contains("Circle AI is getting ready on your Desktop.", panel.TextContent);
        Assert.DoesNotContain("Desktop is preparing.", panel.TextContent);
        Assert.Single(cut.FindAll(".welcome-capabilities[aria-label='AI engine capabilities on this device']"));
        Assert.Equal("Waiting for Circle AI on Desktop", cut.Find(".welcome-setup-guide h3").TextContent.Trim());
        Assert.Contains("Keep Concierge open on both devices", cut.Find(".welcome-setup-guide").TextContent);
        Assert.NotNull(cut.Find(".welcome-setup-progress [role='progressbar']"));
        Assert.Null(cut.Find(".welcome-device-details").GetAttribute("open"));
    }

    [Fact]
    public void Handheld_surfaces_a_Desktop_memory_pause_while_local_Circle_AI_is_ready()
    {
        const string desktopReason = "Circle AI is paused on Desktop because it does not have enough free memory to safely start its worker. Close some apps and try again.";
        var local = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Available)],
            new(2.3, 106.4, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "unreported", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable, desktopReason)],
            new(0.426, null, 8, null, "No safe worker budget is currently available."));
        var refreshCount = 0;

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true)
            .Add(panel => panel.OnRefreshDesktop, () => refreshCount++));

        var guidance = cut.Find(".welcome-setup-guide");
        Assert.Equal("Desktop needs more free memory", guidance.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains(desktopReason, guidance.TextContent);
        Assert.Contains("Circle AI can still answer on this phone", guidance.TextContent);
        Assert.Contains("0.4 GB usable memory", cut.Find(".welcome-capabilities-remote").TextContent);
        Assert.Contains(desktopReason, cut.Find(".welcome-capabilities-remote").TextContent);

        cut.Find(".welcome-desktop-refresh").Click();
        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public void Handheld_shows_Desktop_model_download_while_conversation_is_available()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "No model fits this phone's available resources.")],
            new(0.6, 0.1, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Available)],
            new(24, 180, 16, "Connected", null),
            Activity: new ChatRuntimeActivity("model-download", "Downloading Circle AI", 0.42));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true));

        var progress = cut.Find(".welcome-setup-progress progress");
        Assert.Equal("0.42", progress.GetAttribute("value"));
        Assert.Contains("42%", cut.Find(".welcome-setup-progress").TextContent);
        Assert.Contains("Downloading Circle AI", cut.Find(".welcome-setup-progress").TextContent);
        Assert.Equal("Circle AI is ready on your Desktop", cut.Find(".welcome-setup-guide h3").TextContent.Trim());
        Assert.Contains("Keep Concierge open on your Desktop", cut.Find(".welcome-setup-guide").TextContent);
    }

    [Fact]
    public void Handheld_hides_old_desktop_progress_when_setup_is_required_but_not_running()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "No model fits this phone's available resources.")],
            new(0.6, 0.1, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.ActionRequired,
                "A model download is needed before Circle AI can run.")],
            new(24, 180, 16, "Connected", null),
            Activity: new ChatRuntimeActivity("model-download", "Old progress 1%", 0.01));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true));

        Assert.Equal("Finish Circle AI setup on Desktop", cut.Find(".welcome-setup-guide h3").TextContent.Trim());
        Assert.Empty(cut.FindAll(".welcome-setup-progress"));
        Assert.DoesNotContain("Old progress", cut.Markup);
    }

    [Fact]
    public void Handheld_hides_desktop_progress_from_an_expired_preparing_snapshot()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "No model fits this phone's available resources.")],
            new(0.6, 0.1, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow.AddMinutes(-5),
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Preparing, "Circle AI is preparing.")],
            new(24, 180, 16, "Connected", null),
            Activity: new ChatRuntimeActivity("model-download", "Expired progress 1%", 0.01));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true));

        Assert.Null(cut.Find(".welcome-setup-guide").QuerySelector("progress"));
        Assert.DoesNotContain("Expired progress", cut.Markup);
    }

    [Fact]
    public void Handheld_explains_how_to_finish_setup_on_a_connected_desktop_without_starting_a_download()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "No model fits this phone's available resources.")],
            new(0.6, 0.1, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.ActionRequired,
                "Qwen3-8B-MNN needs a 5.1 GB download before it can run.")],
            new(24, 180, 16, "Connected", null));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true));

        var guide = cut.Find(".welcome-setup-guide");
        Assert.Equal("Finish Circle AI setup on Desktop", guide.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("A 5.1 GB Circle AI download is needed on Desktop", guide.TextContent);
        Assert.Contains("Download on this device", guide.TextContent);
        Assert.Contains("show Desktop's download progress here", guide.TextContent);
        Assert.Contains("when Desktop says Ready", guide.TextContent);
        Assert.Contains("No phone download is currently offered", guide.TextContent);
        Assert.DoesNotContain("Qwen", guide.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Handheld_prioritizes_its_own_download_offer_when_both_devices_need_setup()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.ActionRequired,
                "A model download is needed before Circle AI can run on this phone.")],
            new(2.9, 108.3, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.ActionRequired,
                "A model download is needed before Circle AI can run on Desktop.")],
            new(24, 180, 16, "Connected", null));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true));

        var guide = cut.Find(".welcome-setup-guide");
        Assert.Equal("Circle AI needs setup on this phone", guide.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("prepare Circle AI for this phone", guide.TextContent);
        Assert.Contains("Desktop setup and progress are shown separately", guide.TextContent);
        Assert.DoesNotContain("No phone download is currently offered", guide.TextContent);
        Assert.DoesNotContain("Nothing downloads to this phone", guide.TextContent);
    }

    [Fact]
    public void Handheld_distinguishes_a_desktop_startup_failure_from_a_phone_resource_limit()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "No Circle AI model fits this device's available resources.")],
            new(0.6, 0.1, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "Circle AI stopped while preparing. Restart Concierge to try again.")],
            new(28.9, 1300, 8, "Online", null),
            Activity: new ChatRuntimeActivity("model-download", "embeddings_bf16.bin 1%", 0.01));

        var refreshed = false;
        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.OnRefreshDesktop, EventCallback.Factory.Create(this, () =>
            {
                refreshed = true;
                return Task.CompletedTask;
            }))
            .Add(panel => panel.DesktopSearchMessage, "Desktop is still connected, but Circle AI is not ready.")
            .Add(panel => panel.IsHandheld, true));

        var guide = cut.Find(".welcome-setup-guide");
        Assert.Equal("Circle AI stopped on your Desktop", guide.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("will not retry automatically", guide.TextContent);
        Assert.Contains("Close and reopen Concierge on Desktop", guide.TextContent);
        Assert.Contains("Refresh Desktop status", guide.TextContent);
        Assert.Contains("This phone will not download a model", guide.TextContent);
        Assert.Contains("Desktop is still connected, but Circle AI is not ready.", guide.TextContent);
        Assert.DoesNotContain("not have enough resources", guide.TextContent);
        Assert.DoesNotContain("Qwen", guide.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll(".welcome-setup-progress"));
        cut.Find(".welcome-desktop-refresh").Click();
        Assert.True(refreshed);
    }

    [Fact]
    public void Handheld_explains_when_neither_phone_nor_desktop_has_enough_memory()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "No Circle AI model fits this device's available resources.")],
            new(0.5, 0.1, 8, "Online", null));
        var desktop = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "No model fits this device's available resources.")],
            new(0.3, 1200, 8, "Online", null));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local)
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true));

        var guide = cut.Find(".welcome-setup-guide");
        Assert.Equal("Desktop needs more available memory", guide.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("0.3 GB available memory", guide.TextContent);
        Assert.Contains("Free memory on Desktop", guide.TextContent);
        Assert.Contains("Refresh Desktop status", guide.TextContent);
        Assert.Contains("will not download a model", guide.TextContent);
        Assert.DoesNotContain("Start Concierge on your Desktop", guide.TextContent);
    }

    [Fact]
    public void Capability_explanations_do_not_expose_internal_model_names()
    {
        var desktop = new ChatRuntimeCapabilityCatalog(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [
                new("conversation", "Conversation", ChatRuntimeCapabilityState.Preparing,
                    "Loading Qwen3.6-35B-A3B-MNN into memory (one-time)…"),
                new("tool-use", "Working with tools", ChatRuntimeCapabilityState.ActionRequired,
                    "Qwen3-30B-A3B-MNN needs a 16.5 GB download before it can run.")
            ],
            new(24, 180, 16, "Connected", null));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.DesktopCapabilityCatalog, desktop)
            .Add(panel => panel.IsHandheld, true));

        var text = cut.Find(".welcome-panel").TextContent;
        Assert.Contains("Circle AI is getting ready on your Desktop.", text);
        Assert.Contains("A 16.5 GB Circle AI download is needed before this is ready.", text);
        Assert.False(text.Contains("Qwen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Desktop_setup_guidance_points_to_its_local_setup_action()
    {
        var local = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.ActionRequired,
                "Circle AI needs a download before it can answer.")],
            new(19.3, 1325.6, 8, "Online", null));

        var cut = Render<WelcomePanel>(parameters => parameters
            .Add(panel => panel.CapabilityCatalog, local));

        var guide = cut.Find(".welcome-setup-guide");
        Assert.Equal("Circle AI needs setup on this Desktop", guide.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("Download on this device", guide.TextContent);
        Assert.DoesNotContain("connect a ready Desktop", guide.TextContent, StringComparison.OrdinalIgnoreCase);
    }
}
