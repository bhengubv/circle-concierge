using Bunit;
using Concierge.Shared.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class CircleAiDeviceSettingsTests : BunitContext
{
    [Theory]
    [InlineData("On", true)]
    [InlineData("Ready", false)]
    [InlineData("Available", false)]
    [InlineData("TooBig", false)]
    [InlineData("unknown", false)]
    public void Text_readiness_requires_service_reported_answering_On(string state, bool expected)
        => Assert.Equal(expected, CircleAiServiceReadiness.TextAnsweringIsOn([["Answering", "Answers questions", state, "", "/chat"]]));

    [Fact]
    public void Other_or_malformed_abilities_do_not_enable_text_chat()
    {
        Assert.False(CircleAiServiceReadiness.TextAnsweringIsOn([["Talking", "Speaks", "On", "", "/talk"]]));
        Assert.False(CircleAiServiceReadiness.TextAnsweringIsOn([["Answering", "", "On"]]));
        Assert.False(CircleAiServiceReadiness.TextAnsweringIsOn([]));
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("0", 0.0)]
    [InlineData("1", 1.0)]
    [InlineData("1.1", null)]
    [InlineData("-0.1", null)]
    [InlineData("NaN", null)]
    [InlineData("unknown", null)]
    public void Progress_only_uses_valid_service_fractions(string value, double? expected)
        => Assert.Equal(expected, CircleAiSetupProgress.ParseFraction(value));

    [Fact]
    public void Setup_is_not_started_by_opening_settings_and_service_progress_is_rendered()
    {
        var gateway = new SetupGateway();
        Services.AddSingleton<ICircleAiSettingsGateway>(gateway);
        var panel = Render<Concierge.Shared.Components.Components.CircleAiDeviceSettings>();
        panel.WaitForAssertion(() => Assert.Equal("0.25", panel.Find("progress").GetAttribute("value")));
        Assert.Equal(0, gateway.Starts);
        panel.FindAll("button").Single(b => b.TextContent == "Prepare Circle AI on this device").Click();
        panel.WaitForAssertion(() => Assert.Equal(1, gateway.Starts));
        Assert.Contains("accepted setup", panel.Markup);
        Assert.True(panel.FindAll("button").Single(b => b.TextContent == "Prepare Circle AI on this device").HasAttribute("disabled"));
    }

    [Fact]
    public void Failed_setup_is_not_reported_as_accepted()
    {
        var gateway = new SetupGateway { Fail = true };
        Services.AddSingleton<ICircleAiSettingsGateway>(gateway);
        var panel = Render<Concierge.Shared.Components.Components.CircleAiDeviceSettings>();
        panel.FindAll("button").Single(b => b.TextContent == "Prepare Circle AI on this device").Click();
        panel.WaitForAssertion(() => Assert.Contains("Setup could not be confirmed", panel.Markup));
        Assert.DoesNotContain("accepted setup", panel.Markup);
    }

    [Fact]
    public void Reopening_settings_during_service_setup_does_not_offer_to_restart_it()
    {
        var gateway = new SetupGateway { InProgress = true };
        Services.AddSingleton<ICircleAiSettingsGateway>(gateway);
        var panel = Render<Concierge.Shared.Components.Components.CircleAiDeviceSettings>();
        panel.WaitForAssertion(() => Assert.True(panel.FindAll("button").Single(b => b.TextContent == "Prepare Circle AI on this device").HasAttribute("disabled")));
        Assert.Equal(0, gateway.Starts);
    }

    [Fact]
    public void Approved_connection_refreshes_reports_and_displays_verified_connection()
    {
        var gateway = new FakeGateway();
        var link = new FakeConnection();
        Services.AddSingleton<ICircleAiSettingsGateway>(gateway);
        Services.AddSingleton<ICircleAiSharedService>(link);
        var panel = Render<Concierge.Shared.Components.Components.CircleAiDeviceSettings>();
        link.Approved = true;
        panel.FindAll("button").Single(b => b.TextContent == "Connect Circle AI").Click();
        panel.WaitForAssertion(() => Assert.Contains("Connected to Circle AI service", panel.Markup));
        Assert.Contains("Reconnect Circle AI", panel.Markup);
        Assert.Contains("Checked at", panel.Markup);
        Assert.Contains("Conversation: Not ready", panel.Markup);
        Assert.Equal(2, gateway.Reads);
    }

    [Fact]
    public void Connection_in_flight_has_its_own_visible_busy_label()
    {
        var gateway = new FakeGateway();
        var link = new FakeConnection { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Services.AddSingleton<ICircleAiSettingsGateway>(gateway);
        Services.AddSingleton<ICircleAiSharedService>(link);
        var panel = Render<Concierge.Shared.Components.Components.CircleAiDeviceSettings>();
        panel.FindAll("button").Single(b => b.TextContent == "Connect Circle AI").Click();
        panel.WaitForAssertion(() => Assert.True(panel.FindAll("button").Single(b => b.TextContent == "Connecting…").HasAttribute("disabled")));
        Assert.Contains("Waiting for Circle AI connection approval", panel.Markup);
        link.Pending.SetResult(false);
        panel.WaitForAssertion(() => Assert.Contains("connection was not approved", panel.Markup));
    }

    private sealed class SetupGateway : ICircleAiSettingsGateway, ICircleAiServiceSetup
    {
        public int Starts;
        public bool Fail;
        public bool InProgress;
        public Task<CircleAiSettingsReport> ReadAsync(CancellationToken token) => Task.FromResult(
            new CircleAiSettingsReport("Checked", [new("readiness", "Readiness", "Service report", [])], false, false));
        public Task StartSetupAsync(CancellationToken token)
        {
            Starts++;
            return Fail ? Task.FromException(new InvalidOperationException("Service refused")) : Task.CompletedTask;
        }
        public Task<CircleAiSetupProgress> ReadSetupProgressAsync(CancellationToken token)
            => Task.FromResult(new CircleAiSetupProgress(false, "Preparing", "The brain", 0.25, "Downloading", InProgress));
        public Task OpenCircleAiAsync() => throw new NotSupportedException();
        public Task OpenPermissionsAsync() => throw new NotSupportedException();
    }

    [Fact]
    public void Readiness_and_permission_flags_do_not_turn_unknown_into_ready()
    {
        var readiness = CircleAiSettingsRows.Project("SetupReadiness", [["loading", "Getting ready", "Please wait", "0"]]);
        Assert.Equal("Not ready", readiness[1].Value);
        var unknown = CircleAiSettingsRows.Project("Permissions", [["Microphone", "Hear you", "unknown", "1"]]);
        Assert.Equal("Not reported", Assert.Single(unknown).Value);
        var denied = CircleAiSettingsRows.Project("Permissions", [["Microphone", "Hear you", "0", "1"]]);
        Assert.Equal("Not allowed", Assert.Single(denied).Value);
        Assert.Equal("Missing", Assert.Single(CircleAiSettingsRows.Project("SetupCensus", [["Voice", "0", "123", "Still needed", "0"]])).Value);
        Assert.Equal("Present", Assert.Single(CircleAiSettingsRows.Project("SetupCensus", [["Brain", "1", "123", "Installed", "123"]])).Value);
        Assert.Equal("Not reported", Assert.Single(CircleAiSettingsRows.Project("SetupCensus", [["Brain", "unknown", "123", "", "0"]])).Value);
    }

    [Fact]
    public void Malformed_or_empty_service_rows_never_claim_readiness()
    {
        Assert.Equal("Unrecognized response", Assert.Single(CircleAiSettingsRows.Project("Abilities", [["voice"]])).Value);
        Assert.Equal("No details reported", Assert.Single(CircleAiSettingsRows.Project("ResidentStatus", [])).Value);
    }

    [Fact]
    public void Ability_state_and_formatted_storage_are_preserved_from_service()
    {
        Assert.Equal("supported", Assert.Single(CircleAiSettingsRows.Project("Abilities", [["Voice", "Speak", "supported", "12", "local"]])).Value);
        Assert.Equal("23.1 GB", Assert.Single(CircleAiSettingsRows.Project("Footprint", [["Models", "23.1 GB", "0"]])).Value);
        Assert.Equal("Not reported", Assert.Single(CircleAiSettingsRows.Project("Footprint", [["Models", "", "0"]])).Value);
    }

    [Fact]
    public void Unlinked_device_can_open_connection_and_decline_without_a_false_success()
    {
        Services.AddSingleton<ICircleAiSettingsGateway>(new FakeGateway());
        var link = new FakeConnection();
        Services.AddSingleton<ICircleAiSharedService>(link);
        var panel = Render<Concierge.Shared.Components.Components.CircleAiDeviceSettings>();
        panel.FindAll("button").Single(b => b.TextContent == "Connect Circle AI").Click();
        panel.WaitForAssertion(() => Assert.Contains("Circle AI connection was not approved", panel.Markup));
        Assert.Equal(1, link.Requests);
    }

    private sealed class FakeConnection : ICircleAiSharedService, ICircleAiSharedServiceLink
    {
        public int Requests;
        public bool Approved;
        public TaskCompletionSource<bool>? Pending;
        public Task<bool> RequestConnectionAsync(CancellationToken token) { Requests++; return Pending?.Task ?? Task.FromResult(Approved); }
        public Task<CircleAiSharedServiceSnapshot> InspectAsync(CancellationToken token)
            => Task.FromResult(new CircleAiSharedServiceSnapshot("service", "0.1", 58, true, Approved, "Checked", []));
    }

    [Fact]
    public void Declining_voice_connection_does_not_claim_authorization()
    {
        var gateway = new FakeGateway();
        Services.AddSingleton<ICircleAiSettingsGateway>(gateway);
        var panel = Render<Concierge.Shared.Components.Components.CircleAiDeviceSettings>();
        panel.FindAll("button").Single(b => b.TextContent.Contains("Voice and wake")).Click();
        panel.FindAll("button").Single(b => b.TextContent == "Connect voice settings").Click();
        panel.WaitForAssertion(() => Assert.Contains("Voice connection was not approved", panel.Markup));
        Assert.Equal(1, gateway.VoiceRequests);
        Assert.Equal(1, gateway.Reads);
    }

    [Fact]
    public void Actual_report_is_rendered_and_owner_buttons_are_functional()
    {
        var gateway = new FakeGateway();
        Services.AddSingleton<ICircleAiSettingsGateway>(gateway);
        var panel = Render<Concierge.Shared.Components.Components.CircleAiDeviceSettings>();
        panel.WaitForAssertion(() => Assert.Contains("Not ready", panel.Markup));
        Assert.Empty(panel.FindAll("input, select")); // No fabricated local service preferences.
        panel.FindAll("button").Single(b => b.TextContent.Contains("Language and replies")).Click();
        panel.FindAll("button").Single(b => b.TextContent == "Open Circle AI controls").Click();
        Assert.Equal(1, gateway.Opened);
        panel.FindAll("button").Single(b => b.TextContent.Contains("Permissions and setup")).Click();
        Assert.Contains("Not allowed", panel.Markup);
        panel.FindAll("button").Single(b => b.TextContent == "Open service permissions").Click();
        Assert.Equal(1, gateway.PermissionsOpened);
        var invalidationsBeforeRefresh = gateway.Invalidations;
        panel.FindAll("button").Single(b => b.TextContent == "Refresh status").Click();
        Assert.Equal(2, gateway.Reads);
        Assert.Equal(invalidationsBeforeRefresh + 1, gateway.Invalidations);
    }

    private sealed class FakeGateway : ICircleAiSettingsGateway, ICircleAiVoiceSettingsLink, ICircleAiSettingsCacheControl
    {
        public int Reads, Opened, PermissionsOpened, VoiceRequests;
        public int Invalidations;
        public void InvalidateSettings() => Invalidations++;
        public Task<CircleAiSettingsReport> ReadAsync(CancellationToken token)
        {
            Reads++;
            return Task.FromResult(new CircleAiSettingsReport("Checked", [
                new("readiness", "Readiness and capabilities", "From service", [new("Conversation readiness", "Not ready")]),
                new("language", "Language and replies", "Managed in Circle AI", [], true),
                new("voice", "Voice and wake", "Managed in Circle AI", [], true),
                new("permissions", "Permissions and setup", "Microphone required", [new("Microphone", "Not allowed")])], true, true));
        }
        public Task OpenCircleAiAsync() { Opened++; return Task.CompletedTask; }
        public Task OpenPermissionsAsync() { PermissionsOpened++; return Task.CompletedTask; }
        public Task<bool> RequestVoiceConnectionAsync(CancellationToken token) { VoiceRequests++; return Task.FromResult(false); }
    }
}
