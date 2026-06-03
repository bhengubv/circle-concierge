using Concierge.Shared.Telemetry;
using Concierge.Web.Hosting;

namespace Concierge.Tests;

public sealed class MetricsAndHealthTests
{
    [Fact]
    public void Concierge_metrics_exposes_named_counters_under_the_concierge_meter()
    {
        using var metrics = new ConciergeMetrics();

        Assert.Equal(ConciergeMetrics.MeterName, metrics.Meter.Name);
        Assert.NotNull(metrics.ChatRequestsStarted);
        Assert.Equal("concierge.chat.requests.started", metrics.ChatRequestsStarted.Name);
        Assert.NotNull(metrics.ChatRequestsCompleted);
        Assert.NotNull(metrics.ChatRequestsFailed);
        Assert.NotNull(metrics.ChatTokensEmitted);
        Assert.NotNull(metrics.DiagramArtifactsParsed);
        Assert.NotNull(metrics.ToolInvocations);
    }

    [Fact]
    public void Prometheus_snapshot_captures_concierge_meter_counters_and_renders_exposition()
    {
        using var metrics = new ConciergeMetrics();
        using var snapshot = new PrometheusMetricSnapshot();

        metrics.ChatRequestsStarted.Add(2, new KeyValuePair<string, object?>("provider", "openai"));
        metrics.ChatRequestsCompleted.Add(1, new KeyValuePair<string, object?>("provider", "openai"));
        metrics.ToolInvocations.Add(5, new KeyValuePair<string, object?>("tool", "read_file"), new KeyValuePair<string, object?>("outcome", "success"));

        // MeterListener.RecordObservableInstruments isn't required for Counter<T> — flushing
        // happens inline. Read once and verify shape.
        var text = snapshot.Read();

        Assert.Contains("# TYPE concierge_chat_requests_started counter", text, StringComparison.Ordinal);
        Assert.Contains("concierge_chat_requests_started{provider=\"openai\"} 2", text, StringComparison.Ordinal);
        Assert.Contains("concierge_chat_requests_completed{provider=\"openai\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("concierge_tools_invocations{outcome=\"success\",tool=\"read_file\"} 5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Prometheus_snapshot_with_no_observations_returns_a_ready_gauge_so_scrapers_dont_404()
    {
        using var snapshot = new PrometheusMetricSnapshot();

        var text = snapshot.Read();

        Assert.Contains("concierge_metrics_ready", text, StringComparison.Ordinal);
    }
}
