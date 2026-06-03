using System.Text;
using System.Diagnostics.Metrics;
using Concierge.Shared.Chat;
using Concierge.Shared.Diagrams;
using Concierge.Shared.Telemetry;

namespace Concierge.Web.Hosting;

/// <summary>
/// Plain-text <c>/healthz</c> and a minimal <c>/metrics</c> endpoint that surfaces the
/// Concierge meter as Prometheus exposition format. Production deployments behind an OTel
/// collector should set <see cref="MeterListener"/> via OpenTelemetry — the inline
/// implementation here is a no-dependencies fallback so a single-box dev or staging deploy
/// has metrics out of the box.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapConciergeHealth(this IEndpointRouteBuilder app)
    {
        // /healthz — a single line that flips to NOT_READY if no chat runtime is ready.
        app.MapGet("/healthz", (IEnumerable<IChatRuntime> chatRuntimes, IEnumerable<IDiagramRuntime> diagramRuntimes) =>
        {
            var anyChatReady = chatRuntimes.Any(r => r.IsReady);
            var anyDiagramReady = diagramRuntimes.Any(r => r.IsReady);
            var status = anyChatReady && anyDiagramReady ? "READY" : "DEGRADED";
            var lines = new List<string>
            {
                $"status: {status}",
                $"chat-runtimes: {chatRuntimes.Count()}",
                $"chat-runtimes-ready: {chatRuntimes.Count(r => r.IsReady)}",
                $"diagram-runtimes: {diagramRuntimes.Count()}",
                $"diagram-runtimes-ready: {diagramRuntimes.Count(r => r.IsReady)}",
                $"timestamp-utc: {DateTimeOffset.UtcNow:O}",
            };
            return Results.Text(string.Join('\n', lines), "text/plain", statusCode: anyChatReady ? 200 : 503);
        }).WithName("Healthz").ExcludeFromDescription();

        // /metrics — Prometheus exposition. Aggregates every observation since process start.
        app.MapGet("/metrics", (PrometheusMetricSnapshot snapshot) => Results.Text(snapshot.Read(), "text/plain; version=0.0.4"))
            .WithName("Metrics").ExcludeFromDescription();

        return app;
    }
}

/// <summary>
/// In-memory MeterListener that accumulates counter values for the Concierge meter and
/// renders them as Prometheus exposition. Single-host-only — production deployments should
/// run a real OTel collector, but this gives a useful default with zero infrastructure.
/// </summary>
public sealed class PrometheusMetricSnapshot : IDisposable
{
    private readonly MeterListener _listener;
    private readonly Dictionary<string, Dictionary<string, long>> _counters = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PrometheusMetricSnapshot()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ConciergeMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var tagKey = BuildTagKey(tags);
        lock (_gate)
        {
            if (!_counters.TryGetValue(instrument.Name, out var bucket))
            {
                bucket = new Dictionary<string, long>(StringComparer.Ordinal);
                _counters[instrument.Name] = bucket;
            }
            bucket[tagKey] = bucket.GetValueOrDefault(tagKey) + value;
        }
    }

    public string Read()
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (var (name, bucket) in _counters.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                var promName = name.Replace('.', '_');
                sb.Append("# TYPE ").Append(promName).Append(" counter\n");
                foreach (var (tagKey, value) in bucket.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
                {
                    sb.Append(promName);
                    if (tagKey.Length > 0)
                    {
                        sb.Append('{').Append(tagKey).Append('}');
                    }
                    sb.Append(' ').Append(value).Append('\n');
                }
            }
        }
        if (sb.Length == 0)
        {
            sb.Append("# TYPE concierge_metrics_ready gauge\n");
            sb.Append("concierge_metrics_ready 1\n");
        }
        return sb.ToString();
    }

    private static string BuildTagKey(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return string.Empty;
        }
        var parts = new List<string>(tags.Length);
        foreach (var tag in tags)
        {
            parts.Add($"{tag.Key}=\"{tag.Value}\"");
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(',', parts);
    }

    public void Dispose() => _listener.Dispose();
}
