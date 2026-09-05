using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Devices;

/// <summary>
/// What Concierge can say about the machine without asking a platform.
///
/// These two live in the shared library rather than in a head because they are
/// true everywhere .NET runs, which is every head there is. Everything that needs
/// a platform — clipboard, battery, notifications, opening a link, sending a
/// message — belongs to a head, because that is where the honest answer to "can
/// this device do that" lives.
///
/// Both are read-only and neither asks. A model that has to interrupt somebody to
/// find out which operating system it is on will either stop asking or teach the
/// person to stop reading the prompts, and the second is the failure that makes
/// every other approval in the product worthless.
/// </summary>
public sealed class DeviceInfoCapability : IDeviceCapability
{
    public string Name => "device_info";

    public string Description =>
        "Describe the device Concierge is running on: operating system, version and architecture.";

    public string Reach => "It reads the operating system name and version. Nothing leaves the device.";

    public JsonNode? ArgumentsSchema => null;

    public bool IsReadOnly => true;

    public ConciergeToolRisk Risk => ConciergeToolRisk.Low;

    /// <summary>Always. There is no device on which .NET cannot describe itself.</summary>
    public bool Available => true;

    public Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var lines = new[]
        {
            $"Operating system: {RuntimeInformation.OSDescription}",
            $"Architecture: {RuntimeInformation.OSArchitecture}",
            $"Process architecture: {RuntimeInformation.ProcessArchitecture}",
            $"Runtime: {RuntimeInformation.FrameworkDescription}",
            $"Processors: {Environment.ProcessorCount}",
        };

        return Task.FromResult(new AgentToolResult(true, string.Join('\n', lines), null));
    }
}

/// <summary>
/// Whether this device is on a network.
///
/// Worth having as its own capability rather than folding into device_info,
/// because it is the one that changes. An assistant that cannot reach the web
/// should be able to find out why before it tries three times, and "there is no
/// network" is a better answer to give a person than three failed fetches.
///
/// It reports reachability, not what is on the other side. Whether a particular
/// site answers is web_fetch's business, and that one asks first because it
/// leaves the device — this does not leave it at all.
/// </summary>
public sealed class NetworkStateCapability : IDeviceCapability
{
    public string Name => "network_state";

    public string Description => "Say whether this device currently has a network connection.";

    public string Reach => "It checks whether a network is reachable. Nothing is sent anywhere.";

    public JsonNode? ArgumentsSchema => null;

    public bool IsReadOnly => true;

    public ConciergeToolRisk Risk => ConciergeToolRisk.Low;

    public bool Available => true;

    public Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        bool up;
        try
        {
            up = NetworkInterface.GetIsNetworkAvailable();
        }
        catch (NetworkInformationException)
        {
            // A platform that will not answer is not the same as a platform that
            // is offline, and saying "no network" when the truth is "cannot tell"
            // would send the model down the wrong path.
            return Task.FromResult(new AgentToolResult(
                true, "Cannot tell whether this device is on a network.", null));
        }

        return Task.FromResult(new AgentToolResult(
            true,
            up ? "This device has a network connection." : "This device has no network connection.",
            null));
    }
}
