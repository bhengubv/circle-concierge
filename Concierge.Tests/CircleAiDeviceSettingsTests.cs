using Bunit;
using Concierge.Shared.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class CircleAiDeviceSettingsTests : BunitContext
{
    [Fact]
    public void Readiness_and_permission_flags_do_not_turn_unknown_into_ready()
    {
        var readiness = CircleAiSettingsRows.Project("SetupReadiness", [["loading", "Getting ready", "Please wait", "0"]]);
        Assert.Equal("Not ready", readiness[1].Value);
        var unknown = CircleAiSettingsRows.Project("Permissions", [["Microphone", "Hear you", "unknown", "1"]]);
        Assert.Equal("Not reported", Assert.Single(unknown).Value);
        var denied = CircleAiSettingsRows.Project("Permissions", [["Microphone", "Hear you", "0", "1"]]);
        Assert.Equal("Not allowed", Assert.Single(denied).Value);
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
        public Task<bool> RequestConnectionAsync(CancellationToken token) { Requests++; return Task.FromResult(false); }
        public Task<CircleAiSharedServiceSnapshot> InspectAsync(CancellationToken token) => throw new NotSupportedException();
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
        panel.FindAll("button").Single(b => b.TextContent == "Refresh status").Click();
        Assert.Equal(2, gateway.Reads);
    }

    private sealed class FakeGateway : ICircleAiSettingsGateway, ICircleAiVoiceSettingsLink
    {
        public int Reads, Opened, PermissionsOpened, VoiceRequests;
        public Task<CircleAiSettingsReport> ReadAsync(CancellationToken token)
        {
            Reads++;
            return Task.FromResult(new CircleAiSettingsReport("Checked", [
                new("readiness", "Readiness and capabilities", "From service", [new("Conversation", "Not ready")]),
                new("language", "Language and replies", "Managed in Circle AI", [], true),
                new("voice", "Voice and wake", "Managed in Circle AI", [], true),
                new("permissions", "Permissions and setup", "Microphone required", [new("Microphone", "Not allowed")])], true, true));
        }
        public Task OpenCircleAiAsync() { Opened++; return Task.CompletedTask; }
        public Task OpenPermissionsAsync() { PermissionsOpened++; return Task.CompletedTask; }
        public Task<bool> RequestVoiceConnectionAsync(CancellationToken token) { VoiceRequests++; return Task.FromResult(false); }
    }
}
