using System.Globalization;
using CircleAI.Core;
using Microsoft.AspNetCore.Http;

namespace Concierge.Web.Hosting;

/// <summary>
/// HTTP-request-flavoured <see cref="IDeviceContext"/>. Fills locale, timezone, time, and
/// network signals from the active request (via <see cref="IHttpContextAccessor"/>). Registered
/// as a singleton — the accessor flows the per-request <see cref="HttpContext"/> through
/// <c>AsyncLocal</c>, so every property reads fresh data each call.
/// </summary>
public sealed class HttpContextDeviceContext : IDeviceContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextDeviceContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    private HttpContext? Current => _accessor.HttpContext;

    public string? ActiveAppId => "concierge-web";

    public string? Locale => Current?.Request.Headers.AcceptLanguage
        .FirstOrDefault()?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault()?.Trim()
        ?? CultureInfo.CurrentCulture.Name;

    public string? TimeZoneId =>
        Current?.Request.Headers["X-TimeZone"].FirstOrDefault()
        ?? Current?.Request.Headers["Time-Zone"].FirstOrDefault()
        ?? TimeZoneInfo.Local.Id;

    public DateTimeOffset? LocalTime => DateTimeOffset.Now;

    public double? Latitude => null;

    public double? Longitude => null;

    public string? LocationHint => null;

    public float? BatteryLevel => null;

    public bool? IsCharging => null;

    public string? NetworkType => Current is null ? null : "internet";

    public float? CpuUsagePercent => null;

    public long? AvailableMemoryBytes => null;

    public ThermalState? ThermalState => null;

    public long? StorageFreeBytes => null;

    public DateTimeOffset? LastActiveUtc => Current is null ? null : DateTimeOffset.UtcNow;
}
