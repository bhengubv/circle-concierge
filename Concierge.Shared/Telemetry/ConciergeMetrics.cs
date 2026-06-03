using System.Diagnostics.Metrics;

namespace Concierge.Shared.Telemetry;

/// <summary>
/// Singleton holder for Concierge's <see cref="System.Diagnostics.Metrics.Meter"/>. Counters
/// + histograms registered here surface through any OTel exporter the host wires (Prometheus
/// scraper, OTLP, console) and through the built-in <c>/metrics</c> endpoint that returns
/// a minimal Prometheus-shaped snapshot for hosts that don't run a full OTel collector.
/// </summary>
public sealed class ConciergeMetrics : IDisposable
{
    public const string MeterName = "Concierge";

    private readonly Meter _meter;

    public ConciergeMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        ChatRequestsStarted = _meter.CreateCounter<long>(
            "concierge.chat.requests.started",
            unit: "{request}",
            description: "Number of chat StreamAsync calls invoked, tagged by provider id.");
        ChatRequestsCompleted = _meter.CreateCounter<long>(
            "concierge.chat.requests.completed",
            unit: "{request}",
            description: "Number of chat StreamAsync calls that completed without throwing.");
        ChatRequestsFailed = _meter.CreateCounter<long>(
            "concierge.chat.requests.failed",
            unit: "{request}",
            description: "Number of chat StreamAsync calls that threw or surfaced an engine-error frame.");
        ChatTokensEmitted = _meter.CreateCounter<long>(
            "concierge.chat.tokens.emitted",
            unit: "{chunk}",
            description: "Number of streamed chunks delivered to the UI (one per yielded fragment, not one per LLM token).");
        DiagramArtifactsParsed = _meter.CreateCounter<long>(
            "concierge.diagrams.artifacts.parsed",
            unit: "{artifact}",
            description: "Number of diagram artifacts successfully parsed, tagged by runtime id.");
        ToolInvocations = _meter.CreateCounter<long>(
            "concierge.tools.invocations",
            unit: "{call}",
            description: "Number of agent-tool invocations, tagged by tool name and outcome.");
    }

    public Counter<long> ChatRequestsStarted { get; }
    public Counter<long> ChatRequestsCompleted { get; }
    public Counter<long> ChatRequestsFailed { get; }
    public Counter<long> ChatTokensEmitted { get; }
    public Counter<long> DiagramArtifactsParsed { get; }
    public Counter<long> ToolInvocations { get; }

    public Meter Meter => _meter;

    public void Dispose() => _meter.Dispose();
}
