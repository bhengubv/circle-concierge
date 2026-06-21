using System.Globalization;
using CircleAI.Core;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Networking;

namespace Concierge.Hosting;

/// <summary>
/// MAUI-flavoured <see cref="IDeviceContext"/>. Pulls live signals from
/// <see cref="DeviceInfo"/>, <see cref="Battery"/>, and <see cref="Connectivity"/> so the
/// CircleAI runtime can reason about locale, battery, and network state on the user's device.
/// All access is wrapped in try/catch because some platforms do not expose every sensor.
/// </summary>
public sealed class MauiDeviceContext : CircleAI.Core.IDeviceContext
{
    public string? ActiveAppId => "concierge-maui";

    public string? Locale => CultureInfo.CurrentCulture.Name;

    public string? TimeZoneId => TimeZoneInfo.Local.Id;

    public DateTimeOffset? LocalTime => DateTimeOffset.Now;

    public double? Latitude => null;

    public double? Longitude => null;

    public string? LocationHint => null;

    public float? BatteryLevel => TrySafe(() =>
    {
        var level = Battery.Default.ChargeLevel;
        return level >= 0 ? (float?)level : null;
    });

    public bool? IsCharging => TrySafe(() => Battery.Default.State == BatteryState.Charging);

    public string? NetworkType => TrySafe(() => Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess switch
    {
        NetworkAccess.Internet => "internet",
        NetworkAccess.Local => "local",
        NetworkAccess.ConstrainedInternet => "constrained",
        NetworkAccess.None => "none",
        _ => "unknown"
    });

    public float? CpuUsagePercent => null;

    public long? AvailableMemoryBytes => null;

    public CircleAI.Core.ThermalState? ThermalState => null;

    public long? StorageFreeBytes => null;

    public DateTimeOffset? LastActiveUtc => DateTimeOffset.UtcNow;

    private static T? TrySafe<T>(Func<T> reader)
    {
        try
        {
            return reader();
        }
        catch
        {
            return default;
        }
    }
}
