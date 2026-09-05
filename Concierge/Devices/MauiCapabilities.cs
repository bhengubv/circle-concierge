using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Devices;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Devices;

/// <summary>
/// What the device can do, written once for every head that ships.
///
/// This file is the point of the whole exercise. OpenDroid's power is sixty
/// Android action executors behind one agent loop, and the obvious way to copy it
/// is to write sixty Windows ones, then sixty Android ones, and watch them drift.
/// MAUI Essentials already abstracts the ones that matter across Windows, Android,
/// iOS and Mac Catalyst, so these are written once and are true on all four.
///
/// Every one of them answers <see cref="IDeviceCapability.Available"/> honestly
/// rather than assuming. A desktop has no flashlight; a machine with no default
/// mail client cannot compose an email; a watch may have no browser to open a link
/// in. None of those are errors — they are facts about the device, and the model
/// is simply not told about them. The whole reason the capability interface has an
/// availability check is that this file cannot know which head it is running on.
///
/// The riskier things a phone can do — sending a message, placing a call, paying
/// someone — are deliberately not here yet. They are the ones where "it asks first"
/// has to be exactly right, and they belong with the Android head where they can be
/// tested on a device rather than asserted from Windows.
/// </summary>
public static class MauiDeviceCapabilities
{
    /// <summary>
    /// Adds every capability MAUI can offer on the current head.
    ///
    /// Called after AddConciergeDeviceCapabilities, which registers the portable
    /// two and the source that publishes whatever is registered. Order does not
    /// matter to DI; it matters to the reader, which is why they are adjacent.
    /// </summary>
    public static IServiceCollection AddMauiDeviceCapabilities(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceCapability, ClipboardReadCapability>();
        services.AddSingleton<IDeviceCapability, ClipboardWriteCapability>();
        services.AddSingleton<IDeviceCapability, BatteryCapability>();
        services.AddSingleton<IDeviceCapability, ConnectivityCapability>();
        services.AddSingleton<IDeviceCapability, OpenLinkCapability>();
        services.AddSingleton<IDeviceCapability, FlashlightCapability>();

        return services;
    }
}

/// <summary>
/// Reading the clipboard.
///
/// Read-only and therefore unprompted, which deserves a word. The clipboard is
/// the one read-only thing on this list that can hold something private — a
/// password on its way to a password field. It is here because "look at what I
/// just copied" is one of the genuinely useful things to be able to say to an
/// assistant, and because the alternative is the person pasting it into the
/// composer, where it lands in the conversation permanently instead of being read
/// once. If that trade ever looks wrong, this is the capability to make ask.
/// </summary>
internal sealed class ClipboardReadCapability : IDeviceCapability
{
    public string Name => "clipboard_read";

    public string Description => "Read the text currently on the clipboard.";

    public string Reach => "It reads what you last copied. Nothing leaves the device.";

    public JsonNode? ArgumentsSchema => null;

    public bool IsReadOnly => true;

    public ConciergeToolRisk Risk => ConciergeToolRisk.Low;

    public bool Available => Clipboard.Default.HasText;

    public async Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var text = await Clipboard.Default.GetTextAsync().ConfigureAwait(false);

        return string.IsNullOrEmpty(text)
            ? new AgentToolResult(true, "The clipboard has no text on it.", null)
            : new AgentToolResult(true, text, null);
    }
}

/// <summary>
/// Putting something on the clipboard.
///
/// Asks, because it destroys something: whatever you had copied is gone, and it
/// is gone from the one place people keep a thing they were about to paste.
/// </summary>
internal sealed class ClipboardWriteCapability : IDeviceCapability
{
    public string Name => "clipboard_write";

    public string Description => "Put text on the clipboard, ready to paste.";

    public string Reach => "It replaces whatever you currently have copied.";

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": { "text": { "type": "string", "description": "What to put on the clipboard." } },
          "required": ["text"] }
        """);

    public bool IsReadOnly => false;

    public ConciergeToolRisk Risk => ConciergeToolRisk.Low;

    public bool Available => true;

    public async Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var text = arguments?["text"]?.GetValue<string>();
        if (text is null)
        {
            return new AgentToolResult(false, string.Empty, "Argument 'text' is required.");
        }

        await Clipboard.Default.SetTextAsync(text).ConfigureAwait(false);

        return new AgentToolResult(true, "Copied.", null);
    }
}

/// <summary>
/// How much battery is left, and whether it is charging.
///
/// The reason an assistant wants this is not curiosity: it is whether to start
/// something long. A model about to run a twenty-minute job on a phone at 4% is
/// making a decision it currently cannot inform.
/// </summary>
internal sealed class BatteryCapability : IDeviceCapability
{
    public string Name => "battery_state";

    public string Description => "Report the battery level and whether the device is charging.";

    public string Reach => "It reads the battery level. Nothing leaves the device.";

    public JsonNode? ArgumentsSchema => null;

    public bool IsReadOnly => true;

    public ConciergeToolRisk Risk => ConciergeToolRisk.Low;

    /// <summary>
    /// A desktop with no battery reports Unknown, and saying "0%, unknown state"
    /// would be worse than not offering the capability at all — a model told the
    /// battery is empty may refuse to do the work it was asked for.
    /// </summary>
    public bool Available => Battery.Default.State != BatteryState.Unknown;

    public Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var percent = (int)Math.Round(Battery.Default.ChargeLevel * 100);
        var state = Battery.Default.State switch
        {
            BatteryState.Charging => "charging",
            BatteryState.Full => "full",
            BatteryState.Discharging => "on battery",
            BatteryState.NotCharging => "not charging",
            BatteryState.NotPresent => "no battery",
            _ => "unknown",
        };

        return Task.FromResult(new AgentToolResult(true, $"Battery {percent}%, {state}.", null));
    }
}

/// <summary>
/// What kind of network this is, which is not the same question as whether there
/// is one.
///
/// The portable network_state capability answers "is anything reachable" from
/// plain .NET. This answers "over what", which is the one that decides whether to
/// download a model on a metered connection — a decision worth not making for
/// somebody silently.
/// </summary>
internal sealed class ConnectivityCapability : IDeviceCapability
{
    public string Name => "connection_type";

    public string Description => "Say how this device is connected — WiFi, mobile data, ethernet, or not at all.";

    public string Reach => "It reads the connection type. Nothing is sent anywhere.";

    public JsonNode? ArgumentsSchema => null;

    public bool IsReadOnly => true;

    public ConciergeToolRisk Risk => ConciergeToolRisk.Low;

    public bool Available => true;

    public Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var access = Connectivity.Current.NetworkAccess switch
        {
            NetworkAccess.Internet => "connected to the internet",
            NetworkAccess.Local => "on a local network only",
            NetworkAccess.ConstrainedInternet => "behind a captive portal",
            NetworkAccess.None => "not connected",
            _ => "of unknown connectivity",
        };

        var profiles = Connectivity.Current.ConnectionProfiles.ToList();
        var over = profiles.Count == 0
            ? string.Empty
            : $" over {string.Join(" and ", profiles.Select(p => p.ToString().ToLowerInvariant()))}";

        return Task.FromResult(new AgentToolResult(true, $"This device is {access}{over}.", null));
    }
}

/// <summary>
/// Opening a link in whatever the device uses for links.
///
/// Asks, and the reason is worth stating: it is the one capability here that hands
/// something over to software Concierge does not control. A URL a model chose,
/// opened without being seen, is the shape of every phishing click that ever
/// worked — so the address goes on the approval card, and a person reads it before
/// anything opens.
/// </summary>
internal sealed class OpenLinkCapability : IDeviceCapability
{
    public string Name => "open_link";

    public string Description => "Open a web address in the device's browser.";

    public string Reach => "It opens the address in your browser, outside Concierge.";

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": { "url": { "type": "string", "description": "Absolute http or https address." } },
          "required": ["url"] }
        """);

    public bool IsReadOnly => false;

    /// <summary>
    /// Medium rather than low. It changes nothing on the device and it puts
    /// something in front of a person, which is a bigger deal than it sounds.
    /// </summary>
    public ConciergeToolRisk Risk => ConciergeToolRisk.Medium;

    public bool Available => true;

    public async Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var url = arguments?["url"]?.GetValue<string>();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            // Only http and https. A file:// or intent:// address handed to the
            // system launcher is a way to reach things nobody agreed to, and the
            // approval card would have shown a URL rather than what it does.
            return new AgentToolResult(false, string.Empty, "Give an absolute http or https address.");
        }

        var opened = await Launcher.Default.TryOpenAsync(address).ConfigureAwait(false);

        return opened
            ? new AgentToolResult(true, $"Opened {address}.", null)
            : new AgentToolResult(false, string.Empty, "Nothing on this device could open that.");
    }
}

/// <summary>
/// The torch.
///
/// Here because it is the clearest example of the rule the whole design turns on:
/// it exists on a phone and not on a desktop, the same binary runs on both, and
/// the only correct thing to do on the desktop is not mention it. Nobody has to
/// write a Windows version that returns "not supported" — Available says no and
/// the model never hears about it.
/// </summary>
internal sealed class FlashlightCapability : IDeviceCapability
{
    public string Name => "flashlight";

    public string Description => "Turn the device's flashlight on or off.";

    public string Reach => "It switches the light on the back of your device.";

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": { "on": { "type": "boolean", "description": "True to switch on, false to switch off." } },
          "required": ["on"] }
        """);

    public bool IsReadOnly => false;

    public ConciergeToolRisk Risk => ConciergeToolRisk.Low;

    /// <summary>
    /// There is no ask-if-there-is-a-torch API, so this asks whether the platform
    /// is one that has them. Getting it wrong costs a capability, not a crash: an
    /// unavailable one is simply never published.
    /// </summary>
    public bool Available =>
        DeviceInfo.Current.Platform == DevicePlatform.Android
        || DeviceInfo.Current.Platform == DevicePlatform.iOS;

    public async Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var on = arguments?["on"]?.GetValue<bool>() ?? true;

        try
        {
            if (on)
            {
                await Flashlight.Default.TurnOnAsync().ConfigureAwait(false);
                return new AgentToolResult(true, "Flashlight on.", null);
            }

            await Flashlight.Default.TurnOffAsync().ConfigureAwait(false);
            return new AgentToolResult(true, "Flashlight off.", null);
        }
        catch (PermissionException)
        {
            // The camera permission is the flashlight permission, and a person who
            // said no to it said no for a reason.
            return new AgentToolResult(false, string.Empty, "Concierge has not been given camera permission.");
        }
        catch (FeatureNotSupportedException)
        {
            return new AgentToolResult(false, string.Empty, "This device has no flashlight.");
        }
    }
}
