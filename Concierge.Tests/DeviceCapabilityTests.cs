using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Devices;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The device as something Concierge can act on.
///
/// Every tool before this worked on files, the web, or a notebook — nothing
/// reached the machine underneath. The shape is borrowed from OpenDroid, which is
/// one agent loop and sixty Android action executors; what could not be borrowed
/// is the assumption underneath it, that there is exactly one kind of device.
/// Concierge runs on five heads, and the same binary has to be truthful on all of
/// them.
///
/// So the two rules tested here are the whole design:
///
///   What this device cannot do is absent, not failing. A model told a tool exists
///   will keep trying it, and a person watching cannot tell "broken" from "not
///   here".
///
///   What acts asks first. That is the thing OpenDroid's README does not describe
///   for sending a message or making a payment, and it is the thing Concierge is
///   built around — so it must survive contact with a catalogue of actions that
///   touch the real world.
/// </summary>
public sealed class DeviceCapabilityTests
{
    // ── What the device can do ────────────────────────────────────────────

    [Fact]
    public void An_available_capability_is_published_to_the_model()
    {
        var source = new DeviceCapabilitySource(
            [new Fake("torch", available: true)], Allowing());

        Assert.Contains(source.Tools, t => t.Name == "torch");
    }

    /// <summary>
    /// The rule the whole design turns on. A desktop has no flashlight, and the
    /// correct thing to do on a desktop is not mention one — not offer it and fail.
    /// </summary>
    [Fact]
    public void What_this_device_cannot_do_is_absent_rather_than_broken()
    {
        var source = new DeviceCapabilitySource(
            [new Fake("torch", available: false)], Allowing());

        Assert.Empty(source.Tools);
    }

    /// <summary>
    /// Asked fresh each time, not cached at start-up: a permission can be granted
    /// while the app is open, and a radio can be switched off between turns.
    /// </summary>
    [Fact]
    public void A_capability_that_arrives_later_arrives_without_a_restart()
    {
        var torch = new Fake("torch", available: false);
        var source = new DeviceCapabilitySource([torch], Allowing());

        Assert.Empty(source.Tools);

        torch.IsAvailable = true;

        Assert.Single(source.Tools);
    }

    [Fact]
    public void A_capability_that_goes_away_stops_being_offered()
    {
        var torch = new Fake("torch", available: true);
        var source = new DeviceCapabilitySource([torch], Allowing());

        torch.IsAvailable = false;

        Assert.Empty(source.Tools);
    }

    /// <summary>
    /// An availability check runs on a real device and can throw for reasons
    /// nobody anticipated — a platform API that raises rather than returning false
    /// on an OS version nobody tested. Unavailable is the safe reading of "I don't
    /// know", and it must cost one capability rather than the whole catalogue.
    /// </summary>
    [Fact]
    public void A_capability_that_throws_when_asked_costs_only_itself()
    {
        var source = new DeviceCapabilitySource(
            [new Exploding(), new Fake("clipboard_read", available: true)], Allowing());

        var tool = Assert.Single(source.Tools);
        Assert.Equal("clipboard_read", tool.Name);
    }

    /// <summary>
    /// A person is owed an explanation for what their device cannot do. The model
    /// never sees this list — silence is right for the catalogue and wrong for the
    /// person asking why Concierge will not text from a laptop.
    /// </summary>
    [Fact]
    public void What_the_device_cannot_do_is_still_visible_to_a_person()
    {
        var source = new DeviceCapabilitySource(
            [new Fake("torch", available: false)], Allowing());

        var declared = Assert.Single(source.Declared);
        Assert.Equal("torch", declared.Capability.Name);
        Assert.False(declared.Available);
    }

    // ── Asking first ──────────────────────────────────────────────────────

    /// <summary>
    /// The property that matters most. A head could forget to ask; this cannot.
    /// </summary>
    [Fact]
    public async Task Anything_that_acts_asks_first()
    {
        var torch = new Fake("torch", available: true, readOnly: false);
        var approver = new Recording(ToolApprovalDecision.Denied);

        var result = await Only(new DeviceCapabilitySource([torch], approver)).InvokeAsync(null);

        Assert.False(result.Success);
        Assert.True(approver.WasAsked);
        Assert.False(torch.WasInvoked);
    }

    [Fact]
    public async Task An_allowed_action_happens()
    {
        var torch = new Fake("torch", available: true, readOnly: false);
        var source = new DeviceCapabilitySource([torch], Allowing());

        var result = await Only(source).InvokeAsync(null);

        Assert.True(result.Success);
        Assert.True(torch.WasInvoked);
    }

    /// <summary>
    /// Reading the battery must never interrupt anybody. Prompting for the
    /// harmless things is how people learn to stop reading the prompts, which is
    /// the one failure that makes every other approval in the product worthless.
    /// </summary>
    [Fact]
    public async Task Reading_something_harmless_never_interrupts_anybody()
    {
        var battery = new Fake("battery_state", available: true, readOnly: true);
        var approver = new Recording(ToolApprovalDecision.Denied);

        var result = await Only(new DeviceCapabilitySource([battery], approver)).InvokeAsync(null);

        Assert.True(result.Success);
        Assert.False(approver.WasAsked);
    }

    /// <summary>
    /// A permission can be withdrawn between the catalogue being built and the
    /// call being made. Saying so beats asking somebody to approve something that
    /// cannot happen.
    /// </summary>
    [Fact]
    public async Task A_capability_lost_since_the_catalogue_was_built_says_so_without_asking()
    {
        var torch = new Fake("torch", available: true, readOnly: false);
        var approver = new Recording(ToolApprovalDecision.Allowed);
        var tool = Only(new DeviceCapabilitySource([torch], approver));

        torch.IsAvailable = false;

        var result = await tool.InvokeAsync(null);

        Assert.False(result.Success);
        Assert.False(approver.WasAsked);
        Assert.False(torch.WasInvoked);
    }

    // ── What the person is shown ──────────────────────────────────────────

    /// <summary>
    /// "Send a message" is not a decision anybody can make. "Send a message to
    /// +27…" is. The arguments have to reach the card, or approving is a formality.
    /// </summary>
    [Fact]
    public async Task The_approval_card_carries_the_arguments()
    {
        var send = new Fake("send_message", available: true, readOnly: false);
        var approver = new Recording(ToolApprovalDecision.Denied);
        var tool = Only(new DeviceCapabilitySource([send], approver));

        await tool.InvokeAsync(JsonNode.Parse("""{ "to": "+27821234567", "body": "on my way" }"""));

        Assert.Contains("+27821234567", approver.Seen!.Summary);
        Assert.Contains("on my way", approver.Seen.Summary);
    }

    /// <summary>
    /// The sentence above Allow and Deny says what happens, in the words a person
    /// would use — not the tool's name and not its description for the model.
    /// </summary>
    [Fact]
    public async Task The_person_is_told_what_it_reaches()
    {
        var send = new Fake("send_message", available: true, readOnly: false);
        var approver = new Recording(ToolApprovalDecision.Denied);

        await Only(new DeviceCapabilitySource([send], approver)).InvokeAsync(null);

        Assert.Equal(send.Reach, approver.Seen!.Detail);
    }

    /// <summary>
    /// An approval card is read at a glance, sometimes on a watch. A pasted essay
    /// as an argument must not push the buttons off the screen.
    /// </summary>
    [Fact]
    public async Task A_very_long_argument_does_not_take_over_the_card()
    {
        var write = new Fake("clipboard_write", available: true, readOnly: false);
        var approver = new Recording(ToolApprovalDecision.Denied);
        var tool = Only(new DeviceCapabilitySource([write], approver));

        var essay = new string('x', 5000);
        await tool.InvokeAsync(new JsonObject { ["text"] = essay });

        Assert.True(approver.Seen!.Summary.Length < 300);
        Assert.Contains("…", approver.Seen.Summary);
    }

    /// <summary>The risk the capability declares is the risk the person is shown.</summary>
    [Fact]
    public async Task The_declared_risk_reaches_the_card()
    {
        var open = new Fake("open_link", available: true, readOnly: false)
        {
            Loudness = ConciergeToolRisk.Medium,
        };

        var approver = new Recording(ToolApprovalDecision.Denied);
        await Only(new DeviceCapabilitySource([open], approver)).InvokeAsync(null);

        Assert.Equal(ConciergeToolRisk.Medium, approver.Seen!.Risk);
    }

    // ── The portable two ──────────────────────────────────────────────────

    /// <summary>
    /// These hold everywhere .NET runs, which is every head there is. If either
    /// ever answers "unavailable", something is wrong with the platform, not the
    /// capability.
    /// </summary>
    [Fact]
    public async Task Concierge_can_always_say_what_it_is_running_on()
    {
        var info = new DeviceInfoCapability();

        Assert.True(info.Available);
        Assert.True(info.IsReadOnly);

        var result = await info.InvokeAsync(null);

        Assert.True(result.Success);
        Assert.Contains("Operating system:", result.Output);
    }

    [Fact]
    public async Task Concierge_can_always_say_whether_it_is_on_a_network()
    {
        var network = new NetworkStateCapability();

        Assert.True(network.Available);
        Assert.True(network.IsReadOnly);
        Assert.True((await network.InvokeAsync(null)).Success);
    }

    // ── Being wired up at all ─────────────────────────────────────────────

    /// <summary>
    /// Building the container is itself a thing that can fail, and this is how it
    /// did: the source was registered with TryAddEnumerable and a factory.
    /// TryAddEnumerable deduplicates on the implementation type, so a factory
    /// gives it nothing to compare and it throws — during composition, before
    /// anything runs. On the desktop head that means the window never appears and
    /// the only trace is a stowed exception inside Microsoft.UI.Xaml.
    ///
    /// Every test above passed while the application would not start. They
    /// constructed the source directly, which is exactly the thing a real host
    /// never does.
    /// </summary>
    [Fact]
    public void The_device_layer_can_actually_be_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalService>(_ => UnavailableToolApprovalService.Instance);
        services.AddConciergeDeviceCapabilities();

        using var provider = services.BuildServiceProvider();

        var sources = provider.GetServices<IAgentToolSource>().ToList();

        var source = Assert.Single(sources);
        Assert.Contains(source.Tools, t => t.Name == "device_info");
        Assert.Contains(source.Tools, t => t.Name == "network_state");
    }

    /// <summary>
    /// And registering twice publishes one catalogue, not two. A model shown every
    /// capability in duplicate has no way to tell which one to call.
    /// </summary>
    [Fact]
    public void Registering_the_device_layer_twice_publishes_one_catalogue()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalService>(_ => UnavailableToolApprovalService.Instance);
        services.AddConciergeDeviceCapabilities();
        services.AddConciergeDeviceCapabilities();

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IAgentToolSource>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IAgentTool Only(DeviceCapabilitySource source) => Assert.Single(source.Tools);

    private static IToolApprovalService Allowing() => new Recording(ToolApprovalDecision.Allowed);

    private sealed class Fake : IDeviceCapability
    {
        public Fake(string name, bool available, bool readOnly = true)
        {
            Name = name;
            IsAvailable = available;
            IsReadOnly = readOnly;
        }

        public string Name { get; }
        public string Description => $"does {Name}";
        public string Reach => $"It reaches whatever {Name} reaches.";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly { get; }
        public ConciergeToolRisk Loudness { get; set; } = ConciergeToolRisk.Low;
        public ConciergeToolRisk Risk => Loudness;
        public bool IsAvailable { get; set; }
        public bool Available => IsAvailable;
        public bool WasInvoked { get; private set; }

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            return Task.FromResult(new AgentToolResult(true, "done", null));
        }
    }

    private sealed class Exploding : IDeviceCapability
    {
        public string Name => "explodes";
        public string Description => "asks a platform that answers badly";
        public string Reach => "nothing";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => true;
        public ConciergeToolRisk Risk => ConciergeToolRisk.Low;

        public bool Available => throw new PlatformNotSupportedException("no such API here");

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolResult(true, string.Empty, null));
    }

    private sealed class Recording : IToolApprovalService
    {
        private readonly ToolApprovalDecision _decision;

        public Recording(ToolApprovalDecision decision) => _decision = decision;

        public bool WasAsked { get; private set; }

        public ToolApprovalRequest? Seen { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            WasAsked = true;
            Seen = request;
            return ValueTask.FromResult(_decision);
        }
    }
}
