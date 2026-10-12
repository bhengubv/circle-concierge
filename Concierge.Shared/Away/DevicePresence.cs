namespace Concierge.Away;

public sealed record DeviceHeartbeat(string Id, string Name, string Platform);
public sealed record ConnectedDevice(string Id, string Name, string Platform, DateTimeOffset LastSeen, bool Connected);

/// <summary>The Desktop's shared roster. Online means a recent client heartbeat.</summary>
public sealed class DevicePresenceDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ConnectedDevice> _devices = new();
    public IReadOnlyList<ConnectedDevice> Observe(DeviceHeartbeat heartbeat)
    {
        if (string.IsNullOrWhiteSpace(heartbeat.Id) || heartbeat.Id.Length > 128
            || string.IsNullOrWhiteSpace(heartbeat.Name) || heartbeat.Name.Length > 80
            || string.IsNullOrWhiteSpace(heartbeat.Platform) || heartbeat.Platform.Length > 40)
            throw new ArgumentException("A valid device identity is required.");
        lock (_gate)
        {
            _devices[heartbeat.Id] = new(heartbeat.Id, heartbeat.Name, heartbeat.Platform, DateTimeOffset.UtcNow, true);
            return Snapshot();
        }
    }
    public IReadOnlyList<ConnectedDevice> Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var desktop = new ConnectedDevice("desktop", "Concierge Desktop", "Windows", now, true);
            return new[] { desktop }.Concat(_devices.Values.Select(d => d with
                { Connected = now - d.LastSeen < TimeSpan.FromSeconds(20) }))
                .OrderBy(d => d.Id == "desktop" ? 0 : 1).ThenBy(d => d.Name).ToArray();
        }
    }
}
